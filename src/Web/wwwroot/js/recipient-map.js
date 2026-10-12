// The recipient's door on a map, for the booking form. The merchant drops the pin by searching, by clicking the map
// or by finding the typed address, drags it to correct it, or pastes the location the customer sent (a Google Maps
// link or coordinates, read by the server, which also opens maps.app.goo.gl short links). The pin is two hidden fields;
// without a Google Maps key there is no map, and the pasted link is the way to pin.
(() => {
    const box = document.querySelector("[data-pin]");
    if (!box) {
        return;
    }

    const form = box.closest("form");
    const latitude = box.querySelector("[data-pin-lat]");
    const longitude = box.querySelector("[data-pin-lng]");
    const status = box.querySelector("[data-pin-status]");
    const clear = box.querySelector("[data-pin-clear]");
    const link = box.querySelector("[data-pin-link]");
    const use = box.querySelector("[data-pin-use]");
    const open = box.querySelector("[data-pin-open]");
    const area = form.querySelector("[name='Input.AreaId']");
    const address = form.querySelector("[name='Input.RecipientAddress']");
    let map;
    let marker;
    let geocoder;

    // "Mirpur 10, Dhaka, Bangladesh" from the chosen area and its city
    const areaText = () => {
        const option = area?.selectedOptions[0];
        if (!option || !option.value) {
            return "";
        }

        return [option.textContent.replace(" (suburb)", "").trim(), option.parentElement.label, "Bangladesh"].filter(Boolean).join(", ");
    };
    const addressText = () => [address.value.trim(), areaText() || "Bangladesh"].filter(Boolean).join(", ");

    const say = (text, problem) => {
        status.replaceChildren();
        status.classList.toggle("problem", Boolean(problem));
        if (text) {
            status.append(text);
            return;
        }

        if (latitude.value && longitude.value) {
            const check = document.createElement("a");
            check.href = `https://www.google.com/maps/search/?api=1&query=${latitude.value},${longitude.value}`;
            check.target = "_blank";
            check.rel = "noopener";
            check.textContent = "check it on Google Maps";
            status.append(`Pinned at ${latitude.value}, ${longitude.value} · `, check);
        } else {
            status.append("Not pinned: the rider finds the door by the address.");
        }
    };

    const pinned = () => Boolean(latitude.value && longitude.value);
    const refresh = () => {
        clear.hidden = !pinned();
        box.classList.toggle("pinned", pinned());
        if (open) {
            open.href = "https://www.google.com/maps/search/?api=1&query=" + encodeURIComponent(addressText());
        }
    };

    const setPin = (lat, lng, pan = true) => {
        latitude.value = Number(lat).toFixed(6);
        longitude.value = Number(lng).toFixed(6);
        if (map) {
            const at = { lat: Number(latitude.value), lng: Number(longitude.value) };
            marker.setPosition(at);
            marker.setVisible(true);
            if (pan) {
                map.panTo(at);
                if (map.getZoom() < 16) {
                    map.setZoom(17);
                }
            }
        }

        refresh();
        say();
    };

    const centreOn = async text => {
        if (!geocoder || !text) {
            return null;
        }

        try {
            const { results } = await geocoder.geocode({ address: text, region: "bd" });
            return results[0] ?? null;
        } catch {
            return null;
        }
    };

    clear.addEventListener("click", () => {
        latitude.value = "";
        longitude.value = "";
        marker?.setVisible(false);
        refresh();
        say();
    });

    // The pasted link goes to the server, which knows every form Google Maps writes and can open a short link
    const read = async () => {
        const text = link.value.trim();
        if (!text) {
            return;
        }

        say("Reading the location…");
        try {
            const response = await fetch(box.dataset.pinUrl + "&link=" + encodeURIComponent(text), { credentials: "same-origin" });
            const found = await response.json();
            if (found.latitude !== undefined) {
                setPin(found.latitude, found.longitude);
                link.value = "";
                return;
            }

            const place = found.query ? await centreOn(found.query) : null;
            if (place) {
                setPin(place.geometry.location.lat(), place.geometry.location.lng());
                link.value = "";
                return;
            }

            say(found.problem ?? "That has no location in it.", true);
        } catch {
            say("The location could not be read. Try again, or paste the coordinates.", true);
        }
    };
    use.addEventListener("click", read);
    link.addEventListener("keydown", event => {
        if (event.key === "Enter") {
            event.preventDefault();
            read();
        }
    });
    address.addEventListener("input", refresh);
    area?.addEventListener("change", async () => {
        refresh();
        if (map && !pinned()) {
            const place = await centreOn(areaText());
            if (place) {
                map.fitBounds(place.geometry.viewport);
            }
        }
    });

    refresh();
    say();

    // Called by Google's script once it has loaded (only when the courier has a Google Maps key)
    window.initRecipientMap = async () => {
        const canvas = box.querySelector("[data-pin-map]");
        const start = pinned() ? { lat: Number(latitude.value), lng: Number(longitude.value) } : null;
        geocoder = new google.maps.Geocoder();
        map = new google.maps.Map(canvas, {
            center: start ?? { lat: 0, lng: 0 },
            zoom: start ? 17 : 2,
            mapTypeControl: false,
            streetViewControl: false,
            clickableIcons: false,
            gestureHandling: "cooperative"
        });
        marker = new google.maps.Marker({ map, position: start ?? { lat: 0, lng: 0 }, draggable: true, visible: Boolean(start) });
        marker.addListener("dragend", () => setPin(marker.getPosition().lat(), marker.getPosition().lng(), false));
        map.addListener("click", event => setPin(event.latLng.lat(), event.latLng.lng(), false));
        if (!start) {
            const place = await centreOn(areaText() || "Bangladesh");
            if (place) {
                map.fitBounds(place.geometry.viewport);
            }
        }

        box.querySelector("[data-pin-find]").addEventListener("click", async () => {
            const place = await centreOn(addressText());
            if (place) {
                setPin(place.geometry.location.lat(), place.geometry.location.lng());
            } else {
                say("Google Maps could not find that address. Search for a nearby place, or click the map.", true);
            }
        });

        // Google's place search (Places API, new): picking a result drops the pin on it
        if (google.maps.places?.PlaceAutocompleteElement) {
            const search = new google.maps.places.PlaceAutocompleteElement({ includedRegionCodes: ["bd"] });
            search.setAttribute("placeholder", "Search for a place or address");
            const holder = box.querySelector("[data-pin-search]");
            holder.append(search);
            holder.addEventListener("keydown", event => event.key === "Enter" && event.preventDefault());
            const choose = async place => {
                await place.fetchFields({ fields: ["location", "viewport"] });
                if (place.location) {
                    setPin(place.location.lat(), place.location.lng());
                }
            };
            search.addEventListener("gmp-select", ({ placePrediction }) => choose(placePrediction.toPlace()));
            search.addEventListener("gmp-placeselect", ({ place }) => choose(place));
        }
    };
})();

// The small behaviours every page shares: numbers that count up on arrival, buttons that show they are working and
// cannot be pressed twice, a confirm step before anything hard to undo, toasts, tips that can be put away, "/" to search,
// a password that can be shown, drawings that rest while off screen, and filters that read only their own part again. Everything here is an extra: each page works
// without it.
(() => {
    const root = document.documentElement;
    const reduced = matchMedia("(prefers-reduced-motion: reduce)").matches;
    const icon = paths => `<svg class="icon" viewBox="0 0 24 24" aria-hidden="true">${paths}</svg>`;
    const icons = {
        alert: '<path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/>',
        help: '<circle cx="12" cy="12" r="10"/><path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3"/><line x1="12" y1="17" x2="12.01" y2="17"/>',
        x: '<line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/>',
        eye: '<path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/>',
        eyeOff: '<path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94"/><path d="M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19"/><line x1="1" y1="1" x2="23" y2="23"/>'
    };

    // ---------- Numbers count up on first paint ----------
    const countUp = element => {
        const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
        let node;
        while ((node = walker.nextNode())) {
            const match = node.nodeValue.match(/\d[\d,]*/);
            if (!match) {
                continue;
            }

            const target = Number(match[0].replace(/,/g, ""));
            if (!Number.isFinite(target) || target < 2) {
                return;
            }

            const before = node.nodeValue.slice(0, match.index);
            const after = node.nodeValue.slice(match.index + match[0].length);
            const grouped = match[0].includes(",") || target >= 1000;
            const text = value => before + (grouped ? value.toLocaleString("en-US") : String(value)) + after;
            const textNode = node;
            const start = performance.now();
            const duration = Math.min(1100, 500 + target * 4);
            const step = now => {
                const t = Math.min(1, (now - start) / duration);
                textNode.nodeValue = text(Math.round(target * (1 - Math.pow(1 - t, 3))));
                if (t < 1) {
                    requestAnimationFrame(step);
                }
            };
            textNode.nodeValue = text(0);
            requestAnimationFrame(step);

            return;
        }
    };
    if (!reduced && root.classList.contains("js-enter")) {
        document.querySelectorAll(".stat-value, .flow-count, .cash-card .value, [data-count]").forEach(countUp);
    }

    // ---------- Drawings move only while on screen; parts below the fold rise in the first time they are seen ----------
    if ("IntersectionObserver" in window) {
        const motion = new IntersectionObserver(entries => entries.forEach(entry => {
            entry.target.classList.toggle("paused", !entry.isIntersecting);
            if (entry.isIntersecting) {
                entry.target.classList.add("revealed");
            }
        }));
        document.querySelectorAll("[data-motion]").forEach(element => motion.observe(element));

        if (!reduced) {
            const reveal = new IntersectionObserver(entries => entries.forEach(entry => {
                if (entry.isIntersecting) {
                    entry.target.classList.add("revealed");
                    reveal.unobserve(entry.target);
                }
            }), { rootMargin: "0px 0px -8% 0px" });
            document.querySelectorAll("[data-reveal]").forEach(element => {
                if (element.getBoundingClientRect().top > innerHeight) {
                    element.classList.add("reveal-wait");
                    reveal.observe(element);
                }
            });
        }
    }

    // ---------- The top bar gets a line once the page scrolls ----------
    const topbar = document.querySelector(".topbar");
    if (topbar) {
        const onScroll = () => topbar.classList.toggle("scrolled", scrollY > 4);
        addEventListener("scroll", onScroll, { passive: true });
        onScroll();
    }

    // ---------- The bell: opening it marks what it shows as seen, in a cookie per login the server reads ----------
    document.querySelectorAll("details[data-bell]").forEach(bell => {
        bell.addEventListener("toggle", () => {
            if (bell.open && bell.dataset.bellLatest !== "0") {
                document.cookie = `${bell.dataset.bellCookie}=${bell.dataset.bellLatest}; path=/; max-age=31536000; SameSite=Lax`;
                bell.querySelector("[data-bell-count]")?.remove();
            }
        });
    });

    // ---------- On a phone the tabs are one row to swipe along: the open one is brought into view ----------
    document.querySelectorAll(".tabs").forEach(tabs => {
        const open = tabs.querySelector(".tab.active");
        if (open && tabs.scrollWidth > tabs.clientWidth) {
            const left = open.getBoundingClientRect().left - tabs.getBoundingClientRect().left;
            tabs.scrollLeft += left - (tabs.clientWidth - open.offsetWidth) / 2;
        }
    });

    // ---------- A part of a page with its own filter (data-swap="name") is read again alone ----------
    // A link in it to this same page fetches the page and puts only its part of the same name in place; the address bar
    // follows, so back, reload and a shared link still show that filter. Anything unexpected falls back to the link.
    let shown = location.pathname + location.search;
    let latest = 0;
    const swapIn = async (href, names, push) => {
        const ticket = ++latest;
        const parts = names.map(name => document.querySelector(`[data-swap="${CSS.escape(name)}"]`));
        parts.forEach(part => part.setAttribute("aria-busy", "true"));
        try {
            const response = await fetch(href, { credentials: "same-origin", headers: { "X-Swap": names.join(",") } });
            const fresh = response.ok && !response.redirected
                ? new DOMParser().parseFromString(await response.text(), "text/html")
                : null;
            const next = names.map(name => fresh?.querySelector(`[data-swap="${CSS.escape(name)}"]`));
            if (ticket !== latest) {
                return;
            }

            if (next.includes(null) || next.includes(undefined)) {
                location.href = href;
                return;
            }

            const focused = document.activeElement?.closest("[data-swap] a[href]")?.getAttribute("href");
            parts.forEach((part, index) => part.replaceWith(next[index]));
            if (push) {
                history.pushState({ swap: names }, "", href);
            }
            shown = location.pathname + location.search;
            if (focused) {
                document.querySelector(`[data-swap] a[href="${CSS.escape(focused)}"]`)?.focus();
            }
        } catch {
            location.href = href;
        }
    };
    document.addEventListener("click", event => {
        const link = event.target.closest("[data-swap] a[href]");
        if (!link || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey ||
            event.altKey || link.target || link.hasAttribute("download")) {
            return;
        }

        const url = new URL(link.href);
        if (url.origin !== location.origin || url.pathname.toLowerCase() !== location.pathname.toLowerCase() ||
            url.search === location.search) {
            return;
        }

        event.preventDefault();
        for (const sibling of link.parentElement.children) {
            sibling.classList.toggle("active", sibling === link);
        }
        swapIn(url.href, [link.closest("[data-swap]").dataset.swap], true);
    });
    addEventListener("popstate", () => {
        const names = [...document.querySelectorAll("[data-swap]")].map(part => part.dataset.swap);
        if (names.length > 0 && location.pathname + location.search !== shown) {
            swapIn(location.href, names, false);
        }
    });

    // ---------- "/" jumps to the search box ----------
    addEventListener("keydown", event => {
        if (event.key !== "/" || event.ctrlKey || event.metaKey || event.altKey) {
            return;
        }

        const typing = event.target.closest?.("input, textarea, select, [contenteditable]");
        const search = document.querySelector(".topbar-search input");
        if (!typing && search && search.offsetParent !== null) {
            event.preventDefault();
            search.focus();
            search.select();
        }
    });

    // ---------- A confirm step before anything hard to undo ----------
    let dialog;
    const ask = (message, title, ok, danger) => new Promise(resolve => {
        if (!dialog) {
            dialog = document.createElement("dialog");
            dialog.className = "confirm";
            dialog.innerHTML = `
                <form method="dialog">
                    <div class="confirm-body">
                        <span class="stat-icon"></span>
                        <div><h2></h2><p></p></div>
                    </div>
                    <div class="confirm-actions">
                        <button class="btn btn-outline" value="no">Go back</button>
                        <button class="btn" value="yes"></button>
                    </div>
                </form>`;
            document.body.append(dialog);
        }

        const badge = dialog.querySelector(".stat-icon");
        badge.className = "stat-icon " + (danger ? "red" : "amber");
        badge.innerHTML = icon(danger ? icons.alert : icons.help);
        dialog.querySelector("h2").textContent = title;
        dialog.querySelector("p").textContent = message;
        const yes = dialog.querySelector("[value=yes]");
        yes.textContent = ok;
        yes.className = "btn " + (danger ? "btn-danger-solid" : "btn-primary");
        dialog.returnValue = "";
        dialog.addEventListener("close", () => resolve(dialog.returnValue === "yes"), { once: true });
        dialog.showModal();
        yes.focus();
    });

    document.addEventListener("submit", event => {
        const form = event.target;
        const source = event.submitter?.dataset.confirm !== undefined ? event.submitter : form;
        if (source.dataset.confirm === undefined || form.dataset.confirmed === "yes") {
            delete form.dataset.confirmed;
            return;
        }

        event.preventDefault();
        event.stopImmediatePropagation();
        const submitter = event.submitter;
        ask(
            source.dataset.confirm,
            source.dataset.confirmTitle || "Are you sure?",
            source.dataset.confirmOk || "Yes, go ahead",
            source.dataset.confirmTone !== "calm").then(yes => {
                if (yes) {
                    form.dataset.confirmed = "yes";
                    form.requestSubmit(submitter?.form === form ? submitter : undefined);
                }
            });
    }, true);

    // ---------- Buttons show they are working, and a form is sent once ----------
    document.addEventListener("submit", event => {
        const form = event.target;
        if (event.defaultPrevented || (form.method || "").toLowerCase() !== "post" || form.hasAttribute("data-no-busy")) {
            return;
        }

        if (form.dataset.sending === "yes") {
            event.preventDefault();
            return;
        }

        form.dataset.sending = "yes";
        const button = event.submitter?.classList.contains("btn") ? event.submitter : null;
        if (button) {
            // A button's own name and value must still be sent, so it is only styled, not disabled
            button.classList.add("is-busy");
            button.setAttribute("aria-busy", "true");
        }

        setTimeout(() => {
            delete form.dataset.sending;
            button?.classList.remove("is-busy");
            button?.removeAttribute("aria-busy");
        }, 10000);
    });
    addEventListener("pageshow", event => {
        if (event.persisted) {
            document.querySelectorAll("form[data-sending]").forEach(form => delete form.dataset.sending);
            document.querySelectorAll(".is-busy").forEach(button => button.classList.remove("is-busy"));
        }
    });

    // ---------- Toasts: success fades away by itself, a problem stays until closed ----------
    const toasts = document.querySelector(".toasts");
    if (toasts) {
        document.body.append(toasts);
        for (const toast of toasts.querySelectorAll(".toast")) {
            const close = () => {
                toast.classList.add("leaving");
                setTimeout(() => toast.remove(), 300);
            };
            toast.querySelector(".toast-close")?.addEventListener("click", close);
            const timer = toast.querySelector(".toast-timer");
            if (timer && reduced) {
                // Without motion the timer bar does not run: close on the clock instead
                setTimeout(close, 8000);
            } else {
                timer?.addEventListener("animationend", close);
            }
        }
    }

    // ---------- Tips can be put away; the choice is remembered on this browser ----------
    const remembered = key => {
        try {
            return localStorage.getItem(key) === "1";
        } catch {
            return false;
        }
    };
    for (const tip of document.querySelectorAll(".tip[data-tip]")) {
        const key = "tip:" + tip.dataset.tip;
        if (remembered(key)) {
            tip.classList.add("dismissed");
            continue;
        }

        const close = document.createElement("button");
        close.type = "button";
        close.className = "tip-close";
        close.title = "Hide this tip";
        close.setAttribute("aria-label", "Hide this tip");
        close.innerHTML = icon(icons.x);
        close.addEventListener("click", () => {
            tip.animate([{ opacity: 1, transform: "none" }, { opacity: 0, transform: "translateY(-6px)" }], { duration: 220, easing: "ease-out" })
                .finished.then(() => tip.classList.add("dismissed"));
            try {
                localStorage.setItem(key, "1");
            } catch {
                // Private windows may refuse: the tip simply shows again next time
            }
        });
        tip.append(close);
    }

    // ---------- Show the password while typing ----------
    for (const input of document.querySelectorAll("input[type=password][data-reveal]")) {
        const wrap = document.createElement("div");
        wrap.className = "password-field";
        input.before(wrap);
        wrap.append(input);
        const toggle = document.createElement("button");
        toggle.type = "button";
        toggle.className = "password-toggle";
        toggle.setAttribute("aria-label", "Show password");
        toggle.innerHTML = icon(icons.eye);
        toggle.addEventListener("click", () => {
            const shown = input.type === "text";
            input.type = shown ? "password" : "text";
            toggle.innerHTML = icon(shown ? icons.eye : icons.eyeOff);
            toggle.setAttribute("aria-label", shown ? "Show password" : "Hide password");
            input.focus();
        });
        wrap.append(toggle);
    }

    // ---------- Copy a tracking code ----------
    document.addEventListener("click", async event => {
        const button = event.target.closest("[data-copy]");
        if (!button) {
            return;
        }

        try {
            await navigator.clipboard.writeText(button.dataset.copy);
            const was = button.title;
            button.title = "Copied";
            button.classList.add("copied");
            setTimeout(() => {
                button.title = was;
                button.classList.remove("copied");
            }, 1500);
        } catch {
            // Clipboard refused (not a secure page): nothing to do
        }
    });

    // ---------- Hide the amounts on screen; the choice is remembered on this browser ----------
    // The class is put on <html> by a small script in the head, so amounts are never shown before it is read.
    for (const button of document.querySelectorAll("[data-hide-amounts]")) {
        const show = hidden => {
            document.documentElement.classList.toggle("amounts-hidden", hidden);
            button.setAttribute("aria-pressed", String(hidden));
            button.title = hidden ? "Show the amounts" : "Hide the amounts on screen";
        };

        show(remembered("amounts-hidden"));
        button.addEventListener("click", () => {
            const hidden = !document.documentElement.classList.contains("amounts-hidden");
            show(hidden);
            try {
                localStorage.setItem("amounts-hidden", hidden ? "1" : "0");
            } catch {
                // Private windows may refuse: the amounts simply show again next time
            }
        });
    }

    // ---------- Close the phone menu when a link in it is followed ----------
    const navOpen = document.getElementById("nav-open");
    document.querySelector(".sidebar")?.addEventListener("click", event => {
        if (navOpen && event.target.closest("a")) {
            navOpen.checked = false;
        }
    });
    addEventListener("keydown", event => {
        if (event.key === "Escape" && navOpen?.checked) {
            navOpen.checked = false;
        }
    });

    // ---------- Pop-over menus (the account menu, the favourites picker): one open at a time ----------
    const popovers = [...document.querySelectorAll("details[data-popover]")];
    for (const popover of popovers) {
        popover.addEventListener("toggle", () => {
            if (popover.open) {
                popovers.filter(other => other !== popover).forEach(other => other.open = false);
            }
        });
    }
    document.addEventListener("click", event => {
        popovers.filter(popover => popover.open && !popover.contains(event.target)).forEach(popover => popover.open = false);
    });
    addEventListener("keydown", event => {
        if (event.key === "Escape") {
            popovers.filter(popover => popover.open).forEach(popover => {
                popover.open = false;
                popover.querySelector("summary")?.focus();
            });
        }
    });

    // ---------- Favourites: the stars in the menu and the picker both pin a page to the bar ----------
    // The list is kept in a cookie per login, so the server draws the bar with the page next time.
    const favbar = document.querySelector("[data-favbar]");
    if (favbar) {
        const items = favbar.querySelector("[data-fav-items]");
        const empty = favbar.querySelector(".favbar-empty");
        const count = favbar.querySelector("[data-fav-count]");
        const head = favbar.querySelector(".fav-panel-head");
        const headText = head.textContent;
        const max = Number(favbar.dataset.favMax);
        const chips = () => [...items.querySelectorAll("[data-fav-chip]")];

        const save = () => {
            // "-" for none: an empty cookie reads as never chosen, which would bring the defaults back
            const list = chips().map(chip => chip.dataset.favChip).join(",") || "-";
            document.cookie = `${favbar.dataset.favCookie}=${encodeURIComponent(list)}; path=/; max-age=31536000; samesite=lax`;
            count.textContent = `${chips().length}/${max}`;
            empty.hidden = chips().length > 0;
            head.textContent = headText;
            favbar.classList.remove("full");
        };

        const mark = (href, pinned) => {
            for (const button of document.querySelectorAll(`[data-fav="${CSS.escape(href)}"]`)) {
                button.setAttribute("aria-pressed", String(pinned));
                if (button.classList.contains("nav-star")) {
                    button.title = pinned ? "Remove from favourites" : "Add to favourites";
                }
            }
        };

        document.addEventListener("click", event => {
            const button = event.target.closest("[data-fav]");
            if (!button) {
                return;
            }

            event.preventDefault();
            const href = button.dataset.fav;
            const chip = chips().find(c => c.dataset.favChip === href);
            if (chip) {
                chip.remove();
                mark(href, false);
                save();
                return;
            }

            if (chips().length >= max) {
                head.textContent = `All ${max} places are taken. Remove one first.`;
                favbar.classList.remove("full");
                void favbar.offsetWidth;
                favbar.classList.add("full");
                return;
            }

            // The picker holds every page with its icon and name, so a new chip is made from it
            const option = favbar.querySelector(`.fav-option[data-fav="${CSS.escape(href)}"]`);
            const added = document.createElement("a");
            added.className = "fav-chip" + (location.pathname.toLowerCase() === href.toLowerCase() ? " active" : "");
            added.href = href;
            added.dataset.favChip = href;
            added.append(option.querySelector("svg").cloneNode(true), " ", option.querySelector(".fav-option-label").textContent);
            empty.before(added);
            mark(href, true);
            save();
        });
    }
})();

// ---------- The area picker: division, then zone, then area (data-area-picker on the area's own select) ----------
// The field reads the path taken (Dhaka > Mirpur > Mirpur 10), each step opens the next list, and the cross clears it
(() => {
    const selects = document.querySelectorAll("select[data-area-picker]");
    if (selects.length === 0) {
        return;
    }

    const el = (tag, className, text) => {
        const node = document.createElement(tag);
        if (className) {
            node.className = className;
        }

        if (text !== undefined) {
            node.textContent = text;
        }

        return node;
    };

    selects.forEach(select => {
        const places = [...select.options].filter(option => option.value).map(option => ({
            id: option.value, name: option.textContent.trim(), division: option.dataset.division, zone: option.dataset.zone
        }));
        let division = null;
        let zone = null;
        // While the list is being walked the field shows the steps so far, not the area chosen before
        let browsing = false;

        const root = el("div", "area-picker");
        const trigger = el("div", "area-trigger");
        trigger.tabIndex = 0;
        trigger.setAttribute("role", "combobox");
        trigger.setAttribute("aria-haspopup", "listbox");
        const panel = el("div", "area-panel");
        panel.hidden = true;
        const heading = el("div", "area-heading");
        const search = el("input", "area-search");
        search.type = "search";
        search.autocomplete = "off";
        const list = el("div", "area-list");
        panel.append(heading, search, list);
        root.append(trigger, panel);
        select.after(root);
        select.classList.add("area-native");
        select.tabIndex = -1;

        const chosen = () => places.find(place => place.id === select.value);

        const clear = () => {
            division = null;
            zone = null;
            if (select.value) {
                select.value = "";
                select.dispatchEvent(new Event("input", { bubbles: true }));
                select.dispatchEvent(new Event("change", { bubbles: true }));
            }
        };

        function close() {
            panel.hidden = true;
            browsing = false;
            root.classList.remove("open");
            drawTrigger();
        }

        // Opens the list at the step given, or where the chosen area sits (its zone's areas)
        const open = (atDivision, atZone) => {
            const current = chosen();
            if (atDivision !== undefined) {
                division = atDivision;
                zone = atZone;
            } else if (current) {
                division = current.division;
                zone = current.zone;
            }

            browsing = true;
            search.value = "";
            panel.hidden = false;
            root.classList.add("open");
            draw();
            search.focus();
        };

        // The steps taken so far; each is a button that goes back to that step
        function drawTrigger() {
            const current = browsing ? null : chosen();
            const parts = current ? [current.division, current.zone, current.name] : [division, zone].filter(Boolean);
            trigger.replaceChildren();
            const path = el("span", "area-path");
            if (parts.length === 0) {
                path.append(el("span", "area-placeholder", "Choose the area"));
            }

            parts.forEach((part, index) => {
                const step = el("button", "area-step", part);
                step.type = "button";
                step.addEventListener("click", event => {
                    event.stopPropagation();
                    open(parts[0], index === 0 ? null : parts[1]);
                });
                path.append(step);
                if (index < parts.length - 1 || !current) {
                    path.append(el("span", "area-sep", "›"));
                }
            });
            trigger.append(path);
            if (parts.length > 0) {
                const cross = el("button", "area-clear", "×");
                cross.type = "button";
                cross.setAttribute("aria-label", "Clear the area");
                cross.addEventListener("click", event => {
                    event.stopPropagation();
                    clear();
                    open(null, null);
                });
                trigger.append(cross);
            }

            trigger.append(el("span", "area-caret"));
        }

        const pick = place => {
            select.value = place.id;
            select.dispatchEvent(new Event("input", { bubbles: true }));
            select.dispatchEvent(new Event("change", { bubbles: true }));
            close();
        };

        function draw() {
            const query = search.value.trim().toLowerCase();
            const scope = places.filter(place => (!division || place.division === division) && (!zone || place.zone === zone));
            list.replaceChildren();
            const row = (label, note, onPick, more) => {
                const button = el("button", "area-item");
                button.type = "button";
                button.append(el("span", "area-item-name", label));
                if (note) {
                    button.append(el("small", "area-item-note", note));
                }

                if (more) {
                    button.append(el("span", "area-more", "›"));
                }

                button.addEventListener("click", onPick);
                list.append(button);
            };

            if (query) {
                // Typing looks through every area under the step the picker is at
                heading.textContent = "Matching areas";
                scope.filter(place => place.name.toLowerCase().includes(query) || place.zone.toLowerCase().includes(query))
                    .slice(0, 60)
                    .forEach(place => row(place.name, place.division + " › " + place.zone, () => pick(place), false));
            } else if (!division) {
                heading.textContent = "Select division";
                [...new Set(places.map(place => place.division))].forEach(name => {
                    row(name, places.filter(place => place.division === name).length + " areas", () => { division = name; search.value = ""; draw(); drawTrigger(); search.focus(); }, true);
                });
            } else if (!zone) {
                heading.textContent = "Select zone in " + division;
                [...new Set(scope.map(place => place.zone))].sort((a, b) => a.localeCompare(b)).forEach(name => {
                    row(name, scope.filter(place => place.zone === name).length + " areas", () => { zone = name; search.value = ""; draw(); drawTrigger(); search.focus(); }, true);
                });
            } else {
                heading.textContent = "Select area in " + zone;
                scope.sort((a, b) => a.name.localeCompare(b.name)).forEach(place => row(place.name, "", () => pick(place), false));
            }

            if (!list.hasChildNodes()) {
                list.append(el("p", "area-none", "No place matches that."));
            }

            search.placeholder = !division ? "Search every area" : !zone ? "Search " + division : "Search " + zone;
            list.scrollTop = 0;
            drawTrigger();
        }

        trigger.addEventListener("click", () => (panel.hidden ? open() : close()));
        trigger.addEventListener("keydown", event => {
            if ((event.key === "Enter" || event.key === " ") && event.target === trigger) {
                event.preventDefault();
                panel.hidden ? open() : close();
            }
        });
        search.addEventListener("input", draw);
        search.addEventListener("keydown", event => {
            if (event.key === "Enter") {
                event.preventDefault();
                const first = list.querySelector(".area-item");
                if (first) {
                    first.click();
                }
            }

            if (event.key === "Escape") {
                close();
                trigger.focus();
            }
        });
        // A click inside the picker redraws its own buttons, so where it landed is read from the path it travelled
        document.addEventListener("click", event => { if (!panel.hidden && !event.composedPath().includes(root)) { close(); } });
        // The browser refuses to submit with the field empty: open the picker so the person sees where to look
        select.addEventListener("invalid", () => { if (panel.hidden) { open(); } });
        drawTrigger();
    });
})();

// A scanner that reads labels through the device's camera, for a phone or tablet at the hub (and for trying the scan
// pages without a hand scanner). Hand scanners need none of this: they type into the scan box like a keyboard.
//
//   const scanner = CameraScanner.create({ video, onCode, onTorch, onStop });
//   await scanner.start();   // asks for the camera, then calls onCode(text) for every code it reads
//   scanner.stop();          // also runs by itself when the tab is hidden; either way onStop is told
//
// The browser's own reader (BarcodeDetector, Chrome on Android and desktop) is used where there is one; everywhere
// else (Safari, Firefox) jsQR is loaded from the address on this script's data-jsqr attribute, the first time it is needed.
(() => {
    const libraryUrl = document.currentScript?.dataset.jsqr;
    const formats = ["qr_code", "code_128", "code_39", "itf", "ean_13"];
    const lookAwayFor = 1500;
    const readEvery = 110;
    const widest = 720;

    let libraryLoading;
    const loadLibrary = () => {
        libraryLoading ??= new Promise((resolve, reject) => {
            if (window.jsQR) {
                resolve();
                return;
            }

            const script = document.createElement("script");
            script.src = libraryUrl;
            script.onload = () => resolve();
            script.onerror = () => reject(new Error("reader"));
            document.head.append(script);
        });

        return libraryLoading;
    };

    // The reader to use: the browser's own when it reads QR codes, else jsQR on a canvas
    const makeReader = async video => {
        if ("BarcodeDetector" in window) {
            try {
                const supported = await BarcodeDetector.getSupportedFormats();
                if (supported.includes("qr_code")) {
                    const detector = new BarcodeDetector({ formats: formats.filter(format => supported.includes(format)) });

                    return async () => (await detector.detect(video))[0]?.rawValue ?? null;
                }
            } catch {
                // Fall through to jsQR
            }
        }

        await loadLibrary();
        const canvas = document.createElement("canvas");
        const context = canvas.getContext("2d", { willReadFrequently: true });

        return async () => {
            const scale = Math.min(1, widest / video.videoWidth);
            canvas.width = Math.round(video.videoWidth * scale);
            canvas.height = Math.round(video.videoHeight * scale);
            context.drawImage(video, 0, 0, canvas.width, canvas.height);
            const image = context.getImageData(0, 0, canvas.width, canvas.height);

            return window.jsQR(image.data, image.width, image.height, { inversionAttempts: "dontInvert" })?.data ?? null;
        };
    };

    const explain = error => {
        switch (error?.name || error?.message) {
            case "insecure":
                return "The camera works only on a secure address (https). Open this site with https, or on this computer as localhost.";
            case "unsupported":
                return "This browser cannot use the camera. Try Chrome or Safari.";
            case "NotAllowedError":
            case "SecurityError":
                return "The camera was not allowed. Allow the camera for this site in the browser's settings, then try again.";
            case "NotFoundError":
            case "OverconstrainedError":
                return "No camera was found on this device.";
            case "NotReadableError":
            case "AbortError":
                return "The camera is being used by another app. Close it and try again.";
            case "reader":
                return "The code reader could not be loaded. Check the connection and try again.";
            default:
                return "The camera could not be started.";
        }
    };

    const create = ({ video, onCode, onTorch, onStop }) => {
        let stream;
        let read;
        let timer;
        let running = false;
        let lastCode = "";
        let lastSeenAt = 0;

        const loop = async () => {
            if (!running) {
                return;
            }

            try {
                if (video.readyState >= 2 && video.videoWidth > 0) {
                    const code = await read();
                    const now = Date.now();
                    if (code) {
                        // A label held in view is one scan; it counts again only after it has been out of view
                        const again = code !== lastCode || now - lastSeenAt > lookAwayFor;
                        lastCode = code;
                        lastSeenAt = now;
                        if (again) {
                            onCode(code);
                        }
                    }
                }
            } catch {
                // A frame that cannot be read is skipped
            }

            timer = setTimeout(loop, readEvery);
        };

        const stop = () => {
            running = false;
            clearTimeout(timer);
            stream?.getTracks().forEach(track => track.stop());
            stream = null;
            video.pause();
            video.srcObject = null;
            onTorch?.(false);
            onStop?.();
        };

        const start = async () => {
            if (running) {
                return;
            }

            if (!window.isSecureContext) {
                throw Object.assign(new Error("insecure"), { friendly: explain({ name: "insecure" }) });
            }

            if (!navigator.mediaDevices?.getUserMedia) {
                throw Object.assign(new Error("unsupported"), { friendly: explain({ name: "unsupported" }) });
            }

            try {
                stream = await navigator.mediaDevices.getUserMedia({
                    video: { facingMode: { ideal: "environment" }, width: { ideal: 1280 }, height: { ideal: 720 } },
                    audio: false
                });
                video.srcObject = stream;
                video.muted = true;
                video.setAttribute("playsinline", "");
                await video.play();
                read = await makeReader(video);
            } catch (error) {
                stop();
                throw Object.assign(error, { friendly: explain(error) });
            }

            const track = stream.getVideoTracks()[0];
            onTorch?.(Boolean(track.getCapabilities?.().torch));
            running = true;
            lastCode = "";
            loop();
        };

        const torch = async on => {
            try {
                await stream?.getVideoTracks()[0].applyConstraints({ advanced: [{ torch: on }] });

                return on;
            } catch {
                return false;
            }
        };

        // A camera left on in a hidden tab drains the battery and holds the lens from other apps
        document.addEventListener("visibilitychange", () => {
            if (document.hidden) {
                stop();
            }
        });
        addEventListener("pagehide", stop);

        return { start, stop, torch, get running() { return running; } };
    };

    window.CameraScanner = { create };
})();

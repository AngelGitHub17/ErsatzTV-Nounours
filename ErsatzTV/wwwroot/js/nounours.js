// ErsatzTV — édition Nounours : petites aides côté navigateur
window.nounours = (function () {
    "use strict";

    const stages = new WeakMap();

    function clamp(v, min, max) { return Math.min(max, Math.max(min, v)); }

    // Studio d'habillage : déplacement (glisser) et redimensionnement (poignée) des calques.
    // Les positions sont en % de la scène ; .NET est prévenu à la fin du geste.
    function initStage(stage, dotnetRef) {
        if (!stage || stages.has(stage)) { return; }

        let drag = null;

        function onDown(e) {
            const layer = e.target.closest(".n-layer");
            if (!layer || !stage.contains(layer)) {
                if (e.target === stage || e.target.classList.contains("n-stage-safe")) {
                    dotnetRef.invokeMethodAsync("OnLayerSelected", "");
                }
                return;
            }
            e.preventDefault();
            const rect = stage.getBoundingClientRect();
            const lr = layer.getBoundingClientRect();
            drag = {
                layer,
                id: layer.dataset.id,
                resize: e.target.classList.contains("n-layer-handle"),
                startX: e.clientX,
                startY: e.clientY,
                left: (lr.left - rect.left) / rect.width * 100,
                top: (lr.top - rect.top) / rect.height * 100,
                width: lr.width / rect.width * 100,
                rect
            };
            layer.setPointerCapture(e.pointerId);
            dotnetRef.invokeMethodAsync("OnLayerSelected", drag.id);
        }

        function onMove(e) {
            if (!drag) { return; }
            const dx = (e.clientX - drag.startX) / drag.rect.width * 100;
            const dy = (e.clientY - drag.startY) / drag.rect.height * 100;
            if (drag.resize) {
                drag.newWidth = clamp(drag.width + dx, 1, 100);
                drag.layer.style.width = drag.newWidth + "%";
            } else {
                // magnétisme léger sur la zone de sécurité (5 %) et les bords
                let x = clamp(drag.left + dx, 0, 99);
                let y = clamp(drag.top + dy, 0, 99);
                for (const snap of [0, 5]) {
                    if (Math.abs(x - snap) < 0.8) { x = snap; }
                    if (Math.abs(y - snap) < 0.8) { y = snap; }
                }
                drag.x = x; drag.y = y;
                drag.layer.style.left = x + "%";
                drag.layer.style.top = y + "%";
            }
        }

        function onUp() {
            if (!drag) { return; }
            const d = drag;
            drag = null;
            if (d.resize && d.newWidth !== undefined) {
                dotnetRef.invokeMethodAsync("OnLayerResized", d.id, round(d.newWidth));
            } else if (d.x !== undefined) {
                dotnetRef.invokeMethodAsync("OnLayerMoved", d.id, round(d.x), round(d.y));
            }
        }

        function onKey(e) {
            if (!stage.isConnected) { document.removeEventListener("keydown", onKey); return; }
            const tag = (document.activeElement && document.activeElement.tagName) || "";
            if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") { return; }
            const step = e.shiftKey ? 1 : 0.25;
            const moves = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
            if (moves[e.key]) {
                e.preventDefault();
                dotnetRef.invokeMethodAsync("OnNudge", moves[e.key][0], moves[e.key][1]);
            } else if (e.key === "Delete") {
                dotnetRef.invokeMethodAsync("OnDeleteSelected");
            }
        }

        stage.addEventListener("pointerdown", onDown);
        stage.addEventListener("pointermove", onMove);
        stage.addEventListener("pointerup", onUp);
        stage.addEventListener("pointercancel", onUp);
        document.addEventListener("keydown", onKey);
        stages.set(stage, true);
    }

    function round(v) { return Math.round(v * 100) / 100; }

    // Charge les polices du moteur graphique pour que l'aperçu ressemble au rendu réel
    function loadFonts(fonts) {
        if (!Array.isArray(fonts)) { return; }
        let style = document.getElementById("nounours-fonts");
        if (!style) {
            style = document.createElement("style");
            style.id = "nounours-fonts";
            document.head.appendChild(style);
        }
        style.textContent = fonts.map(f =>
            `@font-face { font-family: "${f.family.replace(/"/g, "")}"; src: url("nounours/habillage/font/${encodeURIComponent(f.fileName)}"); font-display: swap; }`
        ).join("\n");
    }

    // Grille EPG : place la vue sur l'heure actuelle
    function scrollEpgTo(el, x) {
        if (el) { el.scrollLeft = Math.max(0, x - 260); }
    }

    function scrollTopTo(el, y) {
        if (el) { el.scrollTop = Math.max(0, y - 120); }
    }

    function getStorage(key) {
        try { return window.localStorage.getItem(key); } catch { return null; }
    }

    function setStorage(key, value) {
        try { window.localStorage.setItem(key, value); } catch { /* stockage indisponible */ }
    }

    return { initStage, loadFonts, scrollEpgTo, scrollTopTo, getStorage, setStorage };
})();

// Hand-rolled interop helpers for Codemap — no external libraries (spec §2).
window.codemap = (function () {
    let shortcutHandler = null;
    let dotNetRef = null;

    function isTextInput(target) {
        return !!(target && target.closest && target.closest('input, textarea, select, [contenteditable="true"]'));
    }

    // Keys we own (mirrors ShortcutMapper) — prevent browser defaults like Ctrl+K address-bar search.
    function shouldPreventDefault(e, inInput) {
        const key = e.key;
        if (e.ctrlKey || e.metaKey) {
            return ['k', 'K', 'f', 'F', 'Enter'].includes(key);
        }
        if (key === 'Escape') return false;
        if (inInput) return false;
        return ['1', '2', '3', 'f', 'F', '+', '=', '-', '_', '?',
            'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Delete', 'Backspace'].includes(key);
    }

    // ── canvas: all per-frame interaction (pan / zoom / drag / selection highlight) runs
    // client-side so huge graphs never round-trip to the server on pointer moves.
    // The server owns structure (nodes, edges, layout); .NET is only notified of discrete
    // events: node selected, node drag finished.
    const canvas = (function () {
        const MIN_ZOOM = 0.08, MAX_ZOOM = 2.5;
        // Below these scales node text is progressively hidden — SVG text is by far the most
        // expensive thing to paint when thousands of nodes are visible at once.
        const LOD_MID = 0.55, LOD_FAR = 0.28;
        // Above this element count, opacity transitions are disabled (a selection change would
        // otherwise animate tens of thousands of elements at once).
        const PERF_ELEMENT_LIMIT = 1500;

        let svg = null, viewport = null, netRef = null;
        let nodeW = 190, nodeH = 64;
        let tx = 0, ty = 0, k = 1;

        let nodes = new Map();       // id -> { el, x, y }
        let edgesByNode = new Map(); // id -> [edge path elements]
        let edgeCount = 0;

        let selectedId = null;
        let highlighted = [];        // elements holding 'linked'/'active' classes, for cheap clearing

        let mode = null;             // null | 'pan' | 'drag'
        let dragId = null;
        let lastX = 0, lastY = 0, travel = 0;

        function fmt(v) { return Math.round(v * 100) / 100; }

        function applyTransform() {
            if (!viewport) return;
            viewport.setAttribute('transform', `translate(${fmt(tx)} ${fmt(ty)}) scale(${k})`);
            svg.classList.toggle('lod-mid', k < LOD_MID);
            svg.classList.toggle('lod-far', k < LOD_FAR);
        }

        function rebuildIndex() {
            viewport = svg.querySelector('.viewport');
            nodes = new Map();
            edgesByNode = new Map();
            edgeCount = 0;
            if (!viewport) return;
            for (const el of viewport.querySelectorAll('.node-card')) {
                const m = /translate\((-?[\d.]+)[ ,]+(-?[\d.]+)\)/.exec(el.getAttribute('transform') || '');
                nodes.set(el.getAttribute('data-node-id'), { el, x: m ? +m[1] : 0, y: m ? +m[2] : 0 });
            }
            for (const el of viewport.querySelectorAll('.edge-path')) {
                edgeCount++;
                for (const id of [el.getAttribute('data-from'), el.getAttribute('data-to')]) {
                    let list = edgesByNode.get(id);
                    if (!list) edgesByNode.set(id, list = []);
                    list.push(el);
                }
            }
            svg.classList.toggle('perf', nodes.size + edgeCount > PERF_ELEMENT_LIMIT);
        }

        // Mirrors GraphCanvas.EdgePath — keep the two in sync.
        function edgePathD(fromId, toId) {
            const s = nodes.get(fromId), t = nodes.get(toId);
            if (!s || !t) return null;
            const ltr = t.x + nodeW / 2 >= s.x + nodeW / 2;
            const sx = ltr ? s.x + nodeW : s.x, sy = s.y + nodeH / 2;
            const ex = ltr ? t.x : t.x + nodeW, ey = t.y + nodeH / 2;
            const bend = Math.max(42, Math.abs(ex - sx) / 2);
            const sign = ltr ? 1 : -1;
            return `M ${fmt(sx)} ${fmt(sy)} C ${fmt(sx + sign * bend)} ${fmt(sy)}, ${fmt(ex - sign * bend)} ${fmt(ey)}, ${fmt(ex)} ${fmt(ey)}`;
        }

        function refreshEdgesFor(id) {
            for (const el of edgesByNode.get(id) || []) {
                const d = edgePathD(el.getAttribute('data-from'), el.getAttribute('data-to'));
                if (d) el.setAttribute('d', d);
            }
        }

        function setNodePosition(id, x, y) {
            const n = nodes.get(id);
            if (!n) return;
            n.x = x; n.y = y;
            n.el.setAttribute('transform', `translate(${fmt(x)} ${fmt(y)})`);
            refreshEdgesFor(id);
        }

        // Selection dimming works via the svg-level 'has-selection' class (see app.css), so a
        // selection change touches O(degree) elements, never every node on the canvas.
        function applySelection(id) {
            for (const el of highlighted) el.classList.remove('linked', 'active');
            highlighted = [];
            const prev = selectedId && nodes.get(selectedId);
            if (prev) prev.el.classList.remove('selected');
            selectedId = id;
            svg.classList.toggle('has-selection', !!id);
            if (!id) return;
            const node = nodes.get(id);
            if (node) node.el.classList.add('selected');
            for (const el of edgesByNode.get(id) || []) {
                el.classList.add('active');
                highlighted.push(el);
                const from = el.getAttribute('data-from');
                const other = nodes.get(from === id ? el.getAttribute('data-to') : from);
                if (other && other.el !== (node && node.el)) {
                    other.el.classList.add('linked');
                    highlighted.push(other.el);
                }
            }
        }

        function notifySelected(id) {
            if (netRef) netRef.invokeMethodAsync('OnNodeSelectedJs', id);
        }

        function onPointerDown(e) {
            if (e.button !== 0) return;
            lastX = e.clientX; lastY = e.clientY; travel = 0;
            try { svg.setPointerCapture(e.pointerId); } catch { /* pointer already released */ }
            const card = e.target.closest && e.target.closest('.node-card');
            if (card) {
                mode = 'drag';
                dragId = card.getAttribute('data-node-id');
                applySelection(dragId);
                notifySelected(dragId);
            } else {
                mode = 'pan';
                svg.classList.add('panning');
            }
        }

        function onPointerMove(e) {
            if (!mode) return;
            const dx = e.clientX - lastX, dy = e.clientY - lastY;
            lastX = e.clientX; lastY = e.clientY;
            travel += Math.abs(dx) + Math.abs(dy);
            if (mode === 'drag') {
                const n = nodes.get(dragId);
                if (n) setNodePosition(dragId, n.x + dx / k, n.y + dy / k);
            } else {
                tx += dx; ty += dy;
                applyTransform();
            }
        }

        function onPointerUp(e) {
            if (!mode) return;
            if (mode === 'drag') {
                const n = nodes.get(dragId);
                if (n && travel >= 3 && netRef) netRef.invokeMethodAsync('OnNodeMovedJs', dragId, n.x, n.y);
            } else if (travel < 3 && e.type === 'pointerup') {
                // A background press that never moved is a click → clear the selection.
                applySelection(null);
                notifySelected(null);
            }
            mode = null; dragId = null;
            svg.classList.remove('panning');
        }

        function zoomAt(cx, cy, factor) {
            const next = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, k * factor));
            tx = cx - (cx - tx) * (next / k);
            ty = cy - (cy - ty) * (next / k);
            k = next;
            applyTransform();
        }

        function onWheel(e) {
            e.preventDefault();
            const rect = svg.getBoundingClientRect();
            zoomAt(e.clientX - rect.left, e.clientY - rect.top, e.deltaY < 0 ? 1.13 : 1 / 1.13);
        }

        const listeners = [
            ['pointerdown', onPointerDown],
            ['pointermove', onPointerMove],
            ['pointerup', onPointerUp],
            ['pointercancel', onPointerUp],
            ['wheel', onWheel],
        ];

        return {
            attach(svgEl, ref, nodeWidth, nodeHeight) {
                svg = svgEl;
                netRef = ref;
                nodeW = nodeWidth;
                nodeH = nodeHeight;
                for (const [name, fn] of listeners)
                    svg.addEventListener(name, fn, name === 'wheel' ? { passive: false } : undefined);
                rebuildIndex();
                applyTransform();
            },

            detach() {
                if (svg) for (const [name, fn] of listeners) svg.removeEventListener(name, fn);
                svg = null; viewport = null; netRef = null;
                nodes = new Map(); edgesByNode = new Map();
                highlighted = []; selectedId = null; mode = null;
            },

            // Called after every server render that changed the canvas DOM: re-index nodes/edges,
            // re-apply the client-owned transform and selection, optionally zoom to fit.
            sync(fit, selected) {
                if (!svg) return;
                rebuildIndex();
                selectedId = null;
                applySelection(selected || null);
                if (fit) this.zoomToFit(); else applyTransform();
            },

            select(id) {
                if (svg) applySelection(id || null);
            },

            moveNode(id, x, y) {
                setNodePosition(id, x, y);
            },

            zoomIn() { this.zoomBy(1.25); },
            zoomOut() { this.zoomBy(1 / 1.25); },

            /// Zooms around the viewport center (spec: +/- are centered on the current viewport).
            zoomBy(factor) {
                if (!svg) return;
                const rect = svg.getBoundingClientRect();
                zoomAt(rect.width / 2, rect.height / 2, factor);
            },

            zoomToFit() {
                if (!svg || nodes.size === 0) return;
                const rect = svg.getBoundingClientRect();
                if (rect.width === 0 || rect.height === 0) return;
                let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
                for (const n of nodes.values()) {
                    if (n.x < minX) minX = n.x;
                    if (n.y < minY) minY = n.y;
                    if (n.x > maxX) maxX = n.x;
                    if (n.y > maxY) maxY = n.y;
                }
                const w = Math.max(1, maxX + nodeW - minX);
                const h = Math.max(1, maxY + nodeH - minY);
                const margin = 60;
                k = Math.min(1.4, Math.max(MIN_ZOOM, Math.min((rect.width - margin) / w, (rect.height - margin) / h)));
                tx = (rect.width - w * k) / 2 - minX * k;
                ty = (rect.height - h * k) / 2 - minY * k;
                applyTransform();
            },

            centerOnNode(id) {
                if (!svg) return;
                const n = nodes.get(id);
                if (!n) return;
                const rect = svg.getBoundingClientRect();
                tx = rect.width / 2 - (n.x + nodeW / 2) * k;
                ty = rect.height / 2 - (n.y + nodeH / 2) * k;
                applyTransform();
            },
        };
    })();

    return {
        registerShortcuts: function (ref) {
            dotNetRef = ref;
            shortcutHandler = function (e) {
                if (e.isComposing) return;
                if (e.repeat && !e.key.startsWith('Arrow')) return; // held-key repeat only makes sense for nudging
                const inInput = isTextInput(e.target);
                if (shouldPreventDefault(e, inInput)) e.preventDefault();
                dotNetRef.invokeMethodAsync('OnKeyDown', e.key, e.ctrlKey || e.metaKey, e.shiftKey, inInput);
            };
            document.addEventListener('keydown', shortcutHandler);
        },
        unregisterShortcuts: function () {
            if (shortcutHandler) document.removeEventListener('keydown', shortcutHandler);
            shortcutHandler = null;
            dotNetRef = null;
        },
        focusElement: function (element) {
            if (element && element.focus) { element.focus(); if (element.select) element.select(); }
        },
        copyText: function (text) {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(text);
                return;
            }
            // fallback for non-secure contexts
            const area = document.createElement('textarea');
            area.value = text;
            area.style.position = 'fixed';
            area.style.opacity = '0';
            document.body.appendChild(area);
            area.select();
            try { document.execCommand('copy'); } catch (e) { /* clipboard unavailable */ }
            document.body.removeChild(area);
        },
        getTheme: function () {
            return document.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark';
        },
        setTheme: function (theme) {
            document.documentElement.setAttribute('data-theme', theme);
            try { localStorage.setItem('codemap-theme', theme); } catch (e) { /* storage blocked */ }
        },
        canvas: canvas,
    };
})();

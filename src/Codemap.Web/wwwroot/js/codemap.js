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
        measure: function (element) {
            if (!element) return { width: 0, height: 0 };
            const rect = element.getBoundingClientRect();
            return { width: rect.width, height: rect.height };
        },
        capturePointer: function (element, pointerId) {
            try { element.setPointerCapture(pointerId); } catch { /* pointer already released */ }
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
        }
    };
})();

/* ================================================================
   LLM Assistant - chat/chat-findbar.js
   In-thread find: Cmd/Ctrl+Shift+F highlights matches in message bubbles and
   navigates between them. Browser's native Cmd/Ctrl+F is left alone.
   ================================================================ */

(function () {
    'use strict';

    // Find state is private to this module.
    let llmaFindState = { matches: [], index: -1, query: '' };

    /** Open + focus the find bar. */
    function llmaOpenFindBar() {
        const bar = document.getElementById('llma-find-bar');
        const input = document.getElementById('llma-find-input');
        if (!bar || !input) return;
        bar.style.display = '';
        input.focus();
        input.select();
    }

    /** Close the find bar and clear highlights. File-private. */
    function llmaCloseFindBar() {
        const bar = document.getElementById('llma-find-bar');
        if (!bar) return;
        bar.style.display = 'none';
        llmaClearFindHighlights();
        llmaFindState = { matches: [], index: -1, query: '' };
    }

    /** Unwrap all <mark> highlights back to plain text. File-private. */
    function llmaClearFindHighlights() {
        const container = document.getElementById('llma-messages');
        if (!container) return;
        // Replace each mark with its text. Walk a static copy of the NodeList since we mutate the DOM.
        const marks = Array.from(container.querySelectorAll('mark.llma-find-mark'));
        for (const m of marks) {
            const parent = m.parentNode;
            if (!parent) continue;
            parent.replaceChild(document.createTextNode(m.textContent || ''), m);
            parent.normalize();
        }
    }

    /** Highlight all occurrences of `query` across message bubbles. File-private. */
    function llmaRunFind(query) {
        llmaClearFindHighlights();
        llmaFindState = { matches: [], index: -1, query: query || '' };
        const container = document.getElementById('llma-messages');
        const countEl = document.getElementById('llma-find-count');
        if (!container || !query) {
            if (countEl) countEl.textContent = '0 / 0';
            return;
        }
        const needle = query.toLowerCase();
        // Walk all text nodes inside message bubbles. Skip nodes already inside a find <mark>.
        const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT, {
            acceptNode: (node) => {
                if (!node.nodeValue || !node.nodeValue.toLowerCase().includes(needle)) return NodeFilter.FILTER_REJECT;
                if (node.parentElement?.closest('mark.llma-find-mark')) return NodeFilter.FILTER_REJECT;
                return NodeFilter.FILTER_ACCEPT;
            }
        });
        const toProcess = [];
        let n; while ((n = walker.nextNode())) toProcess.push(n);
        for (const text of toProcess) {
            const value = text.nodeValue;
            const lower = value.toLowerCase();
            let start = 0;
            const frag = document.createDocumentFragment();
            let idx;
            while ((idx = lower.indexOf(needle, start)) !== -1) {
                if (idx > start) frag.appendChild(document.createTextNode(value.slice(start, idx)));
                const mark = document.createElement('mark');
                mark.className = 'llma-find-mark';
                mark.textContent = value.slice(idx, idx + needle.length);
                frag.appendChild(mark);
                llmaFindState.matches.push(mark);
                start = idx + needle.length;
            }
            if (start < value.length) frag.appendChild(document.createTextNode(value.slice(start)));
            text.parentNode?.replaceChild(frag, text);
        }
        if (llmaFindState.matches.length > 0) {
            llmaFindState.index = 0;
            llmaScrollToCurrentMatch();
        }
        if (countEl) {
            countEl.textContent = llmaFindState.matches.length === 0
                ? '0 / 0'
                : `${llmaFindState.index + 1} / ${llmaFindState.matches.length}`;
        }
    }

    /** Scroll the active match into view + mark it. File-private. */
    function llmaScrollToCurrentMatch() {
        const m = llmaFindState.matches[llmaFindState.index];
        if (!m) return;
        for (const other of llmaFindState.matches) other.classList.remove('active');
        m.classList.add('active');
        m.scrollIntoView({ block: 'center', behavior: 'smooth' });
    }

    /** Move to the next (dir=+1) / previous (dir=-1) match, wrapping. File-private. */
    function llmaFindNext(dir) {
        const len = llmaFindState.matches.length;
        if (len === 0) return;
        llmaFindState.index = (llmaFindState.index + dir + len) % len;
        llmaScrollToCurrentMatch();
        const countEl = document.getElementById('llma-find-count');
        if (countEl) countEl.textContent = `${llmaFindState.index + 1} / ${len}`;
    }

    /** Wire up the find bar's input + buttons (called once during init). */
    function llmaSetupFindBar() {
        const input = document.getElementById('llma-find-input');
        const close = document.getElementById('llma-find-close');
        const prev  = document.getElementById('llma-find-prev');
        const next  = document.getElementById('llma-find-next');
        if (input) {
            input.addEventListener('input', llmaDebounce(() => llmaRunFind(input.value.trim()), 150));
            input.addEventListener('keydown', (e) => {
                if (e.key === 'Enter') {
                    e.preventDefault();
                    llmaFindNext(e.shiftKey ? -1 : 1);
                } else if (e.key === 'Escape') {
                    e.preventDefault();
                    llmaCloseFindBar();
                }
            });
        }
        close?.addEventListener('click', llmaCloseFindBar);
        prev?.addEventListener('click',  () => llmaFindNext(-1));
        next?.addEventListener('click',  () => llmaFindNext(+1));
    }

    // --- Public API (called by sibling files) ---
    window.llmaOpenFindBar  = llmaOpenFindBar;
    window.llmaSetupFindBar = llmaSetupFindBar;
})();

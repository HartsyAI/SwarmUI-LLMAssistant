/* ================================================================
   LLM Assistant - chat/chat-render.js
   Message DOM rendering, history rebuild, scroll, and in-bubble status.
   ================================================================ */

(function () {
    'use strict';

    /** Render a full message list into the thread container (used on thread load/switch). */
    function llmaRenderMessageHistory(messages) {
        const container = document.getElementById('llma-messages');
        if (!container) return;
        container.innerHTML = '';
        for (const msg of messages) {
            // Prefer the persisted media URL (server-authoritative) over a legacy/in-memory base64.
            // <img src> handles both transparently.
            const imgSrc = (Array.isArray(msg.media) && msg.media[0]?.url) || msg.imageBase64 || null;
            llmaAppendMessageToDOM(msg.role, msg.content, imgSrc, msg.id, msg.meta);
            if (msg.role === 'assistant' && Array.isArray(msg.toolCalls) && msg.toolCalls.length > 0) {
                const bubble = document.querySelector(`[data-msg-id="${msg.id}"] .llma-msg-bubble`);
                if (bubble) llmaReplayToolCalls(bubble, msg.toolCalls);
            }
        }
        llmaRebuildAssetsForThread();
        // Refresh the empty-chat greeting: it shows when the rendered list is empty and hides otherwise.
        if (typeof llmaRenderEmptyChatGreeting === 'function') {
            llmaRenderEmptyChatGreeting();
        }
        // Loading/switching a thread always lands at the latest message with follow re-enabled.
        LLMAState._autoFollow = true;
        llmaToggleScrollButton(false);
        llmaScrollToBottom(true);
    }

    /**
     * Build the branch indicator for a message that has sibling branches (created by editing a user
     * message or regenerating an assistant reply): a labeled pill — ⑂ ‹ Branch k / n › ▾ — where the
     * chevrons step and the label opens a picker listing every branch with a preview + time so you can
     * jump straight to one. Returns null when the message has no siblings.
     */
    function llmaBuildBranchPager(nodeId) {
        if (typeof llmaSiblingsOf !== 'function') return null;
        const node = (LLMAState.allNodes || []).find(n => n.id === nodeId);
        if (!node) return null;
        const sibs = llmaSiblingsOf(node);
        if (!sibs || sibs.length < 2) return null;
        const idx = sibs.findIndex(n => n.id === nodeId);

        const pager = document.createElement('div');
        pager.className = 'llma-branch-pager';

        const pill = document.createElement('div');
        pill.className = 'llma-branch-pill';

        const icon = document.createElement('span');
        icon.className = 'llma-branch-icon';
        icon.textContent = '⑂'; // branch glyph
        icon.setAttribute('aria-hidden', 'true');

        const prev = document.createElement('button');
        prev.className = 'llma-branch-nav';
        prev.type = 'button';
        prev.textContent = '‹';
        prev.title = 'Previous branch';
        prev.disabled = idx <= 0;
        prev.addEventListener('click', (e) => { e.stopPropagation(); llmaSwitchBranch(nodeId, -1); });

        const label = document.createElement('button');
        label.className = 'llma-branch-label';
        label.type = 'button';
        label.title = 'Show all branches';
        label.innerHTML = `Branch ${idx + 1} <span class="llma-branch-sep">/</span> ${sibs.length} <span class="llma-branch-caret">▾</span>`;

        const next = document.createElement('button');
        next.className = 'llma-branch-nav';
        next.type = 'button';
        next.textContent = '›';
        next.title = 'Next branch';
        next.disabled = idx >= sibs.length - 1;
        next.addEventListener('click', (e) => { e.stopPropagation(); llmaSwitchBranch(nodeId, +1); });

        pill.append(icon, prev, label, next);
        pager.appendChild(pill);

        // The picker dropdown is built lazily on open so its previews reflect current state.
        label.addEventListener('click', (e) => {
            e.stopPropagation();
            const existing = pager.querySelector('.llma-branch-menu');
            if (existing) { existing.remove(); return; }
            llmaOpenBranchMenu(pager, sibs, nodeId);
        });

        return pager;
    }

    /** Open the branch-picker dropdown under `pager`, one row per sibling branch. */
    function llmaOpenBranchMenu(pager, sibs, activeId) {
        // Close any other open branch menus first.
        document.querySelectorAll('.llma-branch-menu').forEach(m => m.remove());

        const menu = document.createElement('div');
        menu.className = 'llma-branch-menu';
        sibs.forEach((sib, i) => {
            const row = document.createElement('button');
            row.type = 'button';
            row.className = 'llma-branch-menu-row' + (sib.id === activeId ? ' active' : '');

            const num = document.createElement('span');
            num.className = 'llma-branch-menu-num';
            num.textContent = `${i + 1}`;

            const text = document.createElement('span');
            text.className = 'llma-branch-menu-text';
            const raw = (sib.content || '').replace(/\s+/g, ' ').trim();
            text.textContent = raw ? (raw.length > 60 ? raw.slice(0, 60) + '…' : raw) : '(empty)';

            const time = document.createElement('span');
            time.className = 'llma-branch-menu-time';
            time.textContent = (sib.timestamp && typeof llmaRelativeTime === 'function') ? llmaRelativeTime(sib.timestamp) : '';

            row.append(num, text, time);
            row.addEventListener('click', (e) => {
                e.stopPropagation();
                menu.remove();
                if (sib.id !== activeId) llmaSwitchToBranch(sib.id);
            });
            menu.appendChild(row);
        });
        pager.appendChild(menu);

        // Dismiss on outside click / Escape (bound on the next tick so this very click doesn't close it).
        setTimeout(() => {
            const onAway = (ev) => {
                if (!menu.contains(ev.target)) { cleanup(); }
            };
            const onKey = (ev) => { if (ev.key === 'Escape') cleanup(); };
            const cleanup = () => {
                menu.remove();
                document.removeEventListener('mousedown', onAway, true);
                document.removeEventListener('keydown', onKey, true);
            };
            document.addEventListener('mousedown', onAway, true);
            document.addEventListener('keydown', onKey, true);
        }, 0);
    }

    /** Append a single message row (Claude-style: user right-aligned pill, assistant header+body). */
    function llmaAppendMessageToDOM(role, content, imageBase64, msgId, meta) {
        const container = document.getElementById('llma-messages');
        if (!container) return null;

        // First message of the thread: hide the empty-chat greeting since the
        // CSS empty-state selectors only check the messages list (not state).
        const greet = document.getElementById('llma-empty-greeting');
        if (greet && greet.style.display !== 'none') {
            greet.style.display = 'none';
            greet.innerHTML = '';
        }

        const id = msgId || llmaGenerateId();
        const isUser = role === 'user';
        const assistant = LLMAState.assistants.find(a => a.id === LLMAState.activeAssistantId);

        // Message row — column layout; user right-aligned, assistant left-aligned
        const row = document.createElement('div');
        row.className = `llma-msg-row ${isUser ? 'user' : 'assistant'}`;
        row.setAttribute('data-msg-id', id);

        if (isUser) {
            // ── User: right-aligned pill, no avatar ──
            const bubble = document.createElement('div');
            bubble.className = 'llma-msg-bubble user-bubble';
            if (imageBase64) {
                const img = document.createElement('img');
                img.src = imageBase64;
                img.className = 'llma-msg-image';
                bubble.appendChild(img);
            }
            if (content) bubble.appendChild(document.createTextNode(content));

            const actions = document.createElement('div');
            actions.className = 'llma-msg-actions';
            actions.appendChild(llmaCreateActionBtn('Copy', () => llmaCopyMessage(id)));
            actions.appendChild(llmaCreateActionBtn('Fork', () => llmaForkFromMessage(id)));
            actions.appendChild(llmaCreateActionBtn('Edit', () => llmaStartEdit(id)));
            actions.appendChild(llmaCreateActionBtn('Del', () => llmaDeleteMessage(id)));

            row.appendChild(bubble);
            row.appendChild(actions);
            // Pager sits outside .llma-msg-actions so it stays visible (not hover-faded) — it's the
            // signal that this point has multiple branches.
            const userPager = llmaBuildBranchPager(id);
            if (userPager) row.appendChild(userPager);
        } else {
            // ── Assistant: header (avatar + name) → body (content + meta + actions) ──
            const header = document.createElement('div');
            header.className = 'llma-msg-header';

            const avatar = document.createElement('div');
            avatar.className = 'llma-msg-avatar ai-avatar';
            if (assistant?.avatar) {
                const img = document.createElement('img');
                img.src = assistant.avatar;
                avatar.appendChild(img);
            } else {
                avatar.style.background = assistant?.color || 'var(--emphasis)';
                avatar.textContent = llmaCategoryIcon(assistant?.icon || assistant?.category || 'chat');
            }
            const sender = document.createElement('span');
            sender.className = 'llma-msg-sender';
            sender.textContent = assistant?.name || 'Assistant';
            header.appendChild(avatar);
            header.appendChild(sender);

            const body = document.createElement('div');
            body.className = 'llma-msg-body';

            const bubble = document.createElement('div');
            bubble.className = 'llma-msg-bubble ai-bubble';
            if (content) {
                bubble.innerHTML = llmaRenderAssistantContent(content, id);
                llmaPostRenderMermaid(bubble);
            } else {
                bubble.innerHTML = '<div class="llma-typing"><span></span><span></span><span></span></div>';
            }

            const metaDiv = document.createElement('div');
            metaDiv.className = 'llma-msg-meta';
            if (meta) {
                const parts = [];
                if (meta.model) parts.push(meta.model);
                parts.push(`${meta.genTime || '?'}s`);
                metaDiv.textContent = parts.join(' · ');
            }

            const actions = document.createElement('div');
            actions.className = 'llma-msg-actions';
            actions.appendChild(llmaCreateActionBtn('Copy', () => llmaCopyMessage(id)));
            actions.appendChild(llmaCreateActionBtn('Regen', () => llmaRegenerateMessage(id)));
            actions.appendChild(llmaCreateActionBtn('Use as Prompt', () => {
                const msg = LLMAState.messages.find(m => m.id === id);
                if (msg) llmaSendToPromptBox(msg.content);
            }));
            actions.appendChild(llmaCreateActionBtn('Del', () => llmaDeleteMessage(id)));

            body.appendChild(bubble);
            body.appendChild(metaDiv);
            body.appendChild(actions);
            // Pager outside the hover-faded actions so it stays visible as a branch indicator.
            const aiPager = llmaBuildBranchPager(id);
            if (aiPager) body.appendChild(aiPager);
            row.appendChild(header);
            row.appendChild(body);
        }

        container.appendChild(row);
        llmaScrollToBottom();
        return id;
    }

    /**
     * Scroll the message container to the latest message — but only when auto-follow is active, so a
     * user reading earlier text mid-stream isn't yanked back down. Pass force=true to scroll
     * unconditionally (eg the user just sent a message or switched threads).
     */
    function llmaScrollToBottom(force) {
        const container = document.getElementById('llma-messages');
        if (!container) return;
        if (!force && LLMAState._autoFollow === false) return;
        container.scrollTop = container.scrollHeight;
    }

    /** True when the viewport is at (or within `threshold`px of) the bottom of the message list. */
    function llmaIsAtBottom(container, threshold = 64) {
        return container.scrollHeight - container.scrollTop - container.clientHeight <= threshold;
    }

    /** Show/hide the floating "scroll to latest" button. */
    function llmaToggleScrollButton(show) {
        document.getElementById('llma-scroll-bottom-btn')?.classList.toggle('visible', !!show);
    }

    /** Re-enable auto-follow and snap to the bottom (button click / new user message). */
    function llmaResumeAutoFollow() {
        LLMAState._autoFollow = true;
        llmaToggleScrollButton(false);
        llmaScrollToBottom(true);
    }

    /**
     * Wire the smart-scroll behavior on the message container (idempotent). Auto-follow stays on while
     * the user is at the bottom; scrolling UP turns it off and reveals the scroll-to-latest button;
     * returning to the bottom (manually or via the button) turns it back on. Direction-based so the
     * smooth programmatic scroll-to-bottom can't momentarily flip the state and cause flicker.
     */
    function llmaSetupScroll() {
        const container = document.getElementById('llma-messages');
        if (!container || container.dataset.llmaScrollBound === '1') return;
        container.dataset.llmaScrollBound = '1';
        if (LLMAState._autoFollow === undefined) LLMAState._autoFollow = true;
        let lastTop = container.scrollTop;
        container.addEventListener('scroll', () => {
            const top = container.scrollTop;
            const goingUp = top < lastTop - 2;
            lastTop = top;
            const atBottom = llmaIsAtBottom(container);
            if (atBottom) {
                LLMAState._autoFollow = true;
            } else if (goingUp) {
                LLMAState._autoFollow = false;
            }
            llmaToggleScrollButton(!LLMAState._autoFollow && !atBottom);
        }, { passive: true });
        document.getElementById('llma-scroll-bottom-btn')?.addEventListener('click', llmaResumeAutoFollow);
    }

    /** Replace a bubble's contents with a friendly, informative inline error card. */
    function llmaShowErrorInBubble(bubble, error) {
        if (!bubble) return;
        const info = (typeof llmaHumanizeError === 'function')
            ? llmaHumanizeError(error)
            : { title: (typeof llmaShortError === 'function' ? llmaShortError(error) : String(error)) || 'Something went wrong', hint: null, detail: '' };
        console.error('[LLMAssistant] chat error:', error);
        let body = `<div class="llma-msg-error-title">${llmaEscapeHtml(info.title)}</div>`;
        if (info.hint) body += `<div class="llma-msg-error-hint">${llmaEscapeHtml(info.hint)}</div>`;
        // Keep the raw detail reachable via tooltip when it differs from the shown title.
        const tip = info.detail && info.detail !== info.title ? ` title="${llmaEscapeHtml(info.detail)}"` : '';
        bubble.innerHTML =
            `<div class="llma-msg-error"${tip}>` +
                `<span class="llma-msg-error-icon" aria-hidden="true">⚠</span>` +
                `<div class="llma-msg-error-body">${body}</div>` +
            `</div>`;
    }

    /**
     * Replace (or clear) a bubble's typing indicator with a labeled spinner. Used for slow backend
     * startup events — most importantly local model loads which can take 10+ seconds on a cold start.
     * Passing label=null clears the indicator (called once the first real chunk arrives).
     */
    function llmaShowLoadStatusInBubble(bubble, label) {
        if (!bubble) return;
        let banner = bubble.querySelector('.llma-load-status');
        if (label === null || label === undefined) {
            banner?.remove();
            return;
        }
        if (!banner) {
            // Hide the typing dots while the status banner is up; we re-insert dots once load finishes.
            const typing = bubble.querySelector('.llma-typing');
            if (typing) typing.style.display = 'none';
            banner = document.createElement('div');
            banner.className = 'llma-load-status';
            banner.innerHTML = '<span class="llma-spinner" aria-hidden="true"></span><span class="llma-load-status-text"></span>';
            bubble.insertBefore(banner, bubble.firstChild);
        }
        const text = banner.querySelector('.llma-load-status-text');
        if (text) text.textContent = label;
    }

    // --- Public API (called by sibling files + other chat/ modules) ---
    window.llmaRenderMessageHistory   = llmaRenderMessageHistory;
    window.llmaAppendMessageToDOM     = llmaAppendMessageToDOM;
    window.llmaScrollToBottom         = llmaScrollToBottom;
    window.llmaResumeAutoFollow       = llmaResumeAutoFollow;
    window.llmaToggleScrollButton     = llmaToggleScrollButton;
    window.llmaSetupScroll            = llmaSetupScroll;
    window.llmaShowErrorInBubble      = llmaShowErrorInBubble;
    window.llmaShowLoadStatusInBubble = llmaShowLoadStatusInBubble;
})();

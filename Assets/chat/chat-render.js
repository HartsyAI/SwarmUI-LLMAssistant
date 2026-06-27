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
        llmaScrollToBottom();
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
            actions.appendChild(llmaCreateActionBtn('Edit', () => llmaStartEdit(id)));
            actions.appendChild(llmaCreateActionBtn('Del', () => llmaDeleteMessage(id)));

            row.appendChild(bubble);
            row.appendChild(actions);
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
            row.appendChild(header);
            row.appendChild(body);
        }

        container.appendChild(row);
        llmaScrollToBottom();
        return id;
    }

    /** Scroll the message container to the latest message. */
    function llmaScrollToBottom() {
        const container = document.getElementById('llma-messages');
        if (container) container.scrollTop = container.scrollHeight;
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
    window.llmaShowErrorInBubble      = llmaShowErrorInBubble;
    window.llmaShowLoadStatusInBubble = llmaShowLoadStatusInBubble;
})();

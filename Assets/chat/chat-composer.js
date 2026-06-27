/* ================================================================
   LLM Assistant - chat/chat-composer.js
   Input-area wiring (send/stop/attach/drop/paste + "/" picker hooks),
   char count, and per-message actions (copy / edit / delete / regenerate).
   ================================================================ */

(function () {
    'use strict';

    /** Wire up the composer: send/stop buttons, textarea autoresize, attach/drop, paste. */
    function llmaSetupInput() {
        const input   = document.getElementById('llma-input');
        const sendBtn = document.getElementById('llma-send-btn');
        const stopBtn = document.getElementById('llma-stop-btn');

        if (sendBtn) sendBtn.addEventListener('click', () => llmaSendMessage());
        if (stopBtn) stopBtn.addEventListener('click', () => llmaStopGeneration());

        // "/" tools button — opens the same picker as typing "/", as a full menu of the assistant's tools.
        const toolsBtn = document.getElementById('llma-tools-btn');
        if (toolsBtn) toolsBtn.addEventListener('click', () => {
            if (typeof llmaPickerOpenAll === 'function') llmaPickerOpenAll();
        });

        if (input) {
            input.addEventListener('keydown', e => {
                // Let the "/" tool picker consume navigation/select/close keys first (plain Enter falls through).
                if (typeof llmaPickerOnKeydown === 'function' && llmaPickerOnKeydown(e)) return;
                if (e.key === 'Enter' && !e.shiftKey && LLMAState.enterToSend) {
                    e.preventDefault();
                    llmaSendMessage();
                }
            });
            input.addEventListener('input', () => {
                input.style.height = 'auto';
                input.style.height = Math.min(input.scrollHeight, 140) + 'px';
                llmaUpdateCharCount(input.value);
                if (typeof llmaPickerOnInput === 'function') llmaPickerOnInput(input);
            });
            input.addEventListener('paste', e => llmaHandlePaste(e));
        }

        // File attach. The label opens a file picker via the hidden input; intercept the click
        // on the label so users on a confirmed text-only model get a toast instead of attaching
        // an image the backend will silently ignore.
        const attachLabel = document.getElementById('llma-attach-label');
        if (attachLabel) {
            attachLabel.addEventListener('click', (e) => {
                if (attachLabel.getAttribute('aria-disabled') === 'true') {
                    e.preventDefault();
                    llmaShowToast("This model doesn't support image input. Pick a vision model first.", 'info');
                }
            });
        }
        const attachFile = document.getElementById('llma-attach-file');
        if (attachFile) {
            attachFile.addEventListener('change', () => {
                if (attachFile.files?.length > 0) llmaHandleAttach(attachFile.files[0]);
            });
        }

        // Drag & drop on input area. Honors the same gate as the paperclip — dropping an image
        // onto a text-only model would just be silently discarded by the backend, so block early.
        const inputArea = document.querySelector('.llma-input-wrap');
        if (inputArea) {
            inputArea.addEventListener('dragover', e => { e.preventDefault(); inputArea.style.borderColor = 'var(--emphasis)'; });
            inputArea.addEventListener('dragleave', () => { inputArea.style.borderColor = ''; });
            inputArea.addEventListener('drop', e => {
                e.preventDefault();
                inputArea.style.borderColor = '';
                const files = e.dataTransfer?.files;
                if (files?.length > 0) {
                    for (const f of files) {
                        if (f.type.startsWith('image/')) {
                            if (attachLabel?.getAttribute('aria-disabled') === 'true') {
                                llmaShowToast("This model doesn't support image input.", 'info');
                                return;
                            }
                            llmaHandleAttach(f);
                            return;
                        }
                    }
                }
            });
        }
    }

    /** Update the "~N tokens" char-count hint under the input. */
    function llmaUpdateCharCount(text) {
        const counter = document.getElementById('llma-char-count');
        if (!counter) return;
        if (!text || text.length === 0) { counter.textContent = ''; return; }
        counter.textContent = `~${Math.ceil(text.length / 4)} tokens`;
    }

    /** Copy a message's text to the clipboard. */
    function llmaCopyMessage(msgId) {
        const msg = LLMAState.messages.find(m => m.id === msgId);
        if (msg) {
            navigator.clipboard.writeText(msg.content).then(() => llmaShowToast('Copied!', 'success'));
        }
    }

    /** Delete a message (optimistic local removal; re-fetch on server rejection). */
    async function llmaDeleteMessage(msgId) {
        if (!LLMAState.activeThreadId || !msgId) return;
        // Optimistic local removal so the UI feels snappy; if the server rejects we re-fetch to recover.
        const prevMessages = LLMAState.messages;
        LLMAState.messages = prevMessages.filter(m => m.id !== msgId);
        const el = document.querySelector(`[data-msg-id="${msgId}"]`);
        if (el) el.remove();
        llmaRebuildAssetsForThread();
        llmaUpdateContextBar();
        if (typeof llmaUpdatePanelStats === 'function') llmaUpdatePanelStats();
        try {
            const res = await llmaRequest('LLMAssistantDeleteMessage', { threadId: LLMAState.activeThreadId, messageId: msgId });
            if (!res?.success) {
                llmaShowToast(res?.error || 'Failed to delete message', 'error');
                await llmaReloadActiveThread();
                return;
            }
        } catch {
            llmaShowToast('Failed to delete message', 'error');
            await llmaReloadActiveThread();
            return;
        }
        // Sidebar metadata refresh (title may have changed if first message was deleted, etc.)
        llmaSaveActiveThread();
    }

    /** Inline-edit a message (optimistic local update; revert on failure). */
    function llmaStartEdit(msgId) {
        const msg = LLMAState.messages.find(m => m.id === msgId);
        if (!msg) return;
        const bubble = document.querySelector(`[data-msg-id="${msgId}"] .llma-msg-bubble`);
        if (!bubble) return;
        const original = msg.content;
        bubble.innerHTML = '';

        const textarea = document.createElement('textarea');
        textarea.className = 'llma-input';
        textarea.value = original;
        textarea.rows = Math.max(2, original.split('\n').length);
        textarea.style.width = '100%';
        textarea.style.minHeight = '40px';

        const btnRow = document.createElement('div');
        btnRow.className = 'llma-msg-edit-actions';
        const saveBtn = document.createElement('button');
        saveBtn.className = 'basic-button';
        saveBtn.textContent = 'Save';
        saveBtn.addEventListener('click', async () => {
            const newContent = textarea.value.trim();
            if (!newContent || !LLMAState.activeThreadId) return;
            // Optimistic local update; revert by re-rendering on failure.
            msg.content = newContent;
            bubble.textContent = newContent;
            try {
                const res = await llmaRequest('LLMAssistantEditMessage', {
                    threadId: LLMAState.activeThreadId,
                    messageId: msgId,
                    content: newContent,
                });
                if (!res?.success) {
                    llmaShowToast(res?.error || 'Failed to save edit', 'error');
                    await llmaReloadActiveThread();
                    return;
                }
                llmaSaveActiveThread();
            } catch {
                llmaShowToast('Failed to save edit', 'error');
                await llmaReloadActiveThread();
            }
        });
        const cancelBtn = document.createElement('button');
        cancelBtn.className = 'basic-button';
        cancelBtn.textContent = 'Cancel';
        cancelBtn.addEventListener('click', () => { bubble.textContent = original; });

        btnRow.appendChild(saveBtn);
        btnRow.appendChild(cancelBtn);
        bubble.appendChild(textarea);
        bubble.appendChild(btnRow);
        textarea.focus();
    }

    /** Regenerate from a message: drop it + everything after, then re-stream the last user turn. */
    function llmaRegenerateMessage(msgId) {
        if (LLMAState.isGenerating) return;
        const msgIndex = LLMAState.messages.findIndex(m => m.id === msgId);
        if (msgIndex < 0) return;

        const historyMessages = LLMAState.messages.slice(0, msgIndex);
        const removedIds = LLMAState.messages.slice(msgIndex).map(m => m.id);
        LLMAState.messages = historyMessages;
        removedIds.forEach(id => {
            const el = document.querySelector(`[data-msg-id="${id}"]`);
            if (el) el.remove();
        });

        const history = historyMessages.map(m => ({ role: m.role, content: m.content }));
        const lastUser = historyMessages.filter(m => m.role === 'user').pop();
        const payload = llmaBuildPayload(lastUser?.content || '', 'chat', history);
        llmaStreamResponse(payload);
    }

    // --- Public API (called by sibling files + other chat/ modules) ---
    window.llmaSetupInput        = llmaSetupInput;
    window.llmaUpdateCharCount   = llmaUpdateCharCount;
    window.llmaCopyMessage       = llmaCopyMessage;
    window.llmaDeleteMessage     = llmaDeleteMessage;
    window.llmaStartEdit         = llmaStartEdit;
    window.llmaRegenerateMessage = llmaRegenerateMessage;
})();

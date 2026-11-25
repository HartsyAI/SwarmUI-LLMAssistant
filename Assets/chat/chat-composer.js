/* ================================================================
   LLM Assistant - chat/chat-composer.js
   Input-area wiring (send/stop/attach/drop/paste + "/" picker hooks),
   char count, and the message actions that change the chat (edit / delete / regenerate).
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

        // Tools on/off toggle for the active chat — see tool-picker.js's llmaToggleThreadTools().
        const toolsToggleBtn = document.getElementById('llma-tools-toggle-btn');
        if (toolsToggleBtn) toolsToggleBtn.addEventListener('click', () => {
            if (typeof llmaToggleThreadTools === 'function') llmaToggleThreadTools();
        });
        if (typeof llmaUpdateToolsToggleBtn === 'function') llmaUpdateToolsToggleBtn();

        // Thinking toggle: cycles auto → on → off; llmaBuildPayload sends the matching enableThinking with every request.
        const thinkingBtn = document.getElementById('llma-thinking-toggle-btn');
        LLMAState.thinkingMode = llmaLoadThinkingMode();
        if (thinkingBtn) thinkingBtn.addEventListener('click', () => {
            LLMAState.thinkingMode = llmaNextThinkingMode(LLMAState.thinkingMode);
            llmaSaveThinkingMode(LLMAState.thinkingMode);
            llmaUpdateThinkingToggleBtn();
        });
        llmaUpdateThinkingToggleBtn();

        // Approval mode (Ask / Auto / Plan): a request carries it, and the chat remembers it.
        for (const radio of document.querySelectorAll('input[name="llma-approval"]')) {
            radio.addEventListener('change', () => {
                if (radio.checked) {
                    llmaSetApprovalMode(radio.value, true);
                }
            });
        }
        llmaUpdateApprovalControl();

        if (input) {
            input.addEventListener('keydown', e => {
                // Let the "/" tool picker consume navigation/select/close keys first (plain Enter falls through).
                if (typeof llmaPickerOnKeydown === 'function' && llmaPickerOnKeydown(e)) return;
                if (e.key !== 'Enter' || e.shiftKey) return;
                // Ctrl/Cmd+Enter always sends, regardless of the Enter-to-Send setting.
                if (e.ctrlKey || e.metaKey) {
                    e.preventDefault();
                    llmaSendMessage();
                    return;
                }
                if (LLMAState.enterToSend && !e.altKey) {
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
                        // A video is gated by llmaHandleAttach (it toasts when the model takes no video).
                        if (typeof llmaAttachKind === 'function' && llmaAttachKind(f) === 'video') {
                            llmaHandleAttach(f);
                            return;
                        }
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

    /** Shows the Thinking mode on the composer button: its label, title, aria-pressed and the on/off class. */
    function llmaUpdateThinkingToggleBtn() {
        const btn = document.getElementById('llma-thinking-toggle-btn');
        if (!btn) return;
        const mode = LLMAState.thinkingMode;
        const label = btn.querySelector('.llma-thinking-toggle-label');
        if (label) label.textContent = mode === 'on' ? translate('On') : mode === 'off' ? translate('Off') : translate('Auto');
        btn.classList.toggle('on', mode === 'on');
        btn.classList.toggle('off', mode === 'off');
        btn.setAttribute('aria-pressed', mode === 'auto' ? 'mixed' : String(mode === 'on'));
        // title is reassigned every time this runs — translate() directly, never class="translate".
        btn.title = mode === 'on'
            ? translate('Thinking is ON: the model reasons before answering. Click for OFF.')
            : mode === 'off'
                ? translate('Thinking is OFF: the model answers directly. Click for Auto.')
                : translate("Thinking is Auto: the model's own default applies. Click for ON.");
    }

    /** Update the "~N tokens" char-count hint under the input. */
    function llmaUpdateCharCount(text) {
        const counter = document.getElementById('llma-char-count');
        if (!counter) return;
        if (!text || text.length === 0) { counter.textContent = ''; return; }
        counter.textContent = `~${Math.ceil(text.length / 4)} ${translate('tokens')}`;
    }

    /** Copy a message's plain text to the clipboard (Copy as Markdown is in chat-actions.js). */
    function llmaCopyMessage(msgId) {
        const node = llmaActions.node(msgId);
        if (node) {
            llmaActions.copy(llmaActions.plainTextOf(node));
        }
    }

    /** Delete a message and everything under it (optimistic local removal; re-fetch on server rejection). Asks first when that takes other messages with it. */
    async function llmaDeleteMessage(msgId) {
        if (!LLMAState.activeThreadId || !msgId || llmaActions.deleting) return;
        if (llmaActions.refuseIfBusy('Stop generation before deleting messages')) return;
        llmaActions.deleting = true;
        try {
            await llmaDeleteFromServer(msgId);
        }
        finally {
            llmaActions.deleting = false;
        }
    }

    /** Reads the chat as stored, asks if need be, and deletes. File-private. */
    async function llmaDeleteFromServer(msgId) {
        const threadId = LLMAState.activeThreadId;
        // A delete made from the keyboard leaves focus on the message that took this one's place.
        const row = document.querySelector(`#llma-messages [data-msg-id="${msgId}"]`);
        const focusAt = row?.contains(document.activeElement) ? llmaA11y.messageRows().indexOf(row) : -1;
        // The stored chat decides what the delete takes with it, and whether the message was saved at all: the page's own idea of both can be behind.
        let saved;
        try {
            saved = await llmaActions.fetchThread(threadId);
        }
        catch (error) {
            console.error('[LLMAssistant] Could not read the chat before a delete:', error);
            llmaShowToast('Failed to delete message', 'error');
            return;
        }
        if (LLMAState.activeThreadId !== threadId || llmaActions.busy()) return;
        if (!saved.messages.some(m => m.id === msgId)) {
            // Never saved (a reply that failed or was stopped before anything), or already gone: show the chat as it is stored.
            llmaShowStoredThread(saved, focusAt);
            return;
        }
        const below = llmaActions.countBelow(msgId, saved.messages);
        if (below > 0 && !confirm(llmaActions.deleteQuestion(below))) return;
        // Optimistic local removal so the UI feels snappy; if the server rejects we re-fetch to recover.
        const prevMessages = LLMAState.messages;
        LLMAState.messages = prevMessages.filter(m => m.id !== msgId);
        const el = document.querySelector(`[data-msg-id="${msgId}"]`);
        if (el) el.remove();
        llmaRebuildAssetsForThread();
        llmaUpdateContextBar();
        if (typeof llmaUpdatePanelStats === 'function') llmaUpdatePanelStats();
        try {
            const res = await llmaRequest('LLMAssistantDeleteMessage', { threadId: threadId, messageId: msgId });
            if (!res?.success) {
                llmaShowToast(res?.error || 'Failed to delete message', 'error');
                await llmaReloadActiveThread();
                return;
            }
            // Adopt the authoritative thread: in the branch model a delete removes the whole subtree
            // beneath the message and may move the active leaf, so re-render from the server's view.
            if (res.thread) {
                llmaShowStoredThread(typeof res.thread === 'string' ? JSON.parse(res.thread) : res.thread, focusAt);
            }
        } catch {
            llmaShowToast('Failed to delete message', 'error');
            await llmaReloadActiveThread();
            return;
        }
        // Sidebar metadata refresh (title may have changed if first message was deleted, etc.)
        llmaSaveActiveThread();
    }

    /** Draws the chat as stored after a delete, says so, and puts keyboard focus where the deleted message was. File-private. */
    function llmaShowStoredThread(thread, focusAt) {
        llmaIngestThread(thread);
        llmaRenderMessageHistory(LLMAState.messages);
        llmaRebuildAssetsForThread();
        llmaUpdateContextBar();
        if (typeof llmaUpdatePanelStats === 'function') llmaUpdatePanelStats();
        llmaA11y.announce(translate('Message deleted'));
        if (focusAt >= 0) {
            llmaA11y.focusMessageAt(focusAt);
        }
    }

    /** Sets how this chat approves tool calls ('ask', 'auto' or 'plan'), updates the control, and saves it on the chat when asked to. */
    function llmaSetApprovalMode(mode, persist) {
        if (!['ask', 'auto', 'plan'].includes(mode)) {
            mode = 'auto';
        }
        LLMAState.approvalMode = mode;
        llmaUpdateApprovalControl();
        if (persist && LLMAState.activeThreadId) {
            llmaRequest('LLMAssistantSetThreadApprovalMode', { threadId: LLMAState.activeThreadId, mode: mode }).catch(() => {
                llmaShowToast(translate('Could not save the approval mode for this chat'), 'error');
            });
        }
    }

    /** Shows the current approval mode in the Ask / Auto / Plan control. */
    function llmaUpdateApprovalControl() {
        const radio = document.getElementById(`llma-approval-${LLMAState.approvalMode}`);
        if (radio) {
            radio.checked = true;
        }
    }

    /** Edit a message in place and send it again as a new branch. */
    function llmaStartEdit(msgId) {
        if (llmaActions.refuseIfBusy('Stop generation before editing messages')) return;
        const msg = LLMAState.messages.find(m => m.id === msgId);
        if (!msg) return;
        const bubble = document.querySelector(`[data-msg-id="${msgId}"] .llma-msg-bubble`);
        if (!bubble) return;
        if (bubble.querySelector('textarea')) {
            bubble.querySelector('textarea').focus();
            return;
        }
        const original = msg.content;
        // What the bubble shows now (text and attachments), put back as it was on Cancel.
        const shown = Array.from(bubble.childNodes);
        // Match the textarea's height to the rendered bubble's current height — `rows` alone only
        // accounts for explicit newlines, not wrapped lines, so a long single-line message would
        // otherwise collapse to a tiny 2-row box the moment you click Edit.
        const originalHeight = bubble.getBoundingClientRect().height;
        bubble.innerHTML = '';

        const textarea = document.createElement('textarea');
        textarea.className = 'llma-input';
        textarea.setAttribute('aria-label', translate('Edit message'));
        textarea.value = original;
        textarea.rows = Math.max(2, original.split('\n').length);
        textarea.style.width = '100%';
        textarea.style.minHeight = `${Math.max(40, originalHeight)}px`;

        const btnRow = document.createElement('div');
        btnRow.className = 'llma-msg-edit-actions';
        const saveBtn = document.createElement('button');
        saveBtn.type = 'button';
        saveBtn.className = 'basic-button';
        saveBtn.textContent = translate('Save & resend');
        saveBtn.addEventListener('click', () => {
            const newContent = textarea.value.trim();
            if (!newContent || !LLMAState.activeThreadId) return;
            if (llmaActions.refuseIfBusy('Stop generation before editing messages')) return;
            // Edit = fork a NEW branch from this message and re-send. The original message and the replies
            // beneath it are preserved as a sibling branch, switchable via the ‹ k/n › pager. The server
            // creates the authoritative branch (LLMAssistantEditMessageWS); we mirror it locally first so
            // the messages below the edit point visibly drop away and streaming starts immediately.
            const original = (LLMAState.allNodes || []).find(m => m.id === msgId)
                || LLMAState.messages.find(m => m.id === msgId);
            const newUserId = llmaGenerateId();
            const branchNode = {
                id: newUserId, role: 'user', content: newContent,
                parentId: original ? (original.parentId ?? null) : null,
                media: original?.media,
                timestamp: new Date().toISOString(),
                editedFrom: msgId,
            };
            (LLMAState.allNodes = LLMAState.allNodes || []).push(branchNode);
            LLMAState.activeLeafId = newUserId;
            LLMAState.messages = llmaComputeActivePath(LLMAState.allNodes, newUserId);
            llmaRenderMessageHistory(LLMAState.messages);

            const payload = llmaBuildPayload(newContent, 'chat');
            payload.messageId = msgId;          // the message being edited (server branches from it)
            payload.content = newContent;
            payload.userMessageId = newUserId;  // store the new branch node under this id
            if (branchNode.media) payload.media = branchNode.media;
            llmaStreamResponse(payload, null, { endpoint: 'LLMAssistantEditMessageWS' });
        });
        const cancelBtn = document.createElement('button');
        cancelBtn.type = 'button';
        cancelBtn.className = 'basic-button';
        cancelBtn.textContent = translate('Cancel');
        const cancel = () => {
            bubble.replaceChildren(...shown);
            const row = bubble.closest('.llma-msg-row');
            row?.querySelector('.llma-msg-actions button[aria-label="' + translate('Edit') + '"]')?.focus();
        };
        cancelBtn.addEventListener('click', cancel);
        textarea.addEventListener('keydown', e => {
            if (e.key === 'Escape') {
                e.preventDefault();
                e.stopPropagation();
                cancel();
            }
            else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                e.preventDefault();
                saveBtn.click();
            }
        });

        btnRow.appendChild(saveBtn);
        btnRow.appendChild(cancelBtn);
        bubble.appendChild(textarea);
        bubble.appendChild(btnRow);
        textarea.focus();
    }

    /** Regenerate an assistant reply as a NEW sibling branch (the previous reply stays switchable via the
     *  ‹ k/n › pager). The server moves the branch point to the preceding turn and streams a fresh reply. */
    function llmaRegenerateMessage(msgId) {
        if (llmaActions.refuseIfBusy('Stop generation before regenerating')) return;
        const clicked = llmaActions.node(msgId);
        if (!clicked || !LLMAState.activeThreadId) return;
        // A reply the server never saved (the request failed or was stopped before any text) is asked for again from the message before it.
        const targetId = clicked.unsaved ? clicked.parentId : msgId;
        const target = llmaActions.node(targetId);
        // A user message (Retry) is answered itself; a reply is answered from the message before it.
        const replyTo = target ? (target.role === 'user' ? target.id : target.parentId) : null;
        if (!replyTo) { llmaShowToast('Nothing to regenerate from', 'info'); return; }
        LLMAState.allNodes = LLMAState.allNodes.filter(n => !n.unsaved);
        // Drop back to the turn being answered locally; the streamed reply becomes a new child of it.
        LLMAState.activeLeafId = replyTo;
        LLMAState.messages = llmaComputeActivePath(LLMAState.allNodes, replyTo);
        llmaRenderMessageHistory(LLMAState.messages);

        const payload = llmaBuildPayload('', 'chat');
        payload.messageId = targetId; // the reply to regenerate, or the user message to answer
        llmaStreamResponse(payload, null, { endpoint: 'LLMAssistantRegenerateWS' });
    }

    // --- Public API (called by sibling files + other chat/ modules) ---
    window.llmaSetupInput        = llmaSetupInput;
    window.llmaUpdateThinkingToggleBtn = llmaUpdateThinkingToggleBtn;
    window.llmaSetApprovalMode   = llmaSetApprovalMode;
    window.llmaUpdateApprovalControl = llmaUpdateApprovalControl;
    window.llmaUpdateCharCount   = llmaUpdateCharCount;
    window.llmaCopyMessage       = llmaCopyMessage;
    window.llmaDeleteMessage     = llmaDeleteMessage;
    window.llmaStartEdit         = llmaStartEdit;
    window.llmaRegenerateMessage = llmaRegenerateMessage;
})();

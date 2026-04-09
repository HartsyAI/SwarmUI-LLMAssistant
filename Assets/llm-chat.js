/**
 * LLM Assistant - Chat handler
 * Message rendering, streaming, editing, regeneration, image attach/paste, and vision actions.
 */
(function() {
    'use strict';

    const Chat = {
        messages: [],
        isStreaming: false,
        activeSocket: null,
        streamingMsgId: null,
        streamingText: '',
        pastedImage: null,
        customTitle: false,

        init() {
            this.bindEvents();
        },

        bindEvents() {
            let sendBtn = document.getElementById('llm-send-btn');
            let stopBtn = document.getElementById('llm-stop-btn');
            let input = document.getElementById('llm-input');
            if (sendBtn) sendBtn.addEventListener('click', () => this.submitInput());
            if (stopBtn) stopBtn.addEventListener('click', () => this.stopStreaming());
            if (input) {
                input.addEventListener('keydown', e => {
                    if (e.key === 'Enter' && !e.shiftKey) {
                        e.preventDefault();
                        this.submitInput();
                    }
                });
                input.addEventListener('input', () => {
                    input.style.height = 'auto';
                    input.style.height = Math.min(input.scrollHeight, 200) + 'px';
                    this.updateInputCounter(input.value);
                });
                input.addEventListener('paste', e => this.handlePaste(e));
            }
            // Attach button (file picker)
            let attachFile = document.getElementById('llm-attach-file');
            if (attachFile) {
                attachFile.addEventListener('change', () => {
                    if (attachFile.files?.length > 0) this.handleAttachFile(attachFile.files[0]);
                });
            }
            // Drag & drop on input area
            let inputArea = document.getElementById('llm-input-area');
            if (inputArea) {
                inputArea.addEventListener('dragover', e => {
                    e.preventDefault();
                    inputArea.classList.add('llm-drag-over');
                });
                inputArea.addEventListener('dragleave', () => {
                    inputArea.classList.remove('llm-drag-over');
                });
                inputArea.addEventListener('drop', e => {
                    e.preventDefault();
                    inputArea.classList.remove('llm-drag-over');
                    let files = e.dataTransfer?.files;
                    if (files?.length > 0) {
                        for (let f of files) {
                            if (f.type.startsWith('image/')) {
                                this.handleAttachFile(f);
                                return;
                            }
                        }
                    }
                });
            }
        },

        async submitInput() {
            let input = document.getElementById('llm-input');
            let message = input?.value?.trim();
            if (!message && !this.pastedImage) return;
            if (this.isStreaming) return;
            input.value = '';
            input.style.height = 'auto';
            this.updateInputCounter('');
            let welcome = document.getElementById('llm-welcome');
            if (welcome) welcome.style.display = 'none';
            this.appendMessage('user', message || '(image)');
            let instructionId = LLM.settings?.featureMappings?.['chat-mode'] || 'chat';
            let history = this.getContextHistory();
            let payload = { message, instructionId, history };
            // Backend selection
            if (LLM.selectedBackendId >= 0) {
                payload.backendId = LLM.selectedBackendId;
            }
            // Per-thread parameter overrides
            if (LLM.currentThreadParams) {
                if (LLM.currentThreadParams.temperature >= 0) payload.temperature = LLM.currentThreadParams.temperature;
                if (LLM.currentThreadParams.maxTokens > 0) payload.maxTokens = LLM.currentThreadParams.maxTokens;
            }
            // Attach image
            if (this.pastedImage) {
                payload.media = [{ type: 'base64', data: this.pastedImage, mediaType: 'image/png' }];
                this.clearPastedImage();
            }
            this.streamResponse(payload);
        },

        streamResponse(payload) {
            this.setStreaming(true);
            let assistantMsgId = this.appendMessage('assistant', '');
            this.streamingMsgId = assistantMsgId;
            this.streamingText = '';
            let contentDiv = document.querySelector(`[data-msg-id="${assistantMsgId}"] .llm-msg-content`);
            let startTime = performance.now();
            let firstChunk = true;
            this.activeSocket = LLM.APIClient.sendMessageStreaming(
                payload,
                chunk => {
                    if (firstChunk) {
                        firstChunk = false;
                        let thinking = contentDiv?.querySelector('.llm-thinking');
                        if (thinking) thinking.remove();
                    }
                    this.streamingText += chunk;
                    if (contentDiv) LLM.renderIntoElement(this.streamingText, contentDiv);
                    this.scrollToBottom();
                },
                finalText => {
                    let elapsed = ((performance.now() - startTime) / 1000).toFixed(1);
                    this.streamingText = finalText || this.streamingText;
                    this.updateMessage(assistantMsgId, this.streamingText);
                    if (contentDiv) LLM.renderIntoElement(this.streamingText, contentDiv);
                    let backendName = LLM.getSelectedBackendName() || '';
                    this.addResponseMeta(assistantMsgId, elapsed, backendName);
                    let msg = this.messages.find(m => m.id === assistantMsgId);
                    if (msg) msg.meta = { genTime: elapsed, backend: backendName };
                    this.activeSocket = null;
                    this.streamingMsgId = null;
                    this.setStreaming(false);
                    this.autoSaveThread();
                    LLM.updateContextBar();
                },
                error => {
                    if (contentDiv) contentDiv.innerHTML = `<span class="llm-error">Error: ${error}</span>`;
                    this.activeSocket = null;
                    this.streamingMsgId = null;
                    this.setStreaming(false);
                }
            );
        },

        appendMessage(role, content) {
            let id = LLM.generateId();
            let msg = { id, role, content, timestamp: new Date().toISOString(), edited: false, editedAt: null };
            this.messages.push(msg);
            let container = document.getElementById('llm-messages');
            if (!container) return id;
            let div = document.createElement('div');
            div.className = `llm-message llm-msg-${role}`;
            div.setAttribute('data-msg-id', id);
            let label = document.createElement('div');
            label.className = 'llm-msg-role';
            label.textContent = role === 'user' ? 'You' : 'Assistant';
            let contentDiv = document.createElement('div');
            contentDiv.className = 'llm-msg-content';
            if (role === 'user') {
                contentDiv.textContent = content;
            } else if (content) {
                LLM.renderIntoElement(content, contentDiv);
            } else {
                contentDiv.innerHTML = '<div class="llm-thinking"><span class="llm-thinking-dots"><span>.</span><span>.</span><span>.</span></span></div>';
            }
            let actions = this.createActions(id, role);
            div.appendChild(label);
            div.appendChild(contentDiv);
            div.appendChild(actions);
            container.appendChild(div);
            this.scrollToBottom();
            this.updateContextIndicators();
            LLM.updateContextBar();
            return id;
        },

        createActions(msgId, role) {
            let div = document.createElement('div');
            div.className = 'llm-msg-actions';
            // Copy
            let copyBtn = document.createElement('button');
            copyBtn.className = 'llm-msg-action';
            copyBtn.textContent = 'Copy';
            copyBtn.addEventListener('click', () => {
                let msg = this.messages.find(m => m.id === msgId);
                if (msg) {
                    navigator.clipboard.writeText(msg.content).then(() => {
                        copyBtn.textContent = 'Copied!';
                        setTimeout(() => { copyBtn.textContent = 'Copy'; }, 2000);
                    });
                }
            });
            div.appendChild(copyBtn);
            // Edit (user messages)
            if (role === 'user') {
                let editBtn = document.createElement('button');
                editBtn.className = 'llm-msg-action';
                editBtn.textContent = 'Edit';
                editBtn.addEventListener('click', () => this.startEdit(msgId));
                div.appendChild(editBtn);
            }
            // Regenerate (assistant messages)
            if (role === 'assistant') {
                let regenBtn = document.createElement('button');
                regenBtn.className = 'llm-msg-action';
                regenBtn.textContent = 'Regenerate';
                regenBtn.addEventListener('click', () => this.regenerateMessage(msgId));
                div.appendChild(regenBtn);
                // Use as Prompt
                let promptBtn = document.createElement('button');
                promptBtn.className = 'llm-msg-action';
                promptBtn.textContent = 'Use as Prompt';
                promptBtn.addEventListener('click', () => {
                    let msg = this.messages.find(m => m.id === msgId);
                    if (msg) {
                        let promptBox = document.getElementById('alt_prompt_textbox');
                        if (promptBox) {
                            promptBox.value = msg.content;
                            triggerChangeFor(promptBox);
                        }
                    }
                });
                div.appendChild(promptBtn);
            }
            // Delete
            let delBtn = document.createElement('button');
            delBtn.className = 'llm-msg-action';
            delBtn.textContent = 'Delete';
            delBtn.addEventListener('click', () => this.deleteMessage(msgId));
            div.appendChild(delBtn);
            return div;
        },

        // -- Message Editing --

        startEdit(msgId) {
            let msg = this.messages.find(m => m.id === msgId);
            if (!msg) return;
            let msgEl = document.querySelector(`[data-msg-id="${msgId}"]`);
            let contentDiv = msgEl?.querySelector('.llm-msg-content');
            if (!contentDiv) return;
            let originalContent = msg.content;
            contentDiv.innerHTML = '';
            let textarea = document.createElement('textarea');
            textarea.className = 'llm-edit-textarea';
            textarea.value = originalContent;
            textarea.rows = Math.max(3, originalContent.split('\n').length);
            let btnRow = document.createElement('div');
            btnRow.className = 'llm-edit-actions';
            let saveBtn = document.createElement('button');
            saveBtn.className = 'basic-button';
            saveBtn.textContent = 'Save';
            saveBtn.addEventListener('click', () => {
                let newContent = textarea.value.trim();
                if (newContent) this.finishEdit(msgId, newContent);
            });
            let cancelBtn = document.createElement('button');
            cancelBtn.className = 'basic-button';
            cancelBtn.textContent = 'Cancel';
            cancelBtn.addEventListener('click', () => {
                contentDiv.textContent = originalContent;
            });
            btnRow.appendChild(saveBtn);
            btnRow.appendChild(cancelBtn);
            contentDiv.appendChild(textarea);
            contentDiv.appendChild(btnRow);
            textarea.focus();
        },

        finishEdit(msgId, newContent) {
            let msg = this.messages.find(m => m.id === msgId);
            if (!msg) return;
            msg.content = newContent;
            msg.edited = true;
            msg.editedAt = new Date().toISOString();
            let msgEl = document.querySelector(`[data-msg-id="${msgId}"]`);
            let contentDiv = msgEl?.querySelector('.llm-msg-content');
            if (contentDiv) {
                contentDiv.textContent = newContent;
            }
            this.addEditedLabel(msgEl);
            this.autoSaveThread();
        },

        addEditedLabel(msgEl) {
            if (!msgEl || msgEl.querySelector('.llm-edited-label')) return;
            let roleDiv = msgEl.querySelector('.llm-msg-role');
            if (roleDiv) {
                let label = document.createElement('span');
                label.className = 'llm-edited-label';
                label.textContent = '(edited)';
                roleDiv.appendChild(label);
            }
        },

        // -- Message Regeneration --

        async regenerateMessage(msgId) {
            if (this.isStreaming) return;
            let msgIndex = this.messages.findIndex(m => m.id === msgId);
            if (msgIndex < 0) return;
            let historyMessages = this.messages.slice(0, msgIndex);
            let maxCtx = LLM.currentThreadParams?.maxContextMessages
                || LLM.settings?.parameters?.maxContextMessages || 0;
            let history = historyMessages.map(m => ({ role: m.role, content: m.content }));
            if (maxCtx > 0 && history.length > maxCtx) {
                history = history.slice(-maxCtx);
            }
            let removedIds = this.messages.slice(msgIndex).map(m => m.id);
            this.messages = historyMessages;
            removedIds.forEach(id => {
                let el = document.querySelector(`[data-msg-id="${id}"]`);
                if (el) el.remove();
            });
            let instructionId = LLM.settings?.featureMappings?.['chat-mode'] || 'chat';
            let lastUserMsg = historyMessages.filter(m => m.role === 'user').pop();
            let message = lastUserMsg?.content || '';
            let payload = { message, instructionId, history };
            if (LLM.selectedBackendId >= 0) {
                payload.backendId = LLM.selectedBackendId;
            }
            if (LLM.currentThreadParams) {
                if (LLM.currentThreadParams.temperature >= 0) payload.temperature = LLM.currentThreadParams.temperature;
                if (LLM.currentThreadParams.maxTokens > 0) payload.maxTokens = LLM.currentThreadParams.maxTokens;
            }
            this.streamResponse(payload);
        },

        // -- Image Paste --

        handlePaste(e) {
            let items = e.clipboardData?.items;
            if (!items) return;
            for (let item of items) {
                if (item.type.startsWith('image/')) {
                    e.preventDefault();
                    let file = item.getAsFile();
                    this.readImageFile(file);
                    return;
                }
            }
        },

        // -- Image Attach (file picker) --

        handleAttachFile(file) {
            if (!file.type.startsWith('image/')) return;
            this.readImageFile(file);
            let attachFile = document.getElementById('llm-attach-file');
            if (attachFile) attachFile.value = '';
        },

        readImageFile(file) {
            let reader = new FileReader();
            reader.onload = ev => {
                this.pastedImage = ev.target.result;
                this.showPastePreview(ev.target.result);
            };
            reader.readAsDataURL(file);
        },

        showPastePreview(dataUrl) {
            let preview = document.getElementById('llm-paste-preview');
            if (!preview) return;
            preview.innerHTML = '';
            preview.style.display = '';
            let img = document.createElement('img');
            img.src = dataUrl;
            img.className = 'llm-paste-thumbnail';
            preview.appendChild(img);
            // Vision action buttons
            let captionBtn = document.createElement('button');
            captionBtn.className = 'basic-button llm-preview-action';
            captionBtn.textContent = 'Caption';
            captionBtn.title = 'Send image with caption request';
            captionBtn.addEventListener('click', () => this.captionImage());
            preview.appendChild(captionBtn);
            let promptBtn = document.createElement('button');
            promptBtn.className = 'basic-button llm-preview-action';
            promptBtn.textContent = 'Use as Prompt';
            promptBtn.title = 'Caption and send result to prompt box';
            promptBtn.addEventListener('click', () => this.captionAndSendToPrompt());
            preview.appendChild(promptBtn);
            let initBtn = document.createElement('button');
            initBtn.className = 'basic-button llm-preview-action';
            initBtn.textContent = 'Use as Init';
            initBtn.title = 'Use image as init image for generation';
            initBtn.addEventListener('click', () => {
                if (this.pastedImage && typeof setCurrentImage === 'function') {
                    setCurrentImage(this.pastedImage);
                }
            });
            preview.appendChild(initBtn);
            // Remove button
            let removeBtn = document.createElement('button');
            removeBtn.className = 'llm-paste-remove';
            removeBtn.textContent = '\u00d7';
            removeBtn.title = 'Remove image';
            removeBtn.addEventListener('click', () => this.clearPastedImage());
            preview.appendChild(removeBtn);
        },

        async captionImage() {
            if (!this.pastedImage) return;
            let input = document.getElementById('llm-input');
            if (input) {
                input.value = 'Describe this image in detail.';
            }
            this.submitInput();
        },

        async captionAndSendToPrompt() {
            if (!this.pastedImage) return;
            let welcome = document.getElementById('llm-welcome');
            if (welcome) welcome.style.display = 'none';
            this.appendMessage('user', 'Describe this image for use as an image generation prompt.');
            let instructionId = LLM.settings?.featureMappings?.['caption'] || 'caption';
            let history = this.messages.map(m => ({ role: m.role, content: m.content }));
            let payload = {
                message: 'Describe this image for use as an image generation prompt.',
                instructionId,
                history,
                media: [{ type: 'base64', data: this.pastedImage, mediaType: 'image/png' }]
            };
            if (LLM.selectedBackendId >= 0) {
                payload.backendId = LLM.selectedBackendId;
            }
            this.clearPastedImage();
            this.setStreaming(true);
            let assistantMsgId = this.appendMessage('assistant', '');
            this.streamingMsgId = assistantMsgId;
            this.streamingText = '';
            let contentDiv = document.querySelector(`[data-msg-id="${assistantMsgId}"] .llm-msg-content`);
            let startTime = performance.now();
            let firstChunk = true;
            this.activeSocket = LLM.APIClient.sendMessageStreaming(
                payload,
                chunk => {
                    if (firstChunk) {
                        firstChunk = false;
                        let thinking = contentDiv?.querySelector('.llm-thinking');
                        if (thinking) thinking.remove();
                    }
                    this.streamingText += chunk;
                    if (contentDiv) LLM.renderIntoElement(this.streamingText, contentDiv);
                    this.scrollToBottom();
                },
                finalText => {
                    let elapsed = ((performance.now() - startTime) / 1000).toFixed(1);
                    this.streamingText = finalText || this.streamingText;
                    this.updateMessage(assistantMsgId, this.streamingText);
                    if (contentDiv) LLM.renderIntoElement(this.streamingText, contentDiv);
                    let backendName = LLM.getSelectedBackendName() || '';
                    this.addResponseMeta(assistantMsgId, elapsed, backendName);
                    let msg = this.messages.find(m => m.id === assistantMsgId);
                    if (msg) msg.meta = { genTime: elapsed, backend: backendName };
                    this.activeSocket = null;
                    this.streamingMsgId = null;
                    this.setStreaming(false);
                    this.autoSaveThread();
                    LLM.updateContextBar();
                    let promptBox = document.getElementById('alt_prompt_textbox');
                    if (promptBox) {
                        promptBox.value = this.streamingText;
                        triggerChangeFor(promptBox);
                    }
                },
                error => {
                    if (contentDiv) contentDiv.innerHTML = `<span class="llm-error">Error: ${error}</span>`;
                    this.activeSocket = null;
                    this.streamingMsgId = null;
                    this.setStreaming(false);
                }
            );
        },

        clearPastedImage() {
            this.pastedImage = null;
            let preview = document.getElementById('llm-paste-preview');
            if (preview) {
                preview.innerHTML = '';
                preview.style.display = 'none';
            }
        },

        addResponseMeta(msgId, elapsed, backendName) {
            let msgEl = document.querySelector(`[data-msg-id="${msgId}"]`);
            if (!msgEl || msgEl.querySelector('.llm-msg-meta')) return;
            let meta = document.createElement('div');
            meta.className = 'llm-msg-meta';
            let parts = [];
            if (backendName) parts.push(backendName);
            parts.push(`${elapsed}s`);
            meta.textContent = parts.join('  \u00b7  ');
            let actions = msgEl.querySelector('.llm-msg-actions');
            if (actions) {
                msgEl.insertBefore(meta, actions);
            } else {
                msgEl.appendChild(meta);
            }
        },

        // -- Context window --

        getContextHistory() {
            let maxCtx = LLM.currentThreadParams?.maxContextMessages
                || LLM.settings?.parameters?.maxContextMessages || 0;
            let history = this.messages.map(m => ({ role: m.role, content: m.content }));
            if (maxCtx > 0 && history.length > maxCtx) {
                history = history.slice(-maxCtx);
            }
            return history;
        },

        updateContextIndicators() {
            let maxCtx = LLM.currentThreadParams?.maxContextMessages
                || LLM.settings?.parameters?.maxContextMessages || 0;
            let msgs = document.querySelectorAll('#llm-messages .llm-message');
            if (maxCtx <= 0 || msgs.length <= maxCtx) {
                msgs.forEach(el => el.classList.remove('llm-out-of-context'));
                return;
            }
            let cutoff = msgs.length - maxCtx;
            msgs.forEach((el, i) => {
                el.classList.toggle('llm-out-of-context', i < cutoff);
            });
        },

        // -- Core operations --

        updateMessage(msgId, content) {
            let msg = this.messages.find(m => m.id === msgId);
            if (msg) msg.content = content;
        },

        deleteMessage(msgId) {
            this.messages = this.messages.filter(m => m.id !== msgId);
            let el = document.querySelector(`[data-msg-id="${msgId}"]`);
            if (el) el.remove();
            this.autoSaveThread();
            LLM.updateContextBar();
        },

        clearMessages() {
            this.messages = [];
            this.customTitle = false;
            let container = document.getElementById('llm-messages');
            if (container) container.innerHTML = '';
            let welcome = document.getElementById('llm-welcome');
            if (welcome) welcome.style.display = '';
            LLM.updateContextBar();
        },

        loadMessages(messages) {
            this.clearMessages();
            if (!messages || messages.length === 0) return;
            let welcome = document.getElementById('llm-welcome');
            if (welcome) welcome.style.display = 'none';
            for (let msg of messages) {
                let id = msg.id || LLM.generateId();
                let m = {
                    id, role: msg.role, content: msg.content,
                    timestamp: msg.timestamp,
                    edited: msg.edited || false,
                    editedAt: msg.editedAt || null,
                    meta: msg.meta || null
                };
                this.messages.push(m);
                let container = document.getElementById('llm-messages');
                if (!container) continue;
                let div = document.createElement('div');
                div.className = `llm-message llm-msg-${m.role}`;
                div.setAttribute('data-msg-id', m.id);
                let label = document.createElement('div');
                label.className = 'llm-msg-role';
                label.textContent = m.role === 'user' ? 'You' : 'Assistant';
                let contentDiv = document.createElement('div');
                contentDiv.className = 'llm-msg-content';
                if (m.role === 'user') {
                    contentDiv.textContent = m.content;
                } else {
                    LLM.renderIntoElement(m.content, contentDiv);
                }
                let actions = this.createActions(m.id, m.role);
                div.appendChild(label);
                div.appendChild(contentDiv);
                div.appendChild(actions);
                if (m.edited) this.addEditedLabel(div);
                if (m.role === 'assistant' && m.meta) {
                    let metaDiv = document.createElement('div');
                    metaDiv.className = 'llm-msg-meta';
                    let metaParts = [];
                    if (m.meta.backend) metaParts.push(m.meta.backend);
                    metaParts.push(`${m.meta.genTime || '?'}s`);
                    metaDiv.textContent = metaParts.join('  \u00b7  ');
                    div.insertBefore(metaDiv, actions);
                }
                container.appendChild(div);
            }
            this.scrollToBottom();
            this.updateContextIndicators();
            LLM.updateContextBar();
        },

        setStreaming(active) {
            this.isStreaming = active;
            let sendBtn = document.getElementById('llm-send-btn');
            let stopBtn = document.getElementById('llm-stop-btn');
            let input = document.getElementById('llm-input');
            if (sendBtn) sendBtn.style.display = active ? 'none' : '';
            if (stopBtn) stopBtn.style.display = active ? '' : 'none';
            if (input) input.disabled = active;
        },

        stopStreaming() {
            if (this.activeSocket) {
                try { this.activeSocket.close(); } catch (_) {}
                this.activeSocket = null;
            }
            // Save partial response
            if (this.streamingMsgId && this.streamingText) {
                this.updateMessage(this.streamingMsgId, this.streamingText);
                let contentDiv = document.querySelector(`[data-msg-id="${this.streamingMsgId}"] .llm-msg-content`);
                if (contentDiv) LLM.renderIntoElement(this.streamingText, contentDiv);
                this.autoSaveThread();
            }
            this.streamingMsgId = null;
            this.streamingText = '';
            this.setStreaming(false);
        },

        scrollToBottom() {
            let container = document.getElementById('llm-messages');
            if (container) container.scrollTop = container.scrollHeight;
        },

        updateInputCounter(text) {
            let counter = document.getElementById('llm-input-counter');
            if (!counter) return;
            if (!text || text.length === 0) {
                counter.textContent = '';
                return;
            }
            let chars = text.length;
            let tokens = Math.ceil(chars / 4);
            counter.textContent = `~${tokens} tokens`;
        },

        async autoSaveThread() {
            if (!LLM.currentThreadId) {
                LLM.currentThreadId = LLM.generateId();
            }
            let title = document.getElementById('llm-thread-title')?.textContent || 'LLM Assistant';
            if (!this.customTitle && (title === 'LLM Assistant' || title === 'New Chat' || title === 'New Thread') && this.messages.length > 0) {
                let firstMsg = this.messages[0].content;
                title = firstMsg.length > 50 ? firstMsg.substring(0, 50) + '...' : firstMsg;
                let titleEl = document.getElementById('llm-thread-title');
                if (titleEl) titleEl.textContent = title;
            }
            let thread = {
                id: LLM.currentThreadId,
                title,
                messages: this.messages.map(m => ({
                    id: m.id, role: m.role, content: m.content,
                    timestamp: m.timestamp,
                    edited: m.edited || false,
                    editedAt: m.editedAt || null,
                    meta: m.meta || null
                }))
            };
            if (LLM.currentThreadParams) {
                thread.parameters = LLM.currentThreadParams;
            }
            try {
                await LLM.APIClient.saveThread(thread);
                if (LLM.Threads) LLM.Threads.refreshList();
            } catch (ex) {
                console.error('[LLMAssistant] Failed to save thread:', ex);
            }
        }
    };

    if (!window.LLM) window.LLM = {};
    window.LLM.Chat = Chat;
})();

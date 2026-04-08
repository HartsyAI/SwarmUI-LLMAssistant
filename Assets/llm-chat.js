/**
 * LLM Assistant - Chat handler
 * Message rendering, streaming, editing, regeneration, image attach/paste, and vision actions.
 */
(function() {
    'use strict';

    const Chat = {
        messages: [],
        isStreaming: false,
        pastedImage: null,

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
            let welcome = document.getElementById('llm-welcome');
            if (welcome) welcome.style.display = 'none';
            this.appendMessage('user', message || '(image)');
            let instructionId = LLM.settings?.featureMappings?.['chat-mode'] || 'chat';
            let history = this.messages.map(m => ({ role: m.role, content: m.content }));
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
            let contentDiv = document.querySelector(`[data-msg-id="${assistantMsgId}"] .llm-msg-content`);
            let fullText = '';
            LLM.APIClient.sendMessageStreaming(
                payload,
                chunk => {
                    fullText += chunk;
                    if (contentDiv) LLM.renderIntoElement(fullText, contentDiv);
                    this.scrollToBottom();
                },
                finalText => {
                    fullText = finalText || fullText;
                    this.updateMessage(assistantMsgId, fullText);
                    if (contentDiv) LLM.renderIntoElement(fullText, contentDiv);
                    this.setStreaming(false);
                    this.autoSaveThread();
                    LLM.updateContextBar();
                },
                error => {
                    if (contentDiv) contentDiv.innerHTML = `<span class="llm-error">Error: ${error}</span>`;
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
                contentDiv.innerHTML = '<span class="llm-streaming-cursor"></span>';
            }
            let actions = this.createActions(id, role);
            div.appendChild(label);
            div.appendChild(contentDiv);
            div.appendChild(actions);
            container.appendChild(div);
            this.scrollToBottom();
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
            let history = historyMessages.map(m => ({ role: m.role, content: m.content }));
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
            let contentDiv = document.querySelector(`[data-msg-id="${assistantMsgId}"] .llm-msg-content`);
            let fullText = '';
            LLM.APIClient.sendMessageStreaming(
                payload,
                chunk => {
                    fullText += chunk;
                    if (contentDiv) LLM.renderIntoElement(fullText, contentDiv);
                    this.scrollToBottom();
                },
                finalText => {
                    fullText = finalText || fullText;
                    this.updateMessage(assistantMsgId, fullText);
                    if (contentDiv) LLM.renderIntoElement(fullText, contentDiv);
                    this.setStreaming(false);
                    this.autoSaveThread();
                    LLM.updateContextBar();
                    let promptBox = document.getElementById('alt_prompt_textbox');
                    if (promptBox) {
                        promptBox.value = fullText;
                        triggerChangeFor(promptBox);
                    }
                },
                error => {
                    if (contentDiv) contentDiv.innerHTML = `<span class="llm-error">Error: ${error}</span>`;
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
                    editedAt: msg.editedAt || null
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
                container.appendChild(div);
            }
            this.scrollToBottom();
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
            this.setStreaming(false);
        },

        scrollToBottom() {
            let container = document.getElementById('llm-messages');
            if (container) container.scrollTop = container.scrollHeight;
        },

        async autoSaveThread() {
            if (!LLM.currentThreadId) {
                LLM.currentThreadId = LLM.generateId();
            }
            let title = document.getElementById('llm-thread-title')?.textContent || 'New Thread';
            if (title === 'New Thread' && this.messages.length > 0) {
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
                    editedAt: m.editedAt || null
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

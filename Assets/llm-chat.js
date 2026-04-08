/**
 * LLM Assistant - Chat handler
 * Message rendering, streaming, editing, regeneration, and image paste.
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
            let modeSelect = document.getElementById('llm-mode-select');
            let mode = modeSelect?.value || 'chat';
            let instructionId = LLM.settings?.featureMappings?.[mode + '-mode'] || mode;
            let history = this.messages.map(m => ({ role: m.role, content: m.content }));
            let payload = { message, instructionId, history };
            // Per-thread parameter overrides
            if (LLM.currentThreadParams) {
                if (LLM.currentThreadParams.temperature >= 0) payload.temperature = LLM.currentThreadParams.temperature;
                if (LLM.currentThreadParams.maxTokens > 0) payload.maxTokens = LLM.currentThreadParams.maxTokens;
            }
            // Attach pasted image
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
            // Build history up to (not including) the message being regenerated
            let historyMessages = this.messages.slice(0, msgIndex);
            let history = historyMessages.map(m => ({ role: m.role, content: m.content }));
            // Remove the old message and everything after from UI and memory
            let removedIds = this.messages.slice(msgIndex).map(m => m.id);
            this.messages = historyMessages;
            removedIds.forEach(id => {
                let el = document.querySelector(`[data-msg-id="${id}"]`);
                if (el) el.remove();
            });
            let modeSelect = document.getElementById('llm-mode-select');
            let mode = modeSelect?.value || 'chat';
            let instructionId = LLM.settings?.featureMappings?.[mode + '-mode'] || mode;
            let lastUserMsg = historyMessages.filter(m => m.role === 'user').pop();
            let message = lastUserMsg?.content || '';
            let payload = { message, instructionId, history };
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
                    let reader = new FileReader();
                    reader.onload = ev => {
                        this.pastedImage = ev.target.result;
                        this.showPastePreview(ev.target.result);
                    };
                    reader.readAsDataURL(file);
                    return;
                }
            }
        },

        showPastePreview(dataUrl) {
            let preview = document.getElementById('llm-paste-preview');
            if (!preview) return;
            preview.innerHTML = '';
            preview.style.display = '';
            let img = document.createElement('img');
            img.src = dataUrl;
            img.className = 'llm-paste-thumbnail';
            let removeBtn = document.createElement('button');
            removeBtn.className = 'llm-paste-remove';
            removeBtn.textContent = '\u00d7';
            removeBtn.title = 'Remove image';
            removeBtn.addEventListener('click', () => this.clearPastedImage());
            preview.appendChild(img);
            preview.appendChild(removeBtn);
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

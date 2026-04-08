/**
 * LLM Assistant - Chat handler
 * Message rendering, streaming, and actions.
 */
(function() {
    'use strict';

    const Chat = {
        messages: [],
        isStreaming: false,
        streamAbortController: null,

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
                // Auto-resize textarea
                input.addEventListener('input', () => {
                    input.style.height = 'auto';
                    input.style.height = Math.min(input.scrollHeight, 200) + 'px';
                });
            }
        },

        async submitInput() {
            let input = document.getElementById('llm-input');
            let message = input?.value?.trim();
            if (!message || this.isStreaming) return;
            input.value = '';
            input.style.height = 'auto';
            // Hide welcome message
            let welcome = document.getElementById('llm-welcome');
            if (welcome) welcome.style.display = 'none';
            // Append user message
            this.appendMessage('user', message);
            // Get instruction based on current mode
            let modeSelect = document.getElementById('llm-mode-select');
            let mode = modeSelect?.value || 'chat';
            let instructionId = LLM.settings?.featureMappings?.[mode + '-mode'] || mode;
            // Build history for context
            let history = this.messages.map(m => ({ role: m.role, content: m.content }));
            // Start streaming
            this.setStreaming(true);
            let assistantMsgId = this.appendMessage('assistant', '');
            let contentDiv = document.querySelector(`[data-msg-id="${assistantMsgId}"] .llm-msg-content`);
            let fullText = '';
            LLM.APIClient.sendMessageStreaming(
                { message, instructionId, history },
                chunk => {
                    fullText += chunk;
                    if (contentDiv) {
                        LLM.renderIntoElement(fullText, contentDiv);
                    }
                    this.scrollToBottom();
                },
                finalText => {
                    fullText = finalText || fullText;
                    this.updateMessage(assistantMsgId, fullText);
                    if (contentDiv) {
                        LLM.renderIntoElement(fullText, contentDiv);
                    }
                    this.setStreaming(false);
                    this.autoSaveThread();
                },
                error => {
                    if (contentDiv) {
                        contentDiv.innerHTML = `<span class="llm-error">Error: ${error}</span>`;
                    }
                    this.setStreaming(false);
                }
            );
        },

        appendMessage(role, content) {
            let id = LLM.generateId();
            let msg = { id, role, content, timestamp: new Date().toISOString() };
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
            return id;
        },

        createActions(msgId, role) {
            let div = document.createElement('div');
            div.className = 'llm-msg-actions';
            // Copy button
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
            // Use as Prompt button (assistant only)
            if (role === 'assistant') {
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
            // Delete button
            let delBtn = document.createElement('button');
            delBtn.className = 'llm-msg-action';
            delBtn.textContent = 'Delete';
            delBtn.addEventListener('click', () => this.deleteMessage(msgId));
            div.appendChild(delBtn);
            return div;
        },

        updateMessage(msgId, content) {
            let msg = this.messages.find(m => m.id === msgId);
            if (msg) msg.content = content;
        },

        deleteMessage(msgId) {
            this.messages = this.messages.filter(m => m.id !== msgId);
            let el = document.querySelector(`[data-msg-id="${msgId}"]`);
            if (el) el.remove();
            this.autoSaveThread();
        },

        clearMessages() {
            this.messages = [];
            let container = document.getElementById('llm-messages');
            if (container) container.innerHTML = '';
            let welcome = document.getElementById('llm-welcome');
            if (welcome) welcome.style.display = '';
        },

        loadMessages(messages) {
            this.clearMessages();
            if (!messages || messages.length === 0) return;
            let welcome = document.getElementById('llm-welcome');
            if (welcome) welcome.style.display = 'none';
            for (let msg of messages) {
                this.appendMessage(msg.role, msg.content);
            }
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
            // WebSocket abort is handled by SwarmUI's makeWSRequest internals
            this.setStreaming(false);
        },

        scrollToBottom() {
            let container = document.getElementById('llm-messages');
            if (container) {
                container.scrollTop = container.scrollHeight;
            }
        },

        async autoSaveThread() {
            if (!LLM.currentThreadId) {
                // Create a new thread
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
                title: title,
                messages: this.messages.map(m => ({
                    id: m.id,
                    role: m.role,
                    content: m.content,
                    timestamp: m.timestamp
                }))
            };
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

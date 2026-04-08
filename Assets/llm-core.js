/**
 * LLM Assistant - Core module
 * Global namespace, API client, request builder, and initialization.
 */
if (!window.LLM) {
    window.LLM = {
        initialized: false,
        settings: null,
        currentThreadId: null,
        currentThreadParams: null,

        /** API client wrapping SwarmUI's genericRequest. */
        APIClient: {
            request(endpoint, payload) {
                return new Promise((resolve, reject) => {
                    genericRequest(endpoint, payload, data => {
                        if (data && data.success !== false) {
                            resolve(data);
                        } else {
                            reject(new Error(data?.error || 'Unknown API error'));
                        }
                    });
                });
            },

            async sendMessage(message, instructionId, model) {
                return this.request('LLMAssistantSendMessage', {
                    message, instructionId, model
                });
            },

            sendMessageStreaming(payload, onChunk, onDone, onError) {
                try {
                    makeWSRequest('LLMAssistantSendMessageWS', payload, data => {
                        if (data.error) {
                            if (onError) onError(data.error);
                            return;
                        }
                        if (data.chunk) {
                            onChunk(data.chunk);
                        }
                        if (data.done) {
                            onDone(data.full_text);
                        }
                    }, 0, err => {
                        if (onError) onError(err?.message || 'WebSocket error');
                    });
                } catch (ex) {
                    if (onError) onError(ex.message);
                }
            },

            async getSettings() { return this.request('LLMAssistantGetSettings', {}); },
            async saveSettings(settings) { return this.request('LLMAssistantSaveSettings', { settings }); },
            async resetSettings() { return this.request('LLMAssistantResetSettings', {}); },
            async getModels() { return this.request('LLMAssistantGetModels', {}); },
            async getBackends() { return this.request('LLMAssistantGetBackends', {}); },
            async getThreads() { return this.request('LLMAssistantGetThreads', {}); },
            async getThread(threadId) { return this.request('LLMAssistantGetThread', { threadId }); },
            async saveThread(thread) { return this.request('LLMAssistantSaveThread', { thread }); },
            async deleteThread(threadId) { return this.request('LLMAssistantDeleteThread', { threadId }); },
            async getInstructions() { return this.request('LLMAssistantGetInstructions', {}); },
            async saveInstruction(data) { return this.request('LLMAssistantSaveInstruction', data); },
            async deleteInstruction(id) { return this.request('LLMAssistantDeleteInstruction', { id }); }
        },

        async init() {
            if (this.initialized) return;
            try {
                let resp = await this.APIClient.getSettings();
                this.settings = resp.settings;
                if (LLM.Chat) LLM.Chat.init();
                if (LLM.Threads) LLM.Threads.init();
                if (LLM.Vision) LLM.Vision.init();
                if (LLM.Settings) LLM.Settings.init();
                if (LLM.PromptButtons) LLM.PromptButtons.init();
                this.initModeSelector();
                this.initParamPopover();
                this.initExportButton();
                this.updateContextBar();
                this.initialized = true;
                console.log('[LLMAssistant] Initialized');
            } catch (ex) {
                console.error('[LLMAssistant] Init failed:', ex);
            }
        },

        initModeSelector() {
            let modeSelect = document.getElementById('llm-mode-select');
            if (!modeSelect) return;
            modeSelect.addEventListener('change', () => {
                let mode = modeSelect.value;
                let chatPanel = document.getElementById('llm-chat-panel');
                let visionPanel = document.getElementById('llm-vision-panel');
                if (chatPanel) chatPanel.style.display = (mode !== 'vision') ? '' : 'none';
                if (visionPanel) visionPanel.style.display = (mode === 'vision') ? '' : 'none';
            });
        },

        // -- Per-thread parameter popover --

        initParamPopover() {
            let btn = document.getElementById('llm-params-btn');
            let popover = document.getElementById('llm-params-popover');
            if (!btn || !popover) return;
            btn.addEventListener('click', () => {
                popover.style.display = popover.style.display === 'none' ? '' : 'none';
                if (popover.style.display !== 'none') this.loadParamPopoverValues();
            });
            // Close when clicking outside
            document.addEventListener('click', e => {
                if (!btn.contains(e.target) && !popover.contains(e.target)) {
                    popover.style.display = 'none';
                }
            });
            // Slider value displays
            let tempSlider = document.getElementById('llm-param-temperature');
            let topPSlider = document.getElementById('llm-param-top-p');
            if (tempSlider) {
                tempSlider.addEventListener('input', () => {
                    let val = document.getElementById('llm-param-temperature-val');
                    if (val) val.textContent = tempSlider.value;
                });
            }
            if (topPSlider) {
                topPSlider.addEventListener('input', () => {
                    let val = document.getElementById('llm-param-top-p-val');
                    if (val) val.textContent = topPSlider.value;
                });
            }
            // Apply button
            let applyBtn = document.getElementById('llm-params-apply');
            if (applyBtn) {
                applyBtn.addEventListener('click', () => {
                    this.applyParamOverrides();
                    popover.style.display = 'none';
                });
            }
            // Reset button
            let resetBtn = document.getElementById('llm-params-reset');
            if (resetBtn) {
                resetBtn.addEventListener('click', () => {
                    this.currentThreadParams = null;
                    this.updateParamsButtonLabel();
                    this.updateContextBar();
                    if (LLM.Chat) LLM.Chat.autoSaveThread();
                    popover.style.display = 'none';
                });
            }
        },

        loadParamPopoverValues() {
            let params = this.currentThreadParams || this.settings?.parameters || {};
            let temp = document.getElementById('llm-param-temperature');
            let tempVal = document.getElementById('llm-param-temperature-val');
            let maxTok = document.getElementById('llm-param-max-tokens');
            let topP = document.getElementById('llm-param-top-p');
            let topPVal = document.getElementById('llm-param-top-p-val');
            if (temp) { temp.value = params.temperature ?? 1.0; }
            if (tempVal) { tempVal.textContent = params.temperature ?? 1.0; }
            if (maxTok) { maxTok.value = params.maxTokens ?? 1024; }
            if (topP) { topP.value = params.topP ?? 0.9; }
            if (topPVal) { topPVal.textContent = params.topP ?? 0.9; }
        },

        applyParamOverrides() {
            let temp = document.getElementById('llm-param-temperature');
            let maxTok = document.getElementById('llm-param-max-tokens');
            let topP = document.getElementById('llm-param-top-p');
            this.currentThreadParams = {
                temperature: temp ? parseFloat(temp.value) : 1.0,
                maxTokens: maxTok ? parseInt(maxTok.value, 10) : 1024,
                topP: topP ? parseFloat(topP.value) : 0.9
            };
            this.updateParamsButtonLabel();
            this.updateContextBar();
            if (LLM.Chat) LLM.Chat.autoSaveThread();
        },

        updateParamsButtonLabel() {
            let btn = document.getElementById('llm-params-btn');
            if (!btn) return;
            if (this.currentThreadParams) {
                btn.textContent = `T:${this.currentThreadParams.temperature}`;
                btn.classList.add('llm-params-active');
            } else {
                btn.textContent = 'Params';
                btn.classList.remove('llm-params-active');
            }
        },

        // -- Context bar --

        updateContextBar() {
            let bar = document.getElementById('llm-context-bar');
            if (!bar) return;
            let msgCount = LLM.Chat?.messages?.length || 0;
            let modeSelect = document.getElementById('llm-mode-select');
            let mode = modeSelect?.value || 'chat';
            let instructionId = this.settings?.featureMappings?.[mode + '-mode'] || mode;
            let parts = [];
            parts.push(`${msgCount} message${msgCount !== 1 ? 's' : ''}`);
            parts.push(instructionId);
            if (this.currentThreadParams) {
                parts.push(`T:${this.currentThreadParams.temperature}`);
            }
            bar.textContent = parts.join('  \u00b7  ');
        },

        // -- Thread export --

        initExportButton() {
            let btn = document.getElementById('llm-export-btn');
            let dropdown = document.getElementById('llm-export-dropdown');
            if (!btn || !dropdown) return;
            btn.addEventListener('click', () => {
                dropdown.style.display = dropdown.style.display === 'none' ? '' : 'none';
            });
            document.addEventListener('click', e => {
                if (!btn.contains(e.target) && !dropdown.contains(e.target)) {
                    dropdown.style.display = 'none';
                }
            });
            let jsonBtn = document.getElementById('llm-export-json');
            let mdBtn = document.getElementById('llm-export-md');
            if (jsonBtn) jsonBtn.addEventListener('click', () => { this.exportThread('json'); dropdown.style.display = 'none'; });
            if (mdBtn) mdBtn.addEventListener('click', () => { this.exportThread('markdown'); dropdown.style.display = 'none'; });
        },

        exportThread(format) {
            if (!LLM.Chat?.messages?.length) {
                showError('No messages to export.');
                return;
            }
            let title = document.getElementById('llm-thread-title')?.textContent || 'thread';
            let filename, content, mimeType;
            if (format === 'json') {
                let thread = {
                    id: this.currentThreadId,
                    title,
                    messages: LLM.Chat.messages,
                    parameters: this.currentThreadParams || null,
                    exportedAt: new Date().toISOString()
                };
                content = JSON.stringify(thread, null, 2);
                filename = `${title.replace(/[^a-zA-Z0-9]/g, '_')}.json`;
                mimeType = 'application/json';
            } else {
                let lines = [`# ${title}\n`];
                for (let msg of LLM.Chat.messages) {
                    let role = msg.role === 'user' ? 'You' : 'Assistant';
                    lines.push(`## ${role}\n`);
                    lines.push(msg.content + '\n');
                }
                content = lines.join('\n');
                filename = `${title.replace(/[^a-zA-Z0-9]/g, '_')}.md`;
                mimeType = 'text/markdown';
            }
            let blob = new Blob([content], { type: mimeType });
            let url = URL.createObjectURL(blob);
            let a = document.createElement('a');
            a.href = url;
            a.download = filename;
            a.click();
            URL.revokeObjectURL(url);
        },

        generateId() {
            return Date.now().toString(36) + Math.random().toString(36).substring(2, 8);
        }
    };
}

// Initialize when the LLM Assistant tab is first shown
document.addEventListener('DOMContentLoaded', () => {
    let observer = new MutationObserver(() => {
        let container = document.getElementById('llm-assistant-container');
        if (container && !window.LLM.initialized) {
            window.LLM.init();
            observer.disconnect();
        }
    });
    observer.observe(document.body, { childList: true, subtree: true });
    if (document.getElementById('llm-assistant-container')) {
        window.LLM.init();
    }
});

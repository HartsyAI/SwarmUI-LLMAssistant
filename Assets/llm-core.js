/**
 * LLM Assistant - Core module
 * Global namespace, API client, request builder, and initialization.
 */
if (!window.LLM) {
    window.LLM = {
        initialized: false,
        settings: null,
        currentThreadId: null,

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

            /** Send a chat message (non-streaming). */
            async sendMessage(message, instructionId, model) {
                return this.request('LLMAssistantSendMessage', {
                    message, instructionId, model
                });
            },

            /** Send a streaming chat message via WebSocket. */
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

        /** Initialize the extension after DOM is ready. */
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

        /** Generate a unique ID. */
        generateId() {
            return Date.now().toString(36) + Math.random().toString(36).substring(2, 8);
        }
    };
}

// Initialize when the LLM Assistant tab is first shown
document.addEventListener('DOMContentLoaded', () => {
    // SwarmUI lazy-loads tabs, so we wait for our container to appear
    let observer = new MutationObserver(() => {
        let container = document.getElementById('llm-assistant-container');
        if (container && !window.LLM.initialized) {
            window.LLM.init();
            observer.disconnect();
        }
    });
    observer.observe(document.body, { childList: true, subtree: true });
    // Also try immediately in case tab is already visible
    if (document.getElementById('llm-assistant-container')) {
        window.LLM.init();
    }
});

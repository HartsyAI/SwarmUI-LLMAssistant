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
        selectedBackendId: -1,
        backends: [],

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
                    let socket = makeWSRequest('LLMAssistantSendMessageWS', payload, data => {
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
                    return socket;
                } catch (ex) {
                    if (onError) onError(ex.message);
                    return null;
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
            async exportThread(threadId, format) { return this.request('LLMAssistantExportThread', { threadId, format }); },
            async getInstructions() { return this.request('LLMAssistantGetInstructions', {}); },
            async saveInstruction(data) { return this.request('LLMAssistantSaveInstruction', data); },
            async deleteInstruction(id) { return this.request('LLMAssistantDeleteInstruction', { id }); }
        },

        async init() {
            if (this.initialized) return;
            try {
                this.initContainerHeight();
                let resp = await this.APIClient.getSettings();
                this.settings = resp.settings;
                await this.initBackendSelector();
                if (LLM.Chat) LLM.Chat.init();
                if (LLM.Threads) LLM.Threads.init();
                if (LLM.Settings) LLM.Settings.init();
                if (LLM.PromptButtons) LLM.PromptButtons.init();
                this.initParamPopover();
                this.initExportButton();
                this.initKeyboardShortcuts();
                this.initThreadRename();
                this.initMobileLayout();
                try {
                    let instrResp = await this.APIClient.getInstructions();
                    this.instructions = instrResp.instructions || [];
                } catch (ex) {
                    console.warn('[LLMAssistant] Failed to load instructions:', ex);
                    this.instructions = [];
                }
                this.updateContextBar();
                this.updateWelcomeHint();
                this.initialized = true;
                console.log('[LLMAssistant] Initialized');
            } catch (ex) {
                console.error('[LLMAssistant] Init failed:', ex);
            }
        },

        // -- Container height (accounts for nav tabs) --

        initContainerHeight() {
            let container = document.getElementById('llm-assistant-container');
            if (!container) return;
            let setHeight = () => {
                let navTabs = document.querySelector('.nav.nav-tabs');
                let offset = navTabs ? navTabs.getBoundingClientRect().bottom : 0;
                container.style.height = `calc(100vh - ${offset}px)`;
            };
            setHeight();
            window.addEventListener('resize', setHeight);
        },

        // -- Backend selector --

        async initBackendSelector() {
            let select = document.getElementById('llm-backend-select');
            if (!select) return;
            try {
                let resp = await this.APIClient.getBackends();
                this.backends = resp.backends || [];
            } catch (ex) {
                this.backends = [];
            }
            select.innerHTML = '';
            if (this.backends.length === 0) {
                let opt = document.createElement('option');
                opt.value = '-1';
                opt.textContent = 'No backends';
                opt.disabled = true;
                opt.selected = true;
                select.appendChild(opt);
                select.disabled = true;
            } else {
                this.backends.forEach(b => {
                    let opt = document.createElement('option');
                    opt.value = b.id;
                    opt.textContent = b.name || b.type;
                    select.appendChild(opt);
                });
                select.disabled = false;
                // Select the first backend by default
                this.selectedBackendId = this.backends[0].id;
                select.value = this.selectedBackendId;
            }
            select.addEventListener('change', () => {
                this.selectedBackendId = parseInt(select.value, 10);
                this.updateContextBar();
            });
        },

        getSelectedBackendName() {
            if (this.selectedBackendId < 0) return null;
            let b = this.backends.find(x => x.id === this.selectedBackendId);
            return b ? (b.name || b.type) : null;
        },

        // -- Welcome hint --

        updateWelcomeHint() {
            let hint = document.getElementById('llm-welcome-hint');
            if (!hint) return;
            if (this.backends.length === 0) {
                hint.textContent = 'Configure an LLM backend in Server > Backends to get started.';
            } else {
                hint.textContent = 'Type a message below to begin.';
            }
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
            document.addEventListener('click', e => {
                if (!btn.contains(e.target) && !popover.contains(e.target)) {
                    popover.style.display = 'none';
                }
            });
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
            let applyBtn = document.getElementById('llm-params-apply');
            if (applyBtn) {
                applyBtn.addEventListener('click', () => {
                    this.applyParamOverrides();
                    popover.style.display = 'none';
                });
            }
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
            let maxCtx = document.getElementById('llm-param-max-context');
            if (maxCtx) { maxCtx.value = params.maxContextMessages ?? 0; }
        },

        applyParamOverrides() {
            let temp = document.getElementById('llm-param-temperature');
            let maxTok = document.getElementById('llm-param-max-tokens');
            let topP = document.getElementById('llm-param-top-p');
            let maxCtx = document.getElementById('llm-param-max-context');
            this.currentThreadParams = {
                temperature: temp ? parseFloat(temp.value) : 1.0,
                maxTokens: maxTok ? parseInt(maxTok.value, 10) : 1024,
                topP: topP ? parseFloat(topP.value) : 0.9,
                maxContextMessages: maxCtx ? parseInt(maxCtx.value, 10) || 0 : 0
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

        // -- Thread rename --

        initThreadRename() {
            let titleEl = document.getElementById('llm-thread-title');
            if (!titleEl) return;
            titleEl.addEventListener('click', () => {
                if (titleEl.querySelector('input')) return;
                let current = titleEl.textContent;
                if (current === 'LLM Assistant' && !this.currentThreadId) return;
                let input = document.createElement('input');
                input.type = 'text';
                input.className = 'llm-thread-title-input';
                input.value = current;
                titleEl.textContent = '';
                titleEl.appendChild(input);
                input.focus();
                input.select();
                let commit = () => {
                    let newTitle = input.value.trim() || current;
                    titleEl.textContent = newTitle;
                    if (this.currentThreadId && LLM.Chat) {
                        LLM.Chat.customTitle = true;
                        LLM.Chat.autoSaveThread();
                    }
                    if (LLM.Threads) LLM.Threads.refreshList();
                };
                input.addEventListener('blur', commit);
                input.addEventListener('keydown', e => {
                    if (e.key === 'Enter') { e.preventDefault(); input.blur(); }
                    if (e.key === 'Escape') { e.preventDefault(); titleEl.textContent = current; }
                });
            });
        },

        // -- Instruction indicator --

        getActiveInstructionName() {
            let id = this.settings?.featureMappings?.['chat-mode'] || 'chat';
            if (this.instructions) {
                let instr = this.instructions.find(i => i.id === id);
                if (instr) return instr.title;
            }
            return id.charAt(0).toUpperCase() + id.slice(1);
        },

        // -- Context bar --

        updateContextBar() {
            let bar = document.getElementById('llm-context-bar');
            if (!bar) return;
            let msgCount = LLM.Chat?.messages?.length || 0;
            let maxCtx = this.currentThreadParams?.maxContextMessages
                || this.settings?.parameters?.maxContextMessages || 0;
            let parts = [];
            if (maxCtx > 0) {
                let included = Math.min(msgCount, maxCtx);
                parts.push(`${included}/${msgCount} in context`);
            } else {
                parts.push(`${msgCount} message${msgCount !== 1 ? 's' : ''}`);
            }
            let backendName = this.getSelectedBackendName();
            if (backendName) {
                parts.push(backendName);
            }
            let instrName = this.getActiveInstructionName();
            if (instrName) parts.push(instrName + ' mode');
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

        // -- Mobile layout --

        isMobile() {
            return window.innerWidth <= 768;
        },

        initMobileLayout() {
            let sidebar = document.getElementById('llm-sidebar');
            if (!sidebar) return;
            // Collapse sidebar by default on mobile
            if (this.isMobile()) {
                sidebar.classList.add('collapsed');
            }
            // Add backdrop overlay for mobile sidebar
            let backdrop = document.createElement('div');
            backdrop.className = 'llm-sidebar-backdrop';
            backdrop.style.display = 'none';
            sidebar.parentElement.appendChild(backdrop);
            backdrop.addEventListener('click', () => {
                sidebar.classList.add('collapsed');
                backdrop.style.display = 'none';
            });
            // Update backdrop when sidebar toggles
            let toggle = document.getElementById('llm-sidebar-toggle');
            if (toggle) {
                toggle.addEventListener('click', () => {
                    if (this.isMobile()) {
                        backdrop.style.display = sidebar.classList.contains('collapsed') ? '' : 'none';
                    }
                });
            }
        },

        closeMobileSidebar() {
            if (!this.isMobile()) return;
            let sidebar = document.getElementById('llm-sidebar');
            let backdrop = document.querySelector('.llm-sidebar-backdrop');
            if (sidebar) sidebar.classList.add('collapsed');
            if (backdrop) backdrop.style.display = 'none';
        },

        // -- Keyboard shortcuts --

        initKeyboardShortcuts() {
            document.addEventListener('keydown', e => {
                let container = document.getElementById('llm-assistant-container');
                if (!container || container.offsetParent === null) return;
                if (e.key === 'Escape') {
                    if (LLM.Chat?.isStreaming) {
                        LLM.Chat.stopStreaming();
                    } else {
                        let popover = document.getElementById('llm-params-popover');
                        let dropdown = document.getElementById('llm-export-dropdown');
                        if (popover) popover.style.display = 'none';
                        if (dropdown) dropdown.style.display = 'none';
                    }
                }
                if (e.ctrlKey && e.shiftKey && e.key === 'N') {
                    e.preventDefault();
                    if (LLM.Threads) LLM.Threads.createNew();
                    document.getElementById('llm-input')?.focus();
                }
                if (e.ctrlKey && e.shiftKey && e.key === 'S') {
                    e.preventDefault();
                    let sidebar = document.getElementById('llm-sidebar');
                    if (sidebar) sidebar.classList.toggle('collapsed');
                }
            });
        },

        async exportThread(format) {
            if (!this.currentThreadId || !LLM.Chat?.messages?.length) {
                showError('No messages to export.');
                return;
            }
            try {
                let resp = await this.APIClient.exportThread(this.currentThreadId, format);
                let mimeType = format === 'json' ? 'application/json' : 'text/markdown';
                let blob = new Blob([resp.content], { type: mimeType });
                let url = URL.createObjectURL(blob);
                let a = document.createElement('a');
                a.href = url;
                a.download = resp.filename;
                a.click();
                URL.revokeObjectURL(url);
            } catch (ex) {
                console.error('[LLMAssistant] Export failed:', ex);
                showError('Export failed: ' + ex.message);
            }
        },

        generateId() {
            return crypto.randomUUID().replace(/-/g, '');
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

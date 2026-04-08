/**
 * LLM Assistant - Settings modal
 * Model picker, parameter sliders, instructions editor, feature mapping.
 */
(function() {
    'use strict';

    const Settings = {
        editingInstructionId: null,

        init() {
            this.bindEvents();
        },

        bindEvents() {
            let settingsBtn = document.getElementById('llm-settings-btn');
            let closeBtn = document.getElementById('llm-settings-close');
            let saveBtn = document.getElementById('llm-settings-save');
            let resetBtn = document.getElementById('llm-settings-reset');
            if (settingsBtn) settingsBtn.addEventListener('click', () => this.open());
            if (closeBtn) closeBtn.addEventListener('click', () => this.close());
            if (saveBtn) saveBtn.addEventListener('click', () => this.saveAndClose());
            if (resetBtn) resetBtn.addEventListener('click', () => this.resetDefaults());
            // Tab switching
            document.querySelectorAll('.llm-settings-tab').forEach(tab => {
                tab.addEventListener('click', () => this.switchTab(tab.dataset.tab));
            });
            // Range slider value display
            let tempSlider = document.getElementById('llm-setting-temperature');
            let topPSlider = document.getElementById('llm-setting-top-p');
            if (tempSlider) {
                tempSlider.addEventListener('input', () => {
                    let val = document.getElementById('llm-setting-temperature-val');
                    if (val) val.textContent = tempSlider.value;
                });
            }
            if (topPSlider) {
                topPSlider.addEventListener('input', () => {
                    let val = document.getElementById('llm-setting-top-p-val');
                    if (val) val.textContent = topPSlider.value;
                });
            }
            // Instruction editor
            let createBtn = document.getElementById('llm-create-instruction');
            let instrSave = document.getElementById('llm-instr-save');
            let instrCancel = document.getElementById('llm-instr-cancel');
            if (createBtn) createBtn.addEventListener('click', () => this.showInstructionEditor(null));
            if (instrSave) instrSave.addEventListener('click', () => this.saveInstruction());
            if (instrCancel) instrCancel.addEventListener('click', () => this.hideInstructionEditor());
            // Close modal on overlay click
            let overlay = document.getElementById('llm-settings-modal');
            if (overlay) {
                overlay.addEventListener('click', e => {
                    if (e.target === overlay) this.close();
                });
            }
        },

        open() {
            let modal = document.getElementById('llm-settings-modal');
            if (modal) modal.style.display = '';
            this.loadSettingsIntoUI();
            this.loadInstructionList();
            this.loadFeatureMappings();
        },

        close() {
            let modal = document.getElementById('llm-settings-modal');
            if (modal) modal.style.display = 'none';
            this.hideInstructionEditor();
        },

        switchTab(tabName) {
            document.querySelectorAll('.llm-settings-tab').forEach(t => t.classList.remove('active'));
            document.querySelectorAll('.llm-settings-panel').forEach(p => p.style.display = 'none');
            let tab = document.querySelector(`.llm-settings-tab[data-tab="${tabName}"]`);
            let panel = document.getElementById(`llm-settings-${tabName}`);
            if (tab) tab.classList.add('active');
            if (panel) panel.style.display = '';
        },

        loadSettingsIntoUI() {
            let s = LLM.settings || {};
            let params = s.parameters || {};
            let temp = document.getElementById('llm-setting-temperature');
            let tempVal = document.getElementById('llm-setting-temperature-val');
            let maxTok = document.getElementById('llm-setting-max-tokens');
            let topP = document.getElementById('llm-setting-top-p');
            let topPVal = document.getElementById('llm-setting-top-p-val');
            if (temp) { temp.value = params.temperature ?? 1.0; }
            if (tempVal) { tempVal.textContent = params.temperature ?? 1.0; }
            if (maxTok) { maxTok.value = params.maxTokens ?? 1024; }
            if (topP) { topP.value = params.topP ?? 0.9; }
            if (topPVal) { topPVal.textContent = params.topP ?? 0.9; }
        },

        gatherSettingsFromUI() {
            let s = JSON.parse(JSON.stringify(LLM.settings || {}));
            if (!s.parameters) s.parameters = {};
            let temp = document.getElementById('llm-setting-temperature');
            let maxTok = document.getElementById('llm-setting-max-tokens');
            let topP = document.getElementById('llm-setting-top-p');
            if (temp) s.parameters.temperature = parseFloat(temp.value);
            if (maxTok) s.parameters.maxTokens = parseInt(maxTok.value, 10);
            if (topP) s.parameters.topP = parseFloat(topP.value);
            // Gather feature mappings
            let mappings = {};
            document.querySelectorAll('#llm-feature-mappings select').forEach(sel => {
                mappings[sel.dataset.feature] = sel.value;
            });
            if (Object.keys(mappings).length > 0) {
                s.featureMappings = mappings;
            }
            return s;
        },

        async saveAndClose() {
            let settings = this.gatherSettingsFromUI();
            try {
                await LLM.APIClient.saveSettings(settings);
                LLM.settings = settings;
                this.close();
            } catch (ex) {
                showError('Failed to save settings: ' + ex.message);
            }
        },

        async resetDefaults() {
            if (!confirm('Reset all settings to defaults?')) return;
            try {
                let resp = await LLM.APIClient.resetSettings();
                LLM.settings = resp.settings;
                this.loadSettingsIntoUI();
                this.loadInstructionList();
                this.loadFeatureMappings();
            } catch (ex) {
                showError('Failed to reset settings: ' + ex.message);
            }
        },

        // -- Instructions --

        async loadInstructionList() {
            let container = document.getElementById('llm-instruction-list');
            if (!container) return;
            try {
                let resp = await LLM.APIClient.getInstructions();
                let instructions = resp.instructions || [];
                container.innerHTML = '';
                for (let instr of instructions) {
                    let item = document.createElement('div');
                    item.className = 'llm-instruction-item';
                    let title = document.createElement('span');
                    title.className = 'llm-instruction-title';
                    title.textContent = instr.title || instr.id;
                    let badge = document.createElement('span');
                    badge.className = 'llm-instruction-badge';
                    badge.textContent = instr.isBuiltIn ? 'built-in' : 'custom';
                    let editBtn = document.createElement('button');
                    editBtn.className = 'llm-msg-action';
                    editBtn.textContent = 'Edit';
                    editBtn.addEventListener('click', () => this.showInstructionEditor(instr));
                    item.appendChild(title);
                    item.appendChild(badge);
                    item.appendChild(editBtn);
                    if (!instr.isBuiltIn) {
                        let delBtn = document.createElement('button');
                        delBtn.className = 'llm-msg-action';
                        delBtn.textContent = 'Delete';
                        delBtn.addEventListener('click', () => this.deleteInstruction(instr.id));
                        item.appendChild(delBtn);
                    }
                    container.appendChild(item);
                }
            } catch (ex) {
                container.innerHTML = '<p class="llm-error">Failed to load instructions</p>';
            }
        },

        showInstructionEditor(instr) {
            let editor = document.getElementById('llm-instruction-editor');
            let titleInput = document.getElementById('llm-instr-title');
            let contentInput = document.getElementById('llm-instr-content');
            let tooltipInput = document.getElementById('llm-instr-tooltip');
            if (!editor) return;
            editor.style.display = '';
            this.editingInstructionId = instr ? instr.id : null;
            if (titleInput) titleInput.value = instr ? (instr.title || instr.id) : '';
            if (contentInput) contentInput.value = instr ? (instr.content || '') : '';
            if (tooltipInput) tooltipInput.value = instr ? (instr.tooltip || '') : '';
            if (instr && instr.isBuiltIn && titleInput) {
                titleInput.disabled = true;
            } else if (titleInput) {
                titleInput.disabled = false;
            }
        },

        hideInstructionEditor() {
            let editor = document.getElementById('llm-instruction-editor');
            if (editor) editor.style.display = 'none';
            this.editingInstructionId = null;
        },

        async saveInstruction() {
            let titleInput = document.getElementById('llm-instr-title');
            let contentInput = document.getElementById('llm-instr-content');
            let tooltipInput = document.getElementById('llm-instr-tooltip');
            let title = titleInput?.value?.trim();
            let content = contentInput?.value?.trim();
            let tooltip = tooltipInput?.value?.trim();
            if (!title || !content) {
                showError('Title and content are required.');
                return;
            }
            try {
                await LLM.APIClient.saveInstruction({
                    id: this.editingInstructionId,
                    title, content, tooltip
                });
                this.hideInstructionEditor();
                this.loadInstructionList();
            } catch (ex) {
                showError('Failed to save instruction: ' + ex.message);
            }
        },

        async deleteInstruction(id) {
            if (!confirm('Delete this instruction?')) return;
            try {
                await LLM.APIClient.deleteInstruction(id);
                this.loadInstructionList();
            } catch (ex) {
                showError('Failed to delete instruction: ' + ex.message);
            }
        },

        // -- Feature Mappings --

        async loadFeatureMappings() {
            let container = document.getElementById('llm-feature-mappings');
            if (!container) return;
            let mappings = LLM.settings?.featureMappings || {};
            let features = [
                { key: 'enhance-prompt', label: 'Enhance Prompt' },
                { key: 'magic-vision', label: 'Magic Vision' },
                { key: 'chat-mode', label: 'Chat Mode' },
                { key: 'vision-mode', label: 'Vision Mode' },
                { key: 'prompt-mode', label: 'Prompt Mode' },
                { key: 'random-prompt', label: 'Random Prompt' }
            ];
            // Get available instructions for the dropdowns
            let instructions = [];
            try {
                let resp = await LLM.APIClient.getInstructions();
                instructions = resp.instructions || [];
            } catch (ex) {
                // Fall back to built-in keys
                instructions = [
                    { id: 'chat', title: 'Chat' },
                    { id: 'prompt', title: 'Prompt' },
                    { id: 'caption', title: 'Caption' },
                    { id: 'vision', title: 'Vision' },
                    { id: 'randomprompt', title: 'Random Prompt' }
                ];
            }
            container.innerHTML = '';
            for (let feature of features) {
                let row = document.createElement('div');
                row.className = 'llm-feature-row';
                let label = document.createElement('label');
                label.textContent = feature.label;
                let select = document.createElement('select');
                select.dataset.feature = feature.key;
                for (let instr of instructions) {
                    let opt = document.createElement('option');
                    opt.value = instr.id;
                    opt.textContent = instr.title || instr.id;
                    if (mappings[feature.key] === instr.id) {
                        opt.selected = true;
                    }
                    select.appendChild(opt);
                }
                row.appendChild(label);
                row.appendChild(select);
                container.appendChild(row);
            }
        }
    };

    if (!window.LLM) window.LLM = {};
    window.LLM.Settings = Settings;
})();

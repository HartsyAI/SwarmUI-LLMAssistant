/**
 * LLM Assistant - Prompt buttons for the Generate tab
 * Adds "Enhance Prompt" and "Magic Vision" buttons near the prompt box.
 */
(function() {
    'use strict';

    const PromptButtons = {
        buttonsInjected: false,

        init() {
            this.tryInjectButtons();
            // SwarmUI may load the generate tab lazily, so retry if needed
            if (!this.buttonsInjected) {
                let observer = new MutationObserver(() => {
                    if (!this.buttonsInjected) {
                        this.tryInjectButtons();
                        if (this.buttonsInjected) observer.disconnect();
                    }
                });
                observer.observe(document.body, { childList: true, subtree: true });
            }
        },

        tryInjectButtons() {
            // Look for the prompt box area in the Generate tab
            let promptBox = document.getElementById('alt_prompt_textbox');
            if (!promptBox) return;
            // Don't double-inject
            if (document.getElementById('llm-enhance-prompt-btn')) {
                this.buttonsInjected = true;
                return;
            }
            // Find the nearest container to insert buttons
            let parent = promptBox.closest('.prompt-area, .prompt_box_area, .input-group');
            if (!parent) parent = promptBox.parentElement;
            if (!parent) return;
            let btnRow = document.createElement('div');
            btnRow.className = 'llm-prompt-btn-row';
            // Enhance Prompt button
            let enhanceBtn = document.createElement('button');
            enhanceBtn.id = 'llm-enhance-prompt-btn';
            enhanceBtn.className = 'basic-button llm-prompt-action-btn';
            enhanceBtn.textContent = 'Enhance Prompt';
            enhanceBtn.title = 'Use LLM to improve the current prompt';
            enhanceBtn.addEventListener('click', () => this.enhancePrompt());
            // Random Prompt button
            let randomBtn = document.createElement('button');
            randomBtn.id = 'llm-random-prompt-btn';
            randomBtn.className = 'basic-button llm-prompt-action-btn';
            randomBtn.textContent = 'Random Prompt';
            randomBtn.title = 'Generate a random creative prompt';
            randomBtn.addEventListener('click', () => this.randomPrompt());
            btnRow.appendChild(enhanceBtn);
            btnRow.appendChild(randomBtn);
            parent.appendChild(btnRow);
            this.buttonsInjected = true;
        },

        async enhancePrompt() {
            let promptBox = document.getElementById('alt_prompt_textbox');
            let currentPrompt = promptBox?.value?.trim();
            if (!currentPrompt) {
                showError('Enter a prompt first.');
                return;
            }
            let btn = document.getElementById('llm-enhance-prompt-btn');
            if (btn) { btn.disabled = true; btn.textContent = 'Enhancing...'; }
            let instructionId = LLM.settings?.featureMappings?.['enhance-prompt'] || 'prompt';
            try {
                let resp = await LLM.APIClient.sendMessage(currentPrompt, instructionId);
                if (resp.response && promptBox) {
                    promptBox.value = resp.response;
                    triggerChangeFor(promptBox);
                }
            } catch (ex) {
                showError('Enhance failed: ' + ex.message);
            } finally {
                if (btn) { btn.disabled = false; btn.textContent = 'Enhance Prompt'; }
            }
        },

        async randomPrompt() {
            let btn = document.getElementById('llm-random-prompt-btn');
            if (btn) { btn.disabled = true; btn.textContent = 'Generating...'; }
            let instructionId = LLM.settings?.featureMappings?.['random-prompt'] || 'randomprompt';
            try {
                let resp = await LLM.APIClient.sendMessage('Generate a random prompt.', instructionId);
                let promptBox = document.getElementById('alt_prompt_textbox');
                if (resp.response && promptBox) {
                    promptBox.value = resp.response;
                    triggerChangeFor(promptBox);
                }
            } catch (ex) {
                showError('Random prompt failed: ' + ex.message);
            } finally {
                if (btn) { btn.disabled = false; btn.textContent = 'Random Prompt'; }
            }
        }
    };

    if (!window.LLM) window.LLM = {};
    window.LLM.PromptButtons = PromptButtons;
})();

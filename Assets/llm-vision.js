/**
 * LLM Assistant - Vision panel
 * Image upload, caption, and actions.
 */
(function() {
    'use strict';

    const Vision = {
        currentImage: null,
        currentImageData: null,

        init() {
            this.bindEvents();
        },

        bindEvents() {
            let dropzone = document.getElementById('llm-vision-dropzone');
            let fileInput = document.getElementById('llm-vision-file');
            if (dropzone) {
                dropzone.addEventListener('click', () => fileInput?.click());
                dropzone.addEventListener('dragover', e => { e.preventDefault(); dropzone.classList.add('drag-over'); });
                dropzone.addEventListener('dragleave', () => dropzone.classList.remove('drag-over'));
                dropzone.addEventListener('drop', e => {
                    e.preventDefault();
                    dropzone.classList.remove('drag-over');
                    let files = e.dataTransfer?.files;
                    if (files?.length > 0) this.handleFile(files[0]);
                });
            }
            if (fileInput) {
                fileInput.addEventListener('change', () => {
                    if (fileInput.files?.length > 0) this.handleFile(fileInput.files[0]);
                });
            }
            let captionBtn = document.getElementById('llm-vision-caption');
            let promptBtn = document.getElementById('llm-vision-prompt');
            let initBtn = document.getElementById('llm-vision-init');
            let clearBtn = document.getElementById('llm-vision-clear');
            if (captionBtn) captionBtn.addEventListener('click', () => this.generateCaption());
            if (promptBtn) promptBtn.addEventListener('click', () => this.sendToPrompt());
            if (initBtn) initBtn.addEventListener('click', () => this.useAsInit());
            if (clearBtn) clearBtn.addEventListener('click', () => this.clearImage());
        },

        handleFile(file) {
            if (!file.type.startsWith('image/')) {
                showError('Please upload an image file.');
                return;
            }
            let reader = new FileReader();
            reader.onload = e => {
                this.currentImageData = e.target.result;
                this.showPreview(e.target.result);
            };
            reader.readAsDataURL(file);
        },

        showPreview(dataUrl) {
            let dropzone = document.getElementById('llm-vision-dropzone');
            let preview = document.getElementById('llm-vision-preview');
            let img = document.getElementById('llm-vision-image');
            if (dropzone) dropzone.style.display = 'none';
            if (preview) preview.style.display = '';
            if (img) img.src = dataUrl;
        },

        async generateCaption() {
            if (!this.currentImageData) return;
            let output = document.getElementById('llm-vision-output');
            if (output) output.innerHTML = '<p class="llm-loading">Generating caption...</p>';
            let instructionId = LLM.settings?.featureMappings?.['caption'] || 'caption';
            try {
                let resp = await LLM.APIClient.sendMessage(
                    'Describe this image in detail.',
                    instructionId
                );
                if (output && resp.response) {
                    LLM.renderIntoElement(resp.response, output);
                    this.lastCaption = resp.response;
                }
            } catch (ex) {
                if (output) output.innerHTML = `<p class="llm-error">Error: ${ex.message}</p>`;
            }
        },

        sendToPrompt() {
            let caption = this.lastCaption;
            if (!caption) {
                showError('Generate a caption first.');
                return;
            }
            let promptBox = document.getElementById('alt_prompt_textbox');
            if (promptBox) {
                promptBox.value = caption;
                triggerChangeFor(promptBox);
            }
        },

        useAsInit() {
            if (!this.currentImageData) return;
            // Use SwarmUI's image system if available
            if (typeof setCurrentImage === 'function') {
                setCurrentImage(this.currentImageData);
            }
        },

        clearImage() {
            this.currentImage = null;
            this.currentImageData = null;
            this.lastCaption = null;
            let dropzone = document.getElementById('llm-vision-dropzone');
            let preview = document.getElementById('llm-vision-preview');
            let output = document.getElementById('llm-vision-output');
            let fileInput = document.getElementById('llm-vision-file');
            if (dropzone) dropzone.style.display = '';
            if (preview) preview.style.display = 'none';
            if (output) output.innerHTML = '';
            if (fileInput) fileInput.value = '';
        }
    };

    if (!window.LLM) window.LLM = {};
    window.LLM.Vision = Vision;
})();

/* ================================================================
   LLM Assistant - chat/chat-attachments.js
   Image attach/paste/drop, the quick-caption popover, upload, and
   send-to-prompt-box integration.
   ================================================================ */

(function () {
    'use strict';

    // Caption styles. Mirrors CaptionImageTool.StyleInstructions on the server. If you add a style
    // in C# also add it here so the dropdown stays in sync (defaults match the server's enum).
    const LLMA_CAPTION_STYLES = [
        ['natural', 'Natural'],
        ['detailed', 'Detailed'],
        ['simple', 'Simple'],
        ['danbooru', 'Danbooru tags'],
        ['artistic', 'Artistic style'],
        ['technical', 'Technical'],
        ['color-palette', 'Color palette'],
        ['facial-features', 'Facial features'],
        ['lora-trigger', 'LoRA trigger'],
        ['story', 'Story']
    ];

    /** Intercept an image paste and attach it. */
    function llmaHandlePaste(e) {
        const items = e.clipboardData?.items;
        if (!items) return;
        for (const item of items) {
            if (item.type.startsWith('image/')) {
                e.preventDefault();
                llmaHandleAttach(item.getAsFile());
                return;
            }
        }
    }

    /** Read an image file into the attachment state + show its preview. */
    async function llmaHandleAttach(file) {
        if (!file.type.startsWith('image/')) return;
        const dataUrl = await llmaFileToBase64(file);
        LLMAState.attachedImage = {
            base64:    llmaDataUrlToBase64(dataUrl),
            mediaType: llmaDataUrlMediaType(dataUrl),
            dataUrl:   dataUrl,
        };
        llmaShowAttachmentPreview(dataUrl);
        const attachFile = document.getElementById('llma-attach-file');
        if (attachFile) attachFile.value = '';
    }

    /** Render the attachment thumbnail + its action buttons (caption / use-as-prompt / init / remove). */
    function llmaShowAttachmentPreview(dataUrl) {
        const preview = document.getElementById('llma-attachment-preview');
        if (!preview) return;
        preview.innerHTML = '';
        preview.style.display = '';

        const img = document.createElement('img');
        img.src = dataUrl;
        preview.appendChild(img);

        // "Caption ▾" opens an inline style picker that runs caption_image directly via the tool
        // execute endpoint — no chat round-trip, no thread persistence.
        const captionWrap = document.createElement('div');
        captionWrap.className = 'llma-attach-caption-wrap';
        captionWrap.appendChild(llmaCreateActionBtn('Caption ▾', () => llmaToggleQuickCaption(captionWrap)));
        preview.appendChild(captionWrap);

        preview.appendChild(llmaCreateActionBtn('Use as Prompt', () => llmaCaptionAndSendToPrompt()));
        preview.appendChild(llmaCreateActionBtn('Use as Init', () => {
            if (LLMAState.attachedImage && typeof setCurrentImage === 'function') {
                setCurrentImage(LLMAState.attachedImage.dataUrl);
            }
        }));
        preview.appendChild(llmaCreateActionBtn('×', () => llmaClearAttachment(), 'llma-attachment-remove'));
    }

    /**
     * Toggle the quick-caption popover under the "Caption" button. Renders style picker + Run +
     * (after run) the caption text + actions. File-private.
     */
    function llmaToggleQuickCaption(wrap) {
        if (!LLMAState.attachedImage) return;
        let panel = wrap.querySelector('.llma-quick-caption-panel');
        if (panel) {
            panel.remove();
            return;
        }
        panel = document.createElement('div');
        panel.className = 'llma-quick-caption-panel';
        panel.innerHTML = `
            <select class="llma-quick-caption-style">
                ${LLMA_CAPTION_STYLES.map(([k, label]) => `<option value="${llmaEscapeHtml(k)}">${llmaEscapeHtml(label)}</option>`).join('')}
            </select>
            <button class="basic-button llma-quick-caption-run">Run</button>
            <div class="llma-quick-caption-result" style="display:none;"></div>
        `;
        wrap.appendChild(panel);
        panel.querySelector('.llma-quick-caption-run').addEventListener('click', async () => {
            const style = panel.querySelector('.llma-quick-caption-style').value;
            const result = panel.querySelector('.llma-quick-caption-result');
            result.style.display = '';
            result.textContent = 'Captioning…';
            try {
                // Pass the data URI directly — ImageInputResolver accepts it, no upload needed.
                const res = await llmaRequest('LLMAssistantExecuteTool', {
                    toolId: 'caption_image',
                    arguments: JSON.stringify({
                        image: LLMAState.attachedImage.dataUrl,
                        style: style
                    })
                });
                const inner = res?.result || res;
                if (inner?.success && inner.caption) {
                    llmaRenderQuickCaptionResult(result, inner.caption);
                } else {
                    result.textContent = 'Error: ' + (inner?.error || 'unknown');
                }
            } catch (ex) {
                result.textContent = 'Error: ' + (ex?.message || String(ex));
            }
        });
    }

    /** Render caption text + copy / send-to-chat / send-to-prompt actions. File-private. */
    function llmaRenderQuickCaptionResult(container, caption) {
        container.innerHTML = '';
        const text = document.createElement('div');
        text.className = 'llma-quick-caption-text';
        text.textContent = caption;
        container.appendChild(text);
        const actions = document.createElement('div');
        actions.className = 'llma-quick-caption-actions';
        const copyBtn = llmaCreateActionBtn('Copy', () => {
            navigator.clipboard.writeText(caption).then(() => llmaShowToast('Copied', 'success'));
        }, 'basic-button');
        const chatBtn = llmaCreateActionBtn('Send to chat', () => {
            const input = document.getElementById('llma-input');
            if (input) input.value = caption;
        }, 'basic-button');
        const promptBtn = llmaCreateActionBtn('Send to prompt', () => llmaSendToPromptBox(caption), 'basic-button');
        actions.append(copyBtn, chatBtn, promptBtn);
        container.appendChild(actions);
    }

    /** Clear the attachment state + hide the preview. */
    function llmaClearAttachment() {
        LLMAState.attachedImage = null;
        const preview = document.getElementById('llma-attachment-preview');
        if (preview) { preview.innerHTML = ''; preview.style.display = 'none'; }
    }

    /**
     * Upload the currently-attached image to the per-user uploads dir; returns `{ url, mediaType }`
     * (the shape both the WS media payload and the DOM render expect), or null on failure (toast
     * surfaced). Caller must clear the attachment after a successful return.
     */
    async function llmaUploadAttachedImage(messageId) {
        if (!LLMAState.attachedImage) return null;
        if (!LLMAState.activeThreadId) {
            llmaShowToast('Cannot upload image without an active thread', 'error');
            return null;
        }
        try {
            const result = await llmaRequest('LLMAssistantUploadChatImage', {
                threadId: LLMAState.activeThreadId,
                messageId,
                imageData: LLMAState.attachedImage.dataUrl,
            });
            if (result?.success && result.url) {
                return { url: result.url, mediaType: result.mediaType || LLMAState.attachedImage.mediaType };
            }
            llmaShowToast(result?.error || 'Image upload failed', 'error');
        } catch {
            llmaShowToast('Image upload failed', 'error');
        }
        return null;
    }

    /** Caption the attached image into the prompt box (creates a thread if needed). File-private. */
    function llmaCaptionAndSendToPrompt() {
        if (!LLMAState.attachedImage) return;
        llmaShowChatPanel();
        if (!LLMAState.activeThreadId) {
            llmaCreateThread(LLMAState.activeAssistantId).then(() => llmaDoCaptionPrompt());
        } else {
            llmaDoCaptionPrompt();
        }
    }

    /** Run the caption-for-prompt flow: upload, post a user message, stream, pipe result to prompt box. */
    async function llmaDoCaptionPrompt() {
        const captionMsg = 'Describe this image for use as an image generation prompt.';
        const userMsgId = llmaGenerateId();
        const upload = await llmaUploadAttachedImage(userMsgId);
        if (!upload) return;
        LLMAState.messages.push({ id: userMsgId, role: 'user', content: captionMsg, timestamp: new Date().toISOString(), media: [upload] });
        llmaAppendMessageToDOM('user', captionMsg, upload.url, userMsgId);
        llmaClearAttachment();
        const payload = llmaBuildPayload(captionMsg, 'caption');
        payload.media = [upload];
        llmaStreamResponse(payload, text => llmaSendToPromptBox(text));
    }

    /** Push text into the main SwarmUI prompt box. */
    function llmaSendToPromptBox(text) {
        const promptBox = document.getElementById('alt_prompt_textbox');
        if (!promptBox) return;
        promptBox.value = text;
        if (typeof triggerChangeFor === 'function') triggerChangeFor(promptBox);
    }

    // --- Public API (called by sibling files + other chat/ modules) ---
    window.llmaHandlePaste         = llmaHandlePaste;
    window.llmaHandleAttach        = llmaHandleAttach;
    window.llmaClearAttachment     = llmaClearAttachment;
    window.llmaUploadAttachedImage = llmaUploadAttachedImage;
    window.llmaSendToPromptBox     = llmaSendToPromptBox;
})();

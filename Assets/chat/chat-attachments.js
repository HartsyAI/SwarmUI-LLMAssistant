/* ================================================================
   LLM Assistant - chat/chat-attachments.js
   Image and video attach/paste/drop, the quick-caption popover (images),
   upload, and send-to-prompt-box integration.
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

    // Video container types the server accepts (MediaStorageService), keyed by file extension for files whose type is empty.
    const LLMA_VIDEO_EXT_MIME = { mp4: 'video/mp4', m4v: 'video/mp4', webm: 'video/webm', mov: 'video/quicktime' };
    const LLMA_VIDEO_MIMES = ['video/mp4', 'video/webm', 'video/quicktime'];

    /** 'image', 'video' or null for a file. Video also matches by extension when the browser reports no type or a generic
     *  application/octet-stream (common for .mov), the same fallback llmaVideoMime applies. */
    function llmaAttachKind(file) {
        const type = (file?.type || '').toLowerCase();
        if (type.startsWith('image/')) return 'image';
        if (type.startsWith('video/')) return 'video';
        if ((!type || type === 'application/octet-stream') && LLMA_VIDEO_EXT_MIME[(file?.name || '').split('.').pop().toLowerCase()]) return 'video';
        return null;
    }

    /** The server-accepted MIME for a video file (from its type, else its extension), or null when it is not mp4/webm/mov. */
    function llmaVideoMime(file) {
        const type = (file.type || '').toLowerCase();
        if (LLMA_VIDEO_MIMES.includes(type)) return type;
        if (!type || type === 'application/octet-stream') return LLMA_VIDEO_EXT_MIME[(file.name || '').split('.').pop().toLowerCase()] || null;
        return null;
    }

    /** Whether the active chat model takes video (strict: its metadata must say so). */
    function llmaCurrentModelTakesVideo() {
        const model = (LLMAState.availableModels || []).find(m => m.id === LLMAState.currentModel);
        return llmaIsVideoModel(model);
    }

    /** Intercept an image or video paste and attach it. */
    function llmaHandlePaste(e) {
        const items = e.clipboardData?.items;
        if (!items) return;
        for (const item of items) {
            if (item.kind === 'file' && (item.type.startsWith('image/') || item.type.startsWith('video/'))) {
                e.preventDefault();
                llmaHandleAttach(item.getAsFile());
                return;
            }
        }
    }

    /** Read an image or video file into the attachment state + show its preview. */
    async function llmaHandleAttach(file) {
        const kind = llmaAttachKind(file);
        if (!kind) return;
        const attachFile = document.getElementById('llma-attach-file');
        if (kind === 'video') {
            if (!llmaCurrentModelTakesVideo()) {
                llmaShowToast("This model doesn't support video input. Pick a video model such as MOSS-VL.", 'info');
                if (attachFile) attachFile.value = '';
                return;
            }
            const mediaType = llmaVideoMime(file);
            if (!mediaType) {
                llmaShowToast('Unsupported video format. Use mp4, webm or mov.', 'error');
                if (attachFile) attachFile.value = '';
                return;
            }
            if (file.size > LLMA_MAX_VIDEO_BYTES) {
                llmaShowToast(`${translate('Video is too large')} (${llmaFormatBytes(file.size)}). ${translate('The limit is 64 MB.')}`, 'error');
                if (attachFile) attachFile.value = '';
                return;
            }
            llmaReleaseAttachment();
            // The file is only read into a data URI at upload time; the preview plays straight from a blob URL.
            const objectUrl = URL.createObjectURL(file);
            LLMAState.attachedImage = { kind: 'video', file, mediaType, objectUrl, sizeBytes: file.size };
            llmaShowVideoPreview(objectUrl, file.size);
            if (attachFile) attachFile.value = '';
            return;
        }
        const dataUrl = await llmaFileToBase64(file);
        llmaReleaseAttachment();
        LLMAState.attachedImage = {
            kind:      'image',
            base64:    llmaDataUrlToBase64(dataUrl),
            mediaType: llmaDataUrlMediaType(dataUrl),
            dataUrl:   dataUrl,
        };
        llmaShowAttachmentPreview(dataUrl);
        if (attachFile) attachFile.value = '';
    }

    /** "12.3 MB" style size text. File-private. */
    function llmaFormatBytes(n) {
        if (n >= 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`;
        if (n >= 1024) return `${Math.round(n / 1024)} KB`;
        return `${n} B`;
    }

    /** Render the video attachment preview: a muted metadata-only player, a duration + size note, and the remove button.
     *  No caption / use-as-prompt / use-as-init actions: those are image-only. */
    function llmaShowVideoPreview(objectUrl, sizeBytes) {
        const preview = document.getElementById('llma-attachment-preview');
        if (!preview) return;
        preview.innerHTML = '';
        preview.style.display = '';

        const video = document.createElement('video');
        video.className = 'llma-attach-video';
        video.muted = true;
        video.playsInline = true;
        video.preload = 'metadata';
        // The #t fragment shows a frame instead of a black box before the clip is played.
        video.src = `${objectUrl}#t=0.1`;
        preview.appendChild(video);

        const note = document.createElement('div');
        note.className = 'llma-attach-note';
        note.textContent = llmaFormatBytes(sizeBytes);
        video.addEventListener('loadedmetadata', () => {
            if (!Number.isFinite(video.duration)) return;
            const secs = Math.round(video.duration);
            note.textContent = `${Math.floor(secs / 60)}:${String(secs % 60).padStart(2, '0')} · ${llmaFormatBytes(sizeBytes)}`;
        });
        preview.appendChild(note);
        preview.appendChild(llmaCreateActionBtn('×', () => llmaClearAttachment(), 'llma-attachment-remove'));
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

        // Nudge if the image is big enough that it'll cost real vision tokens. It gets downscaled to the
        // "Vision Image Max Size" setting on upload, so only warn when that cap is still large — i.e. when
        // lowering it would actually save tokens. Otherwise stay quiet.
        llmaMaybeWarnLargeImage(dataUrl, preview);

        // "Caption ▾" opens an inline style picker that runs caption_image directly via the tool
        // execute endpoint — no chat round-trip, no thread persistence.
        const captionWrap = document.createElement('div');
        captionWrap.className = 'llma-attach-caption-wrap';
        captionWrap.appendChild(llmaCreateActionBtn(`${translate('Caption')} ▾`, () => llmaToggleQuickCaption(captionWrap)));
        preview.appendChild(captionWrap);

        preview.appendChild(llmaCreateActionBtn(translate('Use as Prompt'), () => llmaCaptionAndSendToPrompt()));
        preview.appendChild(llmaCreateActionBtn(translate('Use as Init'), () => {
            if (LLMAState.attachedImage && typeof setCurrentImage === 'function') {
                setCurrentImage(LLMAState.attachedImage.dataUrl);
            }
        }));
        preview.appendChild(llmaCreateActionBtn('×', () => llmaClearAttachment(), 'llma-attachment-remove'));
    }

    /**
     * If the attached image is large enough to cost meaningful vision tokens, append a dismissible hint
     * that points at the "Vision Image Max Size" setting. Measures the source off-DOM; only warns when the
     * effective sent size (min of source long-edge and the current cap) still exceeds LLMA_LARGE_IMAGE_PX —
     * so a user who already lowered the cap, or a small image, sees nothing. File-private.
     */
    const LLMA_LARGE_IMAGE_PX = 1024;
    function llmaMaybeWarnLargeImage(dataUrl, preview) {
        const probe = new Image();
        probe.onload = () => {
            const longEdge = Math.max(probe.naturalWidth, probe.naturalHeight);
            const cap = parseInt(LLMAState.settings?.parameters?.imageMaxDimension, 10) || 1536;
            const effective = Math.min(longEdge, cap);
            if (effective <= LLMA_LARGE_IMAGE_PX) return;
            // Don't stack duplicates if the preview re-renders.
            if (preview.querySelector('.llma-attach-warn')) return;
            const warn = document.createElement('div');
            warn.className = 'llma-attach-warn';
            const msg = document.createElement('span');
            msg.textContent = `${translate('Large image')} (${probe.naturalWidth}×${probe.naturalHeight}, ${translate('sent at ~')}${effective}px). ${translate('Vision models bill by resolution — lower it to save tokens.')}`;
            const settingsLink = document.createElement('button');
            settingsLink.type = 'button';
            settingsLink.className = 'llma-attach-warn-link';
            settingsLink.textContent = translate('Adjust in Settings');
            settingsLink.addEventListener('click', () => { if (typeof llmaOpenSettings === 'function') llmaOpenSettings(); });
            const dismiss = document.createElement('button');
            dismiss.type = 'button';
            dismiss.className = 'llma-attach-warn-dismiss';
            dismiss.title = translate('Dismiss');
            dismiss.textContent = '×';
            dismiss.addEventListener('click', () => warn.remove());
            warn.append(msg, settingsLink, dismiss);
            preview.appendChild(warn);
        };
        probe.src = dataUrl;
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
                ${LLMA_CAPTION_STYLES.map(([k, label]) => `<option value="${llmaEscapeHtml(k)}">${llmaEscapeHtml(translate(label))}</option>`).join('')}
            </select>
            <button class="basic-button llma-quick-caption-run">${llmaEscapeHtml(translate('Run'))}</button>
            <div class="llma-quick-caption-result" style="display:none;"></div>
        `;
        wrap.appendChild(panel);
        panel.querySelector('.llma-quick-caption-run').addEventListener('click', async () => {
            const style = panel.querySelector('.llma-quick-caption-style').value;
            const result = panel.querySelector('.llma-quick-caption-result');
            result.style.display = '';
            result.textContent = translate('Captioning…');
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
                    result.textContent = translate('Error:') + ' ' + (inner?.error || translate('unknown'));
                }
            } catch (ex) {
                result.textContent = translate('Error:') + ' ' + (ex?.message || String(ex));
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
        const copyBtn = llmaCreateActionBtn(translate('Copy'), () => {
            navigator.clipboard.writeText(caption).then(() => llmaShowToast('Copied', 'success'));
        }, 'basic-button');
        const chatBtn = llmaCreateActionBtn(translate('Send to chat'), () => {
            const input = document.getElementById('llma-input');
            if (input) input.value = caption;
        }, 'basic-button');
        const promptBtn = llmaCreateActionBtn(translate('Send to prompt'), () => llmaSendToPromptBox(caption), 'basic-button');
        actions.append(copyBtn, chatBtn, promptBtn);
        container.appendChild(actions);
    }

    /** Drop the pending attachment's state, revoking a video's blob URL so the file can be freed. File-private. */
    function llmaReleaseAttachment() {
        const att = LLMAState.attachedImage;
        if (att?.objectUrl) URL.revokeObjectURL(att.objectUrl);
        LLMAState.attachedImage = null;
    }

    /** Clear the attachment state + hide the preview. */
    function llmaClearAttachment() {
        llmaReleaseAttachment();
        const preview = document.getElementById('llma-attachment-preview');
        if (preview) { preview.innerHTML = ''; preview.style.display = 'none'; }
    }

    /**
     * Upload the currently-attached image or video to the per-user uploads dir; returns `{ url, mediaType }`
     * (the shape both the WS media payload and the DOM render expect), or null on failure (toast
     * surfaced). Caller must clear the attachment after a successful return.
     */
    async function llmaUploadAttachedImage(messageId) {
        if (!LLMAState.attachedImage) return null;
        if (LLMAState.attachedImage.kind === 'video') return llmaUploadAttachedVideo(messageId);
        if (!LLMAState.activeThreadId) {
            llmaShowToast('Cannot upload image without an active chat', 'error');
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

    /** Upload the attached video through LLMAssistantUploadChatVideo (the clip is read into a data URI here, not at attach time). File-private. */
    async function llmaUploadAttachedVideo(messageId) {
        const att = LLMAState.attachedImage;
        if (!LLMAState.activeThreadId) {
            llmaShowToast('Cannot upload video without an active chat', 'error');
            return null;
        }
        if (att.sizeBytes > LLMA_MAX_VIDEO_BYTES) {
            llmaShowToast('Video is too large. The limit is 64 MB.', 'error');
            return null;
        }
        try {
            // slice() restamps the type so the data URI always carries the MIME the server's allowlist expects.
            const dataUrl = await llmaFileToBase64(att.file.slice(0, att.file.size, att.mediaType));
            const result = await llmaRequest('LLMAssistantUploadChatVideo', {
                threadId: LLMAState.activeThreadId,
                messageId,
                videoData: dataUrl,
            });
            if (result?.success && result.url) {
                return { url: result.url, mediaType: result.mediaType || att.mediaType };
            }
            llmaShowToast(result?.error || 'Video upload failed', 'error');
        } catch {
            llmaShowToast('Video upload failed', 'error');
        }
        return null;
    }

    /** Caption the attached image into the prompt box (creates a thread if needed). File-private. */
    function llmaCaptionAndSendToPrompt() {
        if (!LLMAState.attachedImage || LLMAState.attachedImage.kind === 'video') return;
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
        llmaAppendMessageToDOM('user', captionMsg, upload, userMsgId);
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
    window.llmaAttachKind          = llmaAttachKind;
    window.llmaClearAttachment     = llmaClearAttachment;
    window.llmaUploadAttachedImage = llmaUploadAttachedImage;
    window.llmaSendToPromptBox     = llmaSendToPromptBox;
})();

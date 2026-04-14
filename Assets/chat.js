/* ================================================================
   LLM Assistant - chat.js
   Message send, receive, stream, render, and input handling.
   ================================================================ */

'use strict';

// -- Render Message History --
function llmaRenderMessageHistory(messages) {
    const container = document.getElementById('llma-messages');
    if (!container) return;
    container.innerHTML = '';
    for (const msg of messages) {
        llmaAppendMessageToDOM(msg.role, msg.content, msg.imageBase64, msg.id, msg.meta);
        if (msg.role === 'assistant' && Array.isArray(msg.toolCalls) && msg.toolCalls.length > 0) {
            const bubble = document.querySelector(`[data-msg-id="${msg.id}"] .llma-msg-bubble`);
            if (bubble) llmaReplayToolCalls(bubble, msg.toolCalls);
        }
    }
    llmaRebuildAssetsForThread();
    // Refresh the empty-chat greeting: it shows when the rendered list is
    // empty and hides itself otherwise.
    if (typeof llmaRenderEmptyChatGreeting === 'function') {
        llmaRenderEmptyChatGreeting();
    }
    llmaScrollToBottom();
}

// -- Append Single Message (Claude-style layout) --
function llmaAppendMessageToDOM(role, content, imageBase64, msgId, meta) {
    const container = document.getElementById('llma-messages');
    if (!container) return null;

    // First message of the thread: hide the empty-chat greeting since the
    // CSS empty-state selectors only check the messages list (not state).
    const greet = document.getElementById('llma-empty-greeting');
    if (greet && greet.style.display !== 'none') {
        greet.style.display = 'none';
        greet.innerHTML = '';
    }

    const id = msgId || llmaGenerateId();
    const isUser = role === 'user';
    const assistant = LLMAState.assistants.find(a => a.id === LLMAState.activeAssistantId);

    // Message row — column layout; user right-aligned, assistant left-aligned
    const row = document.createElement('div');
    row.className = `llma-msg-row ${isUser ? 'user' : 'assistant'}`;
    row.setAttribute('data-msg-id', id);

    if (isUser) {
        // ── User: right-aligned pill, no avatar ──
        const bubble = document.createElement('div');
        bubble.className = 'llma-msg-bubble user-bubble';
        if (imageBase64) {
            const img = document.createElement('img');
            img.src = imageBase64;
            img.className = 'llma-msg-image';
            bubble.appendChild(img);
        }
        if (content) bubble.appendChild(document.createTextNode(content));

        const actions = document.createElement('div');
        actions.className = 'llma-msg-actions';
        actions.appendChild(llmaCreateActionBtn('Copy', () => llmaCopyMessage(id)));
        actions.appendChild(llmaCreateActionBtn('Edit', () => llmaStartEdit(id)));
        actions.appendChild(llmaCreateActionBtn('Del', () => llmaDeleteMessage(id)));

        row.appendChild(bubble);
        row.appendChild(actions);
    } else {
        // ── Assistant: header (avatar + name) → body (content + meta + actions) ──
        const header = document.createElement('div');
        header.className = 'llma-msg-header';

        const avatar = document.createElement('div');
        avatar.className = 'llma-msg-avatar ai-avatar';
        if (assistant?.avatar) {
            const img = document.createElement('img');
            img.src = assistant.avatar;
            avatar.appendChild(img);
        } else {
            avatar.style.background = assistant?.color || 'var(--emphasis)';
            avatar.textContent = llmaCategoryIcon(assistant?.icon || assistant?.category || 'chat');
        }
        const sender = document.createElement('span');
        sender.className = 'llma-msg-sender';
        sender.textContent = assistant?.name || 'Assistant';
        header.appendChild(avatar);
        header.appendChild(sender);

        const body = document.createElement('div');
        body.className = 'llma-msg-body';

        const bubble = document.createElement('div');
        bubble.className = 'llma-msg-bubble ai-bubble';
        if (content) {
            bubble.innerHTML = llmaRenderAssistantContent(content, id);
            llmaPostRenderMermaid(bubble);
        } else {
            bubble.innerHTML = '<div class="llma-typing"><span></span><span></span><span></span></div>';
        }

        const metaDiv = document.createElement('div');
        metaDiv.className = 'llma-msg-meta';
        if (meta) {
            const parts = [];
            if (meta.model) parts.push(meta.model);
            parts.push(`${meta.genTime || '?'}s`);
            metaDiv.textContent = parts.join(' \u00b7 ');
        }

        const actions = document.createElement('div');
        actions.className = 'llma-msg-actions';
        actions.appendChild(llmaCreateActionBtn('Copy', () => llmaCopyMessage(id)));
        actions.appendChild(llmaCreateActionBtn('Regen', () => llmaRegenerateMessage(id)));
        actions.appendChild(llmaCreateActionBtn('Use as Prompt', () => {
            const msg = LLMAState.messages.find(m => m.id === id);
            if (msg) llmaSendToPromptBox(msg.content);
        }));
        actions.appendChild(llmaCreateActionBtn('Del', () => llmaDeleteMessage(id)));

        body.appendChild(bubble);
        body.appendChild(metaDiv);
        body.appendChild(actions);
        row.appendChild(header);
        row.appendChild(body);
    }

    container.appendChild(row);
    llmaScrollToBottom();
    return id;
}

function llmaCreateActionBtn(text, onClick) {
    const btn = document.createElement('button');
    btn.className = 'llma-msg-action-btn';
    btn.textContent = text;
    btn.addEventListener('click', onClick);
    return btn;
}

// -- Setup Input Area --
function llmaSetupInput() {
    const input   = document.getElementById('llma-input');
    const sendBtn = document.getElementById('llma-send-btn');
    const stopBtn = document.getElementById('llma-stop-btn');

    if (sendBtn) sendBtn.addEventListener('click', () => llmaSendMessage());
    if (stopBtn) stopBtn.addEventListener('click', () => llmaStopGeneration());

    if (input) {
        input.addEventListener('keydown', e => {
            if (e.key === 'Enter' && !e.shiftKey && LLMAState.enterToSend) {
                e.preventDefault();
                llmaSendMessage();
            }
        });
        input.addEventListener('input', () => {
            input.style.height = 'auto';
            input.style.height = Math.min(input.scrollHeight, 140) + 'px';
            llmaUpdateCharCount(input.value);
        });
        input.addEventListener('paste', e => llmaHandlePaste(e));
    }

    // File attach
    const attachFile = document.getElementById('llma-attach-file');
    if (attachFile) {
        attachFile.addEventListener('change', () => {
            if (attachFile.files?.length > 0) llmaHandleAttach(attachFile.files[0]);
        });
    }

    // Drag & drop on input area
    const inputArea = document.querySelector('.llma-input-wrap');
    if (inputArea) {
        inputArea.addEventListener('dragover', e => { e.preventDefault(); inputArea.style.borderColor = 'var(--emphasis)'; });
        inputArea.addEventListener('dragleave', () => { inputArea.style.borderColor = ''; });
        inputArea.addEventListener('drop', e => {
            e.preventDefault();
            inputArea.style.borderColor = '';
            const files = e.dataTransfer?.files;
            if (files?.length > 0) {
                for (const f of files) {
                    if (f.type.startsWith('image/')) { llmaHandleAttach(f); return; }
                }
            }
        });
    }
}

// -- Send Message --
async function llmaSendMessage() {
    const input = document.getElementById('llma-input');
    if (!input) return;
    const message = input.value?.trim();
    if (!message && !LLMAState.attachedImage) return;
    if (LLMAState.isGenerating) return;

    input.value = '';
    input.style.height = 'auto';
    llmaUpdateCharCount('');

    // Create thread if needed
    if (!LLMAState.activeThreadId) {
        await llmaCreateThread(LLMAState.activeAssistantId);
    }

    // Hide welcome, show chat
    llmaShowChatPanel();

    // Add user message
    const userMsgId = llmaGenerateId();
    const userMsg = {
        id: userMsgId, role: 'user', content: message || '(image)',
        timestamp: new Date().toISOString(), imageBase64: LLMAState.attachedImage?.base64 || null,
    };
    LLMAState.messages.push(userMsg);
    llmaAppendMessageToDOM('user', userMsg.content, userMsg.imageBase64, userMsgId);
    // The previous exact count is stale now that a new message was added;
    // panel will show the heuristic until llmaRefreshExactTotalTokens() lands on data.done.
    LLMAState.exactTokenCount = null;
    LLMAState.exactTokenCountForLen = -1;
    if (typeof llmaUpdatePanelStats === 'function') llmaUpdatePanelStats();

    // Build payload
    const history = LLMAState.messages.map(m => ({ role: m.role, content: m.content }));
    const payload = llmaBuildPayload(message, 'chat', history);

    // Attach image if present
    if (LLMAState.attachedImage) {
        payload.media = [{ type: 'base64', data: LLMAState.attachedImage.base64, mediaType: LLMAState.attachedImage.mediaType }];
        llmaClearAttachment();
    }

    llmaStreamResponse(payload);
}

// -- Build Payload --
function llmaBuildPayload(message, instructionId, history, options = {}) {
    const payload = { message, instructionId, history };
    if (LLMAState.activeAssistantId) payload.assistantId = LLMAState.activeAssistantId;
    if (LLMAState.currentModel) payload.model = LLMAState.currentModel;

    // Thread params
    const tp = LLMAState.threadParams[LLMAState.activeThreadId] || {};
    const d  = LLMAState.settings?.defaults || {};
    const get = (k) => tp[k] ?? d[k];

    if (get('temperature') !== undefined) payload.temperature = get('temperature');
    if (get('maxTokens'))   payload.maxTokens = get('maxTokens');
    if (get('contextMessages') > 0) payload.maxContextMessages = get('contextMessages');
    if (options.media) payload.media = options.media;
    return payload;
}

// -- Streaming --
function llmaStreamResponse(payload, onComplete) {
    llmaSetStreaming(true);
    const assistantMsgId = llmaGenerateId();
    const assistantMsg = {
        id: assistantMsgId, role: 'assistant', content: '',
        timestamp: new Date().toISOString(),
        toolCalls: [],
    };
    LLMAState.messages.push(assistantMsg);
    llmaAppendMessageToDOM('assistant', '', null, assistantMsgId);

    const bubble = document.querySelector(`[data-msg-id="${assistantMsgId}"] .llma-msg-bubble`);
    const startTime = performance.now();
    let streamingText = '';
    let firstChunk = true;
    // Text content containers per iteration — tool call/result bubbles sit between them
    let textContainer = null;
    const ensureTextContainer = () => {
        if (!bubble) return null;
        if (!textContainer || !textContainer.isConnected) {
            textContainer = document.createElement('div');
            textContainer.className = 'llma-msg-text-segment';
            bubble.appendChild(textContainer);
        }
        return textContainer;
    };
    // Reset so next chunk after a tool result starts a fresh text segment
    const breakTextSegment = () => { textContainer = null; streamingText = ''; };

    LLMAState._streamingMsgId = assistantMsgId;
    LLMAState._streamingText  = '';

    try {
        LLMAState._activeSocket = makeWSRequest('LLMAssistantSendMessageWS', payload, data => {
            if (data.error) {
                llmaShowErrorInBubble(bubble, data.error);
                llmaSetStreaming(false);
                return;
            }
            if (data.chunk) {
                if (firstChunk) {
                    firstChunk = false;
                    const typing = bubble?.querySelector('.llma-typing');
                    if (typing) typing.remove();
                }
                streamingText += data.chunk;
                LLMAState._streamingText = streamingText;
                const tc = ensureTextContainer();
                if (tc) {
                    // Hide any raw <tool_call> tags during streaming for cleaner UI
                    const cleanText = streamingText.replace(/<tool_call>[\s\S]*?(<\/tool_call>|$)/g, '').trim();
                    tc.innerHTML = llmaRenderMarkdown(cleanText);
                }
                llmaScrollToBottom();
            }
            if (data.iteration !== undefined) {
                // New agentic iteration beginning — start a fresh text segment
                breakTextSegment();
            }
            if (data.tool_call) {
                // Append tool call bubble inline
                breakTextSegment();
                const msgObj = LLMAState.messages.find(m => m.id === assistantMsgId);
                if (msgObj) {
                    msgObj.toolCalls = msgObj.toolCalls || [];
                    msgObj.toolCalls.push({
                        id: data.tool_call.id,
                        name: data.tool_call.name,
                        arguments: data.tool_call.arguments,
                        result: null,
                    });
                }
                if (bubble) {
                    llmaRenderToolCall(bubble, data.tool_call);
                    llmaScrollToBottom();
                }
            }
            if (data.tool_result) {
                const msgObj = LLMAState.messages.find(m => m.id === assistantMsgId);
                if (msgObj && msgObj.toolCalls) {
                    const entry = msgObj.toolCalls.find(tc => tc.id === data.tool_result.id);
                    if (entry) entry.result = data.tool_result.result;
                }
                if (bubble) {
                    llmaRenderToolResult(bubble, data.tool_result);
                    llmaScrollToBottom();
                }
            }
            if (data.done) {
                const elapsed = ((performance.now() - startTime) / 1000).toFixed(1);
                const modelName = LLMAState.currentModel || '';

                // Persist full (possibly multi-iteration) content with tags stripped
                const fullRaw = data.full_text || streamingText;
                const cleanFull = llmaStripToolTags(fullRaw);

                // Update message in state
                const msg = LLMAState.messages.find(m => m.id === assistantMsgId);
                if (msg) {
                    msg.content = cleanFull;
                    msg.rawContent = fullRaw;
                    msg.meta = { genTime: elapsed, model: modelName };
                    if (data.truncated) msg.meta.truncated = true;
                    if (data.reason) msg.meta.reason = data.reason;
                }
                // Render the final text of this (last) iteration into the current segment
                // with asset-card swap applied
                const cleanFinal = llmaStripToolTags(streamingText);
                const tc = ensureTextContainer();
                if (tc) {
                    tc.innerHTML = llmaRenderAssistantContent(cleanFinal, assistantMsgId);
                    llmaPostRenderMermaid(tc);
                }

                // Rebuild the asset index for this thread and update the sidebar
                llmaRebuildAssetsForThread();

                // Update meta display
                const metaDiv = document.querySelector(`[data-msg-id="${assistantMsgId}"] .llma-msg-meta`);
                if (metaDiv) {
                    const parts = [];
                    if (modelName) parts.push(modelName);
                    parts.push(`${elapsed}s`);
                    metaDiv.textContent = parts.join(' \u00b7 ');
                }

                LLMAState._activeSocket = null;
                LLMAState._streamingMsgId = null;
                LLMAState._streamingText = '';
                llmaSetStreaming(false);
                llmaSaveActiveThread();
                llmaUpdateContextBar();
                if (typeof llmaUpdatePanelStats === 'function') llmaUpdatePanelStats();
                // Fire-and-forget exact-count refresh: lands async and patches the
                // sidebar/context bar once the server responds.
                if (typeof llmaRefreshExactTotalTokens === 'function') llmaRefreshExactTotalTokens();
                if (onComplete) onComplete(cleanFinal);
            }
        }, 0, err => {
            llmaShowErrorInBubble(bubble, err?.message || 'WebSocket error');
            LLMAState._activeSocket = null;
            LLMAState._streamingMsgId = null;
            llmaSetStreaming(false);
        });
    } catch (ex) {
        llmaShowErrorInBubble(bubble, ex.message);
        llmaSetStreaming(false);
    }
}

function llmaShowErrorInBubble(bubble, error) {
    if (!bubble) return;
    bubble.innerHTML = `<span class="llma-msg-error">Error: ${llmaEscapeHtml(error)}</span>`;
}

function llmaStopGeneration() {
    if (LLMAState._activeSocket) {
        try { LLMAState._activeSocket.close(); } catch (_) {}
        LLMAState._activeSocket = null;
    }
    if (LLMAState._streamingMsgId && LLMAState._streamingText) {
        const msg = LLMAState.messages.find(m => m.id === LLMAState._streamingMsgId);
        if (msg) msg.content = LLMAState._streamingText;
        const bubble = document.querySelector(`[data-msg-id="${LLMAState._streamingMsgId}"] .llma-msg-bubble`);
        if (bubble) {
            bubble.innerHTML = llmaRenderMarkdown(LLMAState._streamingText);
            llmaPostRenderMermaid(bubble);
        }
        llmaSaveActiveThread();
    }
    LLMAState._streamingMsgId = null;
    LLMAState._streamingText = '';
    llmaSetStreaming(false);
}

function llmaSetStreaming(active) {
    LLMAState.isGenerating = active;
    const sendBtn = document.getElementById('llma-send-btn');
    const stopBtn = document.getElementById('llma-stop-btn');
    const input   = document.getElementById('llma-input');
    if (sendBtn) sendBtn.style.display = active ? 'none' : '';
    if (stopBtn) stopBtn.style.display = active ? '' : 'none';
    if (input) input.disabled = active;
}

// -- Message Operations --
function llmaCopyMessage(msgId) {
    const msg = LLMAState.messages.find(m => m.id === msgId);
    if (msg) {
        navigator.clipboard.writeText(msg.content).then(() => llmaShowToast('Copied!', 'success'));
    }
}

function llmaDeleteMessage(msgId) {
    LLMAState.messages = LLMAState.messages.filter(m => m.id !== msgId);
    const el = document.querySelector(`[data-msg-id="${msgId}"]`);
    if (el) el.remove();
    llmaRebuildAssetsForThread();
    llmaSaveActiveThread();
    llmaUpdateContextBar();
    if (typeof llmaUpdatePanelStats === 'function') llmaUpdatePanelStats();
}

function llmaStartEdit(msgId) {
    const msg = LLMAState.messages.find(m => m.id === msgId);
    if (!msg) return;
    const bubble = document.querySelector(`[data-msg-id="${msgId}"] .llma-msg-bubble`);
    if (!bubble) return;
    const original = msg.content;
    bubble.innerHTML = '';

    const textarea = document.createElement('textarea');
    textarea.className = 'llma-input';
    textarea.value = original;
    textarea.rows = Math.max(2, original.split('\n').length);
    textarea.style.width = '100%';
    textarea.style.minHeight = '40px';

    const btnRow = document.createElement('div');
    btnRow.className = 'llma-msg-edit-actions';
    const saveBtn = document.createElement('button');
    saveBtn.className = 'basic-button';
    saveBtn.textContent = 'Save';
    saveBtn.addEventListener('click', () => {
        const newContent = textarea.value.trim();
        if (newContent) {
            msg.content = newContent;
            bubble.textContent = newContent;
            llmaSaveActiveThread();
        }
    });
    const cancelBtn = document.createElement('button');
    cancelBtn.className = 'basic-button';
    cancelBtn.textContent = 'Cancel';
    cancelBtn.addEventListener('click', () => { bubble.textContent = original; });

    btnRow.appendChild(saveBtn);
    btnRow.appendChild(cancelBtn);
    bubble.appendChild(textarea);
    bubble.appendChild(btnRow);
    textarea.focus();
}

function llmaRegenerateMessage(msgId) {
    if (LLMAState.isGenerating) return;
    const msgIndex = LLMAState.messages.findIndex(m => m.id === msgId);
    if (msgIndex < 0) return;

    const historyMessages = LLMAState.messages.slice(0, msgIndex);
    const removedIds = LLMAState.messages.slice(msgIndex).map(m => m.id);
    LLMAState.messages = historyMessages;
    removedIds.forEach(id => {
        const el = document.querySelector(`[data-msg-id="${id}"]`);
        if (el) el.remove();
    });

    const history = historyMessages.map(m => ({ role: m.role, content: m.content }));
    const lastUser = historyMessages.filter(m => m.role === 'user').pop();
    const payload = llmaBuildPayload(lastUser?.content || '', 'chat', history);
    llmaStreamResponse(payload);
}

// -- Image Handling --
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

function llmaShowAttachmentPreview(dataUrl) {
    const preview = document.getElementById('llma-attachment-preview');
    if (!preview) return;
    preview.innerHTML = '';
    preview.style.display = '';

    const img = document.createElement('img');
    img.src = dataUrl;
    preview.appendChild(img);

    // Vision action buttons
    const captionBtn = document.createElement('button');
    captionBtn.className = 'llma-msg-action-btn';
    captionBtn.textContent = 'Caption';
    captionBtn.addEventListener('click', () => llmaCaptionImage());
    preview.appendChild(captionBtn);

    const promptBtn = document.createElement('button');
    promptBtn.className = 'llma-msg-action-btn';
    promptBtn.textContent = 'Use as Prompt';
    promptBtn.addEventListener('click', () => llmaCaptionAndSendToPrompt());
    preview.appendChild(promptBtn);

    const initBtn = document.createElement('button');
    initBtn.className = 'llma-msg-action-btn';
    initBtn.textContent = 'Use as Init';
    initBtn.addEventListener('click', () => {
        if (LLMAState.attachedImage && typeof setCurrentImage === 'function') {
            setCurrentImage(LLMAState.attachedImage.dataUrl);
        }
    });
    preview.appendChild(initBtn);

    const removeBtn = document.createElement('button');
    removeBtn.className = 'llma-attachment-remove';
    removeBtn.textContent = '\u00d7';
    removeBtn.addEventListener('click', () => llmaClearAttachment());
    preview.appendChild(removeBtn);
}

function llmaClearAttachment() {
    LLMAState.attachedImage = null;
    const preview = document.getElementById('llma-attachment-preview');
    if (preview) { preview.innerHTML = ''; preview.style.display = 'none'; }
}

function llmaCaptionImage() {
    if (!LLMAState.attachedImage) return;
    const input = document.getElementById('llma-input');
    if (input) input.value = 'Describe this image in detail.';
    llmaSendMessage();
}

function llmaCaptionAndSendToPrompt() {
    if (!LLMAState.attachedImage) return;
    llmaShowChatPanel();
    if (!LLMAState.activeThreadId) {
        llmaCreateThread(LLMAState.activeAssistantId).then(() => llmaDoCaptionPrompt());
    } else {
        llmaDoCaptionPrompt();
    }
}

function llmaDoCaptionPrompt() {
    const captionMsg = 'Describe this image for use as an image generation prompt.';
    const userMsgId = llmaGenerateId();
    LLMAState.messages.push({ id: userMsgId, role: 'user', content: captionMsg, timestamp: new Date().toISOString() });
    llmaAppendMessageToDOM('user', captionMsg, LLMAState.attachedImage?.dataUrl, userMsgId);

    const history = LLMAState.messages.map(m => ({ role: m.role, content: m.content }));
    const payload = llmaBuildPayload(captionMsg, 'caption', history, {
        media: [{ type: 'base64', data: LLMAState.attachedImage.base64, mediaType: LLMAState.attachedImage.mediaType }],
    });
    llmaClearAttachment();
    llmaStreamResponse(payload, text => llmaSendToPromptBox(text));
}

// -- Prompt Box Integration --
function llmaSendToPromptBox(text) {
    const promptBox = document.getElementById('alt_prompt_textbox');
    if (!promptBox) return;
    promptBox.value = text;
    if (typeof triggerChangeFor === 'function') triggerChangeFor(promptBox);
}

// -- Scroll --
function llmaScrollToBottom() {
    const container = document.getElementById('llma-messages');
    if (container) container.scrollTop = container.scrollHeight;
}

// -- Char Count --
function llmaUpdateCharCount(text) {
    const counter = document.getElementById('llma-char-count');
    if (!counter) return;
    if (!text || text.length === 0) { counter.textContent = ''; return; }
    counter.textContent = `~${Math.ceil(text.length / 4)} tokens`;
}

// -- Tool Call / Result Rendering --
function llmaStripToolTags(text) {
    if (!text) return '';
    return text
        .replace(/<tool_call>[\s\S]*?<\/tool_call>/g, '')
        .replace(/<tool_result\b[^>]*>[\s\S]*?<\/tool_result>/g, '')
        .trim();
}

function llmaFormatToolName(name) {
    if (!name) return '';
    return name.replace(/_/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
}

function llmaRenderToolCall(bubble, call) {
    if (!bubble || !call) return;
    const wrap = document.createElement('div');
    wrap.className = 'llma-tool-call-bubble pending';
    wrap.setAttribute('data-tool-id', call.id || '');

    const header = document.createElement('div');
    header.className = 'llma-tool-call-header';

    const icon = document.createElement('span');
    icon.className = 'llma-tool-call-icon';
    icon.textContent = '\u2699'; // gear
    header.appendChild(icon);

    const title = document.createElement('span');
    title.className = 'llma-tool-call-title';
    title.textContent = `Calling ${llmaFormatToolName(call.name)}`;
    header.appendChild(title);

    const status = document.createElement('span');
    status.className = 'llma-tool-call-status';
    status.textContent = 'running\u2026';
    header.appendChild(status);

    const toggle = document.createElement('button');
    toggle.className = 'llma-tool-call-toggle';
    toggle.textContent = '\u25be'; // down-arrow
    toggle.title = 'Toggle details';
    header.appendChild(toggle);

    const body = document.createElement('div');
    body.className = 'llma-tool-call-body';

    const argsLabel = document.createElement('div');
    argsLabel.className = 'llma-tool-call-label';
    argsLabel.textContent = 'Arguments';
    body.appendChild(argsLabel);

    const argsPre = document.createElement('pre');
    argsPre.className = 'llma-tool-call-args';
    try {
        argsPre.textContent = JSON.stringify(call.arguments ?? {}, null, 2);
    } catch (_) {
        argsPre.textContent = String(call.arguments ?? '');
    }
    body.appendChild(argsPre);

    const resultSlot = document.createElement('div');
    resultSlot.className = 'llma-tool-result-slot';
    body.appendChild(resultSlot);

    toggle.addEventListener('click', () => {
        wrap.classList.toggle('collapsed');
        toggle.textContent = wrap.classList.contains('collapsed') ? '\u25b8' : '\u25be';
    });

    wrap.appendChild(header);
    wrap.appendChild(body);
    bubble.appendChild(wrap);
}

function llmaRenderToolResult(bubble, toolResult) {
    if (!bubble || !toolResult) return;
    const callBubble = bubble.querySelector(`.llma-tool-call-bubble[data-tool-id="${toolResult.id}"]`);
    const result = toolResult.result || {};
    const success = result.success !== false && !result.error;

    const target = callBubble || bubble;
    if (callBubble) {
        callBubble.classList.remove('pending');
        callBubble.classList.add(success ? 'success' : 'error');
        const status = callBubble.querySelector('.llma-tool-call-status');
        if (status) status.textContent = success ? 'done' : 'error';
    }

    const slot = callBubble
        ? callBubble.querySelector('.llma-tool-result-slot')
        : (() => {
            const s = document.createElement('div');
            s.className = 'llma-tool-result-slot standalone';
            target.appendChild(s);
            return s;
          })();

    slot.innerHTML = '';
    const resultWrap = document.createElement('div');
    resultWrap.className = `llma-tool-result-bubble ${success ? 'success' : 'error'}`;

    const resultLabel = document.createElement('div');
    resultLabel.className = 'llma-tool-call-label';
    resultLabel.textContent = success ? 'Result' : 'Error';
    resultWrap.appendChild(resultLabel);

    // Tool-specific previews
    const name = toolResult.name || '';
    if (success && name === 'generate_image' && result.imageUrl) {
        const img = document.createElement('img');
        img.src = result.imageUrl;
        img.className = 'llma-tool-result-image';
        img.addEventListener('click', () => {
            if (typeof setCurrentImage === 'function') setCurrentImage(result.imageUrl);
        });
        resultWrap.appendChild(img);
        if (result.prompt) {
            const caption = document.createElement('div');
            caption.className = 'llma-tool-result-caption';
            caption.textContent = result.prompt;
            resultWrap.appendChild(caption);
        }
    } else if (success && name === 'web_search' && Array.isArray(result.results)) {
        const list = document.createElement('ul');
        list.className = 'llma-tool-result-search';
        for (const item of result.results) {
            const li = document.createElement('li');
            const a = document.createElement('a');
            a.href = item.url || '#';
            a.target = '_blank';
            a.rel = 'noopener noreferrer';
            a.textContent = item.title || item.url || '(untitled)';
            li.appendChild(a);
            if (item.snippet) {
                const sn = document.createElement('div');
                sn.className = 'llma-tool-result-snippet';
                sn.textContent = item.snippet;
                li.appendChild(sn);
            }
            list.appendChild(li);
        }
        resultWrap.appendChild(list);
    } else if (success && name === 'file_read' && typeof result.content === 'string') {
        const info = document.createElement('div');
        info.className = 'llma-tool-result-fileinfo';
        info.textContent = `${result.path || ''} (${result.bytesRead ?? result.size ?? '?'} bytes${result.truncated ? ', truncated' : ''})`;
        resultWrap.appendChild(info);
        const pre = document.createElement('pre');
        pre.className = 'llma-tool-result-filecontent';
        pre.textContent = result.content;
        resultWrap.appendChild(pre);
    } else if (success && name === 'http_request') {
        const info = document.createElement('div');
        info.className = 'llma-tool-result-fileinfo';
        const statusClass = result.ok ? 'ok' : 'err';
        info.innerHTML = `<span class="llma-http-status ${statusClass}">${result.status} ${llmaEscapeHtml(result.statusText || '')}</span> <span class="llma-http-method">${llmaEscapeHtml(result.method || '')}</span> ${llmaEscapeHtml(result.url || '')}${result.truncated ? ' (truncated)' : ''}`;
        resultWrap.appendChild(info);
        if (typeof result.body === 'string' && result.body.length) {
            const pre = document.createElement('pre');
            pre.className = 'llma-tool-result-filecontent';
            pre.textContent = result.body.length > 4000 ? result.body.slice(0, 4000) + '\n…' : result.body;
            resultWrap.appendChild(pre);
        }
    } else if (name === 'shell_exec') {
        const info = document.createElement('div');
        info.className = 'llma-tool-result-fileinfo';
        const exitTxt = result.killed ? `killed (timeout)` : `exit ${result.exitCode}`;
        info.textContent = `$ ${result.command || ''}  [${exitTxt}${result.truncated ? ', truncated' : ''}]`;
        resultWrap.appendChild(info);
        if (typeof result.stdout === 'string' && result.stdout.length) {
            const pre = document.createElement('pre');
            pre.className = 'llma-tool-result-filecontent';
            pre.textContent = result.stdout;
            resultWrap.appendChild(pre);
        }
        if (typeof result.stderr === 'string' && result.stderr.length) {
            const label = document.createElement('div');
            label.className = 'llma-tool-call-label';
            label.textContent = 'stderr';
            resultWrap.appendChild(label);
            const pre = document.createElement('pre');
            pre.className = 'llma-tool-result-filecontent';
            pre.textContent = result.stderr;
            resultWrap.appendChild(pre);
        }
    } else {
        // Generic JSON fallback
        const pre = document.createElement('pre');
        pre.className = 'llma-tool-result-json';
        try {
            pre.textContent = JSON.stringify(result, null, 2);
        } catch (_) {
            pre.textContent = String(result);
        }
        resultWrap.appendChild(pre);
    }

    slot.appendChild(resultWrap);
}

// -- Replay tool calls for a stored assistant message (used on thread reload) --
function llmaReplayToolCalls(bubble, toolCalls) {
    if (!bubble || !Array.isArray(toolCalls)) return;
    for (const tc of toolCalls) {
        llmaRenderToolCall(bubble, { id: tc.id, name: tc.name, arguments: tc.arguments });
        if (tc.result !== null && tc.result !== undefined) {
            llmaRenderToolResult(bubble, { id: tc.id, name: tc.name, result: tc.result });
        }
    }
}

/* ================================================================
   LLM Assistant - chat/chat-pipeline.js
   The send → build-payload → stream → stop pipeline. Server is
   authoritative for history; we send threadId + the new message.
   ================================================================ */

(function () {
    'use strict';

    /** Read the composer, post the user message, and kick off streaming. */
    async function llmaSendMessage() {
        const input = document.getElementById('llma-input');
        if (!input) return;
        const message = input.value?.trim();
        if (!message && !LLMAState.attachedImage) return;
        if (LLMAState.isGenerating) return;

        // Pre-flight: a missing model is the most common "silent" failure. Catch it here with a clear
        // message and a nudge to the model dropdown, rather than letting the WS fail opaquely. The
        // input is left untouched so the user doesn't lose what they typed.
        if (!LLMAState.currentModel) {
            const haveModels = (LLMAState.availableModels || []).length > 0;
            llmaShowToast(haveModels
                ? 'Select a model from the dropdown at the top before sending.'
                : 'No LLM backend is running. Start one under Server > Backends, then refresh the model list.', 'error');
            if (typeof llmaFlashModelSelect === 'function') llmaFlashModelSelect();
            return;
        }

        // Option B: a leading "/toolId ..." forces that tool — strip it for the model payload (the backend
        // adds a "call this tool" directive); the bubble still shows what the user typed.
        const forced = (typeof llmaPickerExtractForceTool === 'function') ? llmaPickerExtractForceTool(message) : null;
        const payloadMessage = forced ? forced.message : message;
        if (typeof llmaPickerClose === 'function') llmaPickerClose();

        input.value = '';
        input.style.height = 'auto';
        llmaUpdateCharCount('');

        // Create thread if needed
        if (!LLMAState.activeThreadId) {
            await llmaCreateThread(LLMAState.activeAssistantId);
        }

        // Hide welcome, show chat
        llmaShowChatPanel();

        const userMsgId = llmaGenerateId();
        let mediaPayload = null;
        let mediaForRender = null;
        if (LLMAState.attachedImage) {
            const upload = await llmaUploadAttachedImage(userMsgId);
            if (!upload) return; // toast already shown
            mediaPayload = [upload];
            mediaForRender = upload.url;
            llmaClearAttachment();
        }

        // Add user message (uses the served URL; no base64 in the in-memory message either)
        const userMsg = {
            id: userMsgId, role: 'user', content: message || '(image)',
            timestamp: new Date().toISOString(),
            media: mediaPayload || undefined,
        };
        LLMAState.messages.push(userMsg);
        llmaAppendMessageToDOM('user', userMsg.content, mediaForRender, userMsgId);
        // The previous exact count is stale now that a new message was added;
        // panel will show the heuristic until llmaRefreshExactTotalTokens() lands on data.done.
        LLMAState.exactTokenCount = null;
        LLMAState.exactTokenCountForLen = -1;
        if (typeof llmaUpdatePanelStats === 'function') llmaUpdatePanelStats();

        // Build payload — server is authoritative for history; we only send threadId + new message.
        const payload = llmaBuildPayload(payloadMessage, 'chat');
        if (mediaPayload) {
            payload.media = mediaPayload;
        }
        if (forced) {
            payload.forceToolId = forced.toolId;
        }

        llmaStreamResponse(payload);
    }

    /**
     * Build the WS request payload. Server is authoritative for chat history — we send the active
     * threadId and the new message; we do NOT send history arrays. Thread params override settings.
     */
    function llmaBuildPayload(message, instructionId, options = {}) {
        const payload = { message, instructionId };
        if (LLMAState.activeThreadId) payload.threadId = LLMAState.activeThreadId;
        if (LLMAState.currentModel) payload.model = LLMAState.currentModel;

        // Thread params
        const tp = LLMAState.threadParams[LLMAState.activeThreadId] || {};
        const d  = LLMAState.settings?.parameters || {};
        const get = (k) => tp[k] ?? d[k];

        if (get('temperature') !== undefined) payload.temperature = get('temperature');
        if (get('maxTokens'))   payload.maxTokens = get('maxTokens');
        if (get('maxContextMessages') > 0) payload.maxContextMessages = get('maxContextMessages');
        // Seed: -1 means "random, let the backend pick"; only forward when the user pinned a value.
        const seed = get('seed');
        if (typeof seed === 'number' && seed >= 0) payload.seed = seed;
        if (options.media) payload.media = options.media;
        return payload;
    }

    /** Stream a response over the WS, rendering text/tool-call/tool-result events incrementally. */
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
        // Streaming markdown is throttled: re-parsing + re-sanitizing the whole message on every token is
        // O(n^2) and janky on long replies. Render at most once per STREAM_RENDER_MS during streaming; the
        // `done` handler always does the final authoritative render. Trailing-edge so the last partial isn't lost.
        const STREAM_RENDER_MS = 60;
        let lastStreamRender = 0;
        let pendingStreamRender = null;
        const clearPendingRender = () => {
            if (pendingStreamRender) { clearTimeout(pendingStreamRender); pendingStreamRender = null; }
        };
        const renderStreamingNow = () => {
            const tc = ensureTextContainer();
            if (!tc) return;
            // Hide any (possibly partial) <tool_call> markup during streaming for a clean UI.
            const cleanText = streamingText.replace(/<tool_call>[\s\S]*?(<\/tool_call>|$)/g, '').trim();
            tc.innerHTML = llmaRenderMarkdown(cleanText);
            lastStreamRender = performance.now();
        };
        const scheduleStreamingRender = () => {
            const since = performance.now() - lastStreamRender;
            if (since >= STREAM_RENDER_MS) {
                clearPendingRender();
                renderStreamingNow();
            } else if (!pendingStreamRender) {
                pendingStreamRender = setTimeout(() => { pendingStreamRender = null; renderStreamingNow(); }, STREAM_RENDER_MS - since);
            }
        };
        // Reset so next chunk after a tool result starts a fresh text segment
        const breakTextSegment = () => { clearPendingRender(); textContainer = null; streamingText = ''; };

        LLMAState._streamingMsgId = assistantMsgId;
        LLMAState._streamingText  = '';

        try {
            LLMAState._activeSocket = makeWSRequest('LLMAssistantSendMessageWS', payload, data => {
                if (data.error) {
                    clearPendingRender();
                    llmaShowErrorInBubble(bubble, data.error);
                    llmaSetStreaming(false);
                    return;
                }
                // Backend status events — eg LlamaSharp's "loading_model" before the disk read kicks
                // off. The typing-indicator dots already exist in the bubble; we swap them for a labeled
                // spinner so the user knows something is happening during a multi-second model load.
                if (data.status) {
                    if (data.status === 'loading_model') {
                        llmaShowLoadStatusInBubble(bubble, `Loading model ${data.model || ''}…`);
                    } else if (data.status === 'model_ready') {
                        llmaShowLoadStatusInBubble(bubble, null);
                    }
                    return;
                }
                if (data.chunk) {
                    if (firstChunk) {
                        firstChunk = false;
                        const typing = bubble?.querySelector('.llma-typing');
                        if (typing) typing.remove();
                        // Drop any lingering load-status indicator the moment real tokens start flowing.
                        llmaShowLoadStatusInBubble(bubble, null);
                    }
                    streamingText += data.chunk;
                    LLMAState._streamingText = streamingText;
                    scheduleStreamingRender();
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
                    // Cancel any trailing throttled render so it can't overwrite the final asset-swapped render.
                    clearPendingRender();
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
                        metaDiv.textContent = parts.join(' · ');
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
                // err here is the string from makeWSRequest's onerror (NOT an object) — pass it straight
                // through so the humanizer can recognize it instead of collapsing to "WebSocket error".
                llmaShowErrorInBubble(bubble, err || 'WebSocket error');
                LLMAState._activeSocket = null;
                LLMAState._streamingMsgId = null;
                llmaSetStreaming(false);
            });
        } catch (ex) {
            llmaShowErrorInBubble(bubble, ex);
            llmaSetStreaming(false);
        }
    }

    /** Abort an in-flight stream, persisting whatever text arrived so far. */
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

    /** Toggle the generating UI state (send/stop buttons, input disabled). File-private. */
    function llmaSetStreaming(active) {
        LLMAState.isGenerating = active;
        const sendBtn = document.getElementById('llma-send-btn');
        const stopBtn = document.getElementById('llma-stop-btn');
        const input   = document.getElementById('llma-input');
        if (sendBtn) sendBtn.style.display = active ? 'none' : '';
        if (stopBtn) stopBtn.style.display = active ? '' : 'none';
        if (input) input.disabled = active;
    }

    // --- Public API (called by sibling files + other chat/ modules) ---
    window.llmaSendMessage    = llmaSendMessage;
    window.llmaBuildPayload   = llmaBuildPayload;
    window.llmaStreamResponse = llmaStreamResponse;
    window.llmaStopGeneration = llmaStopGeneration;
})();

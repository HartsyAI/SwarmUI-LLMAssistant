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
        const typed = input.value;
        let message = typed?.trim();
        if (!message && !LLMAState.attachedImage && !llmaChatDocuments.hasPending()) return;
        if (LLMAState.isGenerating) return;
        if (llmaChatDocuments.isReading()) {
            llmaShowToast(translate('Still reading the attached document. Send again in a moment.'), 'info');
            return;
        }

        // Pre-flight: a missing model is the most common "silent" failure. Catch it here with a clear
        // message and a nudge to the model dropdown, rather than letting the WS fail opaquely. The
        // input is left untouched so the user doesn't lose what they typed.
        if (!LLMAState.currentModel) {
            const haveModels = (LLMAState.availableModels || []).length > 0;
            llmaShowToast(haveModels
                ? translate('Select a model before sending.')
                : translate('No LLM backend is running. Start one under Server > Backends, then refresh the model list.'), 'error');
            if (typeof llmaFlashModelSelect === 'function') llmaFlashModelSelect();
            return;
        }

        llmaCodeMode.hidePlanCard();
        // The files attached in this chat; for a chat not made yet they move to it when it is made below.
        const attachedIn = llmaChatDocuments.thread();
        // /compact summarizes the start of the chat now instead of sending a message.
        if (!LLMAState.attachedImage && !llmaChatDocuments.hasPending() && llmaContext.tryCommand(message)) {
            input.value = '';
            input.style.height = 'auto';
            llmaUpdateCharCount('');
            if (typeof llmaPickerClose === 'function') llmaPickerClose();
            return;
        }

        // Option B: a leading "/toolId ..." forces that tool; strip it for the model payload (the backend
        // adds a "call this tool" directive); the bubble still shows what the user typed.
        const forced = (typeof llmaPickerExtractForceTool === 'function') ? llmaPickerExtractForceTool(message) : null;
        // A leading "/name args" that is a command or skill (and not a tool, which wins a shared name) is expanded by the server into the text that is sent. The input
        // is left as typed when it cannot be, so nothing is lost.
        if (!forced) {
            const invocation = await llmaPromptAssets.invocation(message);
            if (invocation) {
                const expanded = await llmaPromptAssets.expand(invocation);
                if (expanded == null) {
                    return;
                }
                message = expanded;
            }
        }
        const payloadMessage = forced ? forced.message : message;
        if (typeof llmaPickerClose === 'function') llmaPickerClose();

        input.value = '';
        input.style.height = 'auto';
        llmaUpdateCharCount('');

        // Create thread if needed
        if (!LLMAState.activeThreadId) {
            await llmaCreateThread(LLMAState.activeAssistantId);
            // No chat could be made (it said why): nothing is sent, and the message stays in the composer with its attachments.
            if (!LLMAState.activeThreadId) {
                llmaPutBackText(typed);
                return;
            }
        }

        // Hide welcome, show chat. Sending always means "take me to the latest"; re-arm auto-follow
        // so the user's new message and the incoming reply track the bottom even if they'd scrolled up.
        llmaShowChatPanel();
        LLMAState._autoFollow = true;
        if (typeof llmaToggleScrollButton === 'function') llmaToggleScrollButton(false);

        const userMsgId = llmaGenerateId();
        let mediaPayload = null;
        let mediaForRender = null;
        const attachment = LLMAState.attachedImage;
        if (attachment) {
            const upload = await llmaUploadAttachedImage(userMsgId);
            if (!upload) return; // toast already shown
            mediaPayload = [upload];
            mediaForRender = mediaPayload;
            llmaClearAttachment();
        }

        const documents = llmaChatDocuments.take(attachedIn ?? LLMAState.activeThreadId);

        // Add user message (uses the served URL; no base64 in the in-memory message either). In the
        // branch model it hangs off the current active leaf and becomes the new leaf, mirroring the
        // server's AppendMessage so allNodes stays consistent without an extra round trip.
        const userMsg = {
            id: userMsgId, role: 'user', content: message || (!mediaPayload ? '(document)' : mediaPayload[0].mediaType?.startsWith('video/') ? '(video)' : '(image)'),
            timestamp: new Date().toISOString(),
            media: mediaPayload || undefined,
            documents: documents || undefined,
            parentId: LLMAState.activeLeafId ?? null,
        };
        LLMAState.messages.push(userMsg);
        (LLMAState.allNodes = LLMAState.allNodes || []).push(userMsg);
        LLMAState.activeLeafId = userMsgId;
        llmaAppendMessageToDOM('user', userMsg.content, mediaForRender, userMsgId, undefined, documents);
        // mediaForRender is the uploaded {url, mediaType} entries, so a video renders as a player.
        // The previous exact count is stale now that a new message was added;
        // the context bar shows the heuristic until llmaRefreshExactTotalTokens() lands on data.done.
        LLMAState.exactTokenCount = null;
        LLMAState.exactTokenCountForLen = -1;

        // Build payload. The server is authoritative for history; we only send threadId + new message.
        // Send the client-generated message id so the server persists it under the SAME id we rendered
        // with; otherwise the server assigns its own GUID and later edit/delete/fork can't find the message.
        const payload = llmaBuildPayload(payloadMessage, 'chat');
        payload.userMessageId = userMsgId;
        if (mediaPayload) {
            payload.media = mediaPayload;
        }
        if (documents) {
            // The server takes the ids and builds the references from its own copies.
            payload.documents = documents.map(d => d.id);
        }
        // The server takes no empty message: one of attachments alone is sent under the words the page shows for it.
        if (mediaPayload || documents) {
            payload.message = payload.message || userMsg.content;
        }
        if (forced) {
            payload.forceToolId = forced.toolId;
        }

        // Compare mode fans the same prompt out to two models side-by-side; otherwise normal single stream.
        // Both share all the prep above (thread create, attachment upload, user-message append).
        const refused = error => llmaTakeBack({ threadId: payload.threadId, messageId: userMsgId, parentId: userMsg.parentId, typed, documents, attachment }, error);
        if (typeof llmaIsCompareActive === 'function' && llmaIsCompareActive()) {
            llmaStreamCompare(payload, userMsgId, { refused });
        } else {
            llmaStreamResponse(payload, null, { refused });
        }
    }

    /** A message the server refused before storing it comes off the chat, and what was typed and attached goes back to the composer, to be changed and sent again. */
    function llmaTakeBack(unsent, error) {
        llmaShowToast(error, 'error');
        llmaChatDocuments.giveBack(unsent.threadId, unsent.documents);
        // Another chat is on screen: only the documents have a place to wait there.
        if ((LLMAState.activeThreadId || null) !== (unsent.threadId || null)) return;
        llmaDropUnstored(unsent.messageId, unsent.parentId);
        llmaRestoreAttachment(unsent.attachment);
        llmaPutBackText(unsent.typed);
    }

    /** Takes a message the server did not store, and what hangs under it, off the chat, and shows the branch ending at leafId. */
    function llmaDropUnstored(messageId, leafId) {
        const gone = llmaActions.subtreeIds(messageId, LLMAState.allNodes);
        LLMAState.allNodes = LLMAState.allNodes.filter(n => !gone.has(n.id));
        LLMAState.activeLeafId = leafId;
        LLMAState.messages = llmaComputeActivePath(LLMAState.allNodes, leafId);
        llmaRenderMessageHistory(LLMAState.messages);
        llmaRebuildAssetsForThread();
        llmaUpdateContextBar();
    }

    /** Puts text back into the composer, ahead of anything typed there since, and gives it the keyboard. */
    function llmaPutBackText(text) {
        const input = document.getElementById('llma-input');
        if (!input) return;
        input.value = input.value && text ? `${text}\n${input.value}` : text || input.value;
        input.style.height = 'auto';
        input.style.height = Math.min(input.scrollHeight, 140) + 'px';
        llmaUpdateCharCount(input.value);
        input.focus();
    }

    /**
     * Build the WS request payload. Server is authoritative for chat history; we send the active
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
        // Recurrent-block passes for a looped model (Dory): only this chat's own override is sent; the saved value is resolved
        // server-side (assistant parameters over the user's), then the backend default, so neither is shadowed from here.
        if (tp.loops > 0)       payload.loops = tp.loops;
        // Thinking toggle: true/false only when the user picked On/Off; Auto leaves the field out so the template default applies.
        const enableThinking = llmaThinkingRequestValue(LLMAState.thinkingMode);
        if (enableThinking !== undefined) payload.enableThinking = enableThinking;
        if (get('maxContextMessages') > 0) payload.maxContextMessages = get('maxContextMessages');
        // Seed: -1 means "random, let the backend pick"; only forward when the user pinned a value.
        const seed = get('seed');
        if (typeof seed === 'number' && seed >= 0) payload.seed = seed;
        if (options.media) payload.media = options.media;
        // How tool calls are approved in this chat; the server also saves it on the chat.
        payload.approvalMode = LLMAState.approvalMode || 'auto';
        return payload;
    }

    /** Stream a response over the WS, rendering text/tool-call/tool-result events incrementally. */
    function llmaStreamResponse(payload, onComplete, opts = {}) {
        // Branching: edit / regenerate stream through their own WS endpoints (which create the branch
        // server-side first); a normal send uses the default.
        const wsEndpoint = opts.endpoint || 'LLMAssistantSendMessageWS';
        llmaSetStreaming(true);
        llmaA11y.announce(translate('Assistant is replying'));
        const assistantMsgId = llmaGenerateId();
        // Persist the assistant reply under the same id we render with (see userMessageId note in
        // llmaSendMessage) so message ops on the reply resolve server-side.
        payload.assistantMessageId = assistantMsgId;
        const assistantMsg = {
            id: assistantMsgId, role: 'assistant', content: '',
            timestamp: new Date().toISOString(),
            toolCalls: [],
            parentId: LLMAState.activeLeafId ?? null,
        };
        LLMAState.messages.push(assistantMsg);
        (LLMAState.allNodes = LLMAState.allNodes || []).push(assistantMsg);
        llmaAppendMessageToDOM('assistant', '', null, assistantMsgId);

        const bubble = document.querySelector(`[data-msg-id="${assistantMsgId}"] .llma-msg-bubble`);
        const startTime = performance.now();
        let firstChunk = true;
        // Every chunk of the reply across tool iterations (the renderer's own text restarts at each one).
        let streamedRaw = '';
        // Text content containers per iteration; tool call/result bubbles sit between them. Owns the
        // accumulated text, the <tool_call> hide filter, and the throttled render (see utils.js).
        const renderer = llmaMakeStreamRenderer(() => bubble);

        LLMAState._streamingMsgId = assistantMsgId;
        LLMAState._streamingText  = '';
        LLMAState._streamingRenderer = renderer;

        const takenUp = llmaActions.turnWatcher(payload.threadId);
        try {
            LLMAState._activeSocket = makeWSRequest(wsEndpoint, payload, data => {
                takenUp(data);
                // Before the reply starts, a long chat may be summarized (compacting / compaction / compactionFailed).
                if (llmaContext.onFrame(data)) {
                    return;
                }
                // Backend status events, eg LlamaSharp's "loading_model" before the disk read kicks
                // off. The typing-indicator dots already exist in the bubble; we swap them for a labeled
                // spinner so the user knows something is happening during a multi-second model load.
                if (data.status) {
                    if (data.status === 'loading_model') {
                        llmaShowLoadStatusInBubble(bubble, `${translate('Loading model')} ${data.model || ''}…`);
                    } else if (data.status === 'model_ready') {
                        llmaShowLoadStatusInBubble(bubble, null);
                    } else if (data.status === 'queued') {
                        // The engine holds the request behind others on the same model: say so instead of a silent wait.
                        llmaShowLoadStatusInBubble(bubble, translate('Waiting in queue'));
                    }
                    return;
                }
                // Auto-generated chat title, sent once after a chat's first exchange (see
                // ChatEndpoints.MaybeGenerateTitleAsync); a side-channel event on this same socket,
                // unrelated to the reply currently streaming into this bubble.
                if (data.titleUpdated) {
                    const meta = LLMAState.threads.find(t => t.id === data.threadId);
                    if (meta) {
                        meta.title = data.titleUpdated;
                        llmaRenderThreadList(LLMAState.threads);
                    }
                    if (LLMAState.activeThreadId === data.threadId) {
                        const titleEl = document.getElementById('llma-thread-title');
                        if (titleEl) titleEl.textContent = data.titleUpdated;
                    }
                    return;
                }
                // The automatic memory extractor saved something after this reply (see MemoryExtractionService): a small notice with Undo, nothing more.
                if (data.memoryChanges) {
                    if (typeof LLMAMemoryNotice !== 'undefined') LLMAMemoryNotice.show(data.memoryChanges);
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
                    renderer.appendChunk(data.chunk);
                    streamedRaw += data.chunk;
                    LLMAState._streamingText = renderer.getRawText();
                    llmaScrollToBottom();
                }
                if (data.iteration !== undefined) {
                    // New agentic iteration beginning: start a fresh text segment
                    renderer.breakSegment();
                }
                if (data.tool_call) {
                    // Append tool call bubble inline
                    renderer.breakSegment();
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
                    llmaCodeMode.onToolCall(data.tool_call);
                }
                if (data.tool_output) {
                    // What a running command prints, as it prints it: the Terminal tab shows it live.
                    llmaCodeMode.onToolOutput(data.tool_output);
                }
                if (data.tool_approval) {
                    llmaA11y.announce(`${translate('A tool is waiting for your approval')}: ${data.tool_approval.name}`);
                    // A tool call is waiting for the user: show Allow / Deny on its step.
                    if (bubble) {
                        llmaRenderToolApproval(bubble, data.tool_approval);
                        llmaScrollToBottom();
                    }
                }
                if (data.tool_result) {
                    const msgObj = LLMAState.messages.find(m => m.id === assistantMsgId);
                    if (msgObj && msgObj.toolCalls) {
                        const entry = msgObj.toolCalls.find(tc => tc.id === data.tool_result.id);
                        if (entry) entry.result = data.tool_result.result;
                    }
                    // After the result is saved on the message: the Terminal tab reads it from there.
                    llmaCodeMode.onToolResult(data.tool_result);
                    if (bubble) {
                        llmaRenderToolResult(bubble, data.tool_result);
                        llmaScrollToBottom();
                    }
                }
                if (data.done) {
                    // Cancel any trailing throttled render so it can't overwrite the final asset-swapped render.
                    renderer.cancelPendingRender();
                    const elapsed = ((performance.now() - startTime) / 1000).toFixed(1);
                    const modelName = LLMAState.currentModel || '';

                    // Persist full (possibly multi-iteration) content with tags stripped
                    const fullRaw = data.full_text || renderer.getRawText();
                    const cleanFull = llmaStripToolTags(fullRaw);

                    // Update message in state
                    const msg = LLMAState.messages.find(m => m.id === assistantMsgId);
                    if (msg) {
                        msg.content = cleanFull;
                        msg.rawContent = fullRaw;
                        msg.meta = { genTime: elapsed, model: modelName };
                        if (data.truncated) msg.meta.truncated = true;
                        if (data.reason) msg.meta.reason = data.reason;
                        if (data.stopReason) msg.meta.stopReason = data.stopReason;
                        if (data.failure) msg.meta.error = data.failure;
                        if (data.usage) msg.meta.usage = data.usage;
                    }
                    // The completed reply is now the tip of the active branch.
                    LLMAState.activeLeafId = assistantMsgId;
                    llmaActions.noteFailure(assistantMsg.parentId, null);
                    // Render the final text of this (last) iteration into the current segment
                    // with asset-card swap applied
                    const cleanFinal = llmaStripToolTags(renderer.getRawText());
                    const tc = renderer.ensureTextContainer();
                    if (tc) {
                        llmaSetRenderedHtml(tc, llmaRenderAssistantContent(cleanFinal, assistantMsgId));
                        llmaPostRenderMermaid(tc);
                    }
                    // A reply that failed after some text: its message goes under the text.
                    if (data.failure) llmaAppendErrorToBubble(bubble, data.failure);

                    // Rebuild the asset index for this thread and update the sidebar
                    llmaRebuildAssetsForThread();

                    // Update meta display
                    const metaDiv = document.querySelector(`[data-msg-id="${assistantMsgId}"] .llma-msg-meta`);
                    if (metaDiv) {
                        const parts = [];
                        if (modelName) parts.push(modelName);
                        parts.push(`${elapsed}s`);
                        if (data.stopReason === 'length') parts.push(translate('cut off (max tokens)'));
                        metaDiv.textContent = parts.join(' · ');
                        metaDiv.classList.toggle('llma-msg-meta-truncated', data.stopReason === 'length');
                    }
                    if (data.stopReason === 'length') {
                        llmaShowToast('Reply cut off at the max token limit. Raise Max Tokens in Settings for longer replies.', 'info');
                    }

                    if (data.failure) {
                        llmaA11y.announceFailure(data.failure);
                    }
                    else {
                        llmaA11y.announce(data.stopReason === 'length' ? translate('Reply cut off at the max token limit') : translate('Reply complete'));
                    }
                    llmaFinishStreamState();
                    if (onComplete) onComplete(cleanFinal);
                }
            }, 0, (err, data) => {
                // An error frame from the server and a failed socket both come here (core's helper never hands an error frame to the callback above).
                // err is a string; pass it straight through so the humanizer can recognize it instead of collapsing to "WebSocket error".
                // It can be the first frame of a turn the server took up.
                takenUp(data);
                // A failure after some text keeps the text: text still waiting for its render is drawn first.
                renderer.flushRender();
                bubble?.querySelector('.llma-typing')?.remove();
                llmaShowLoadStatusInBubble(bubble, null);
                const message = err || translate('WebSocket error');
                // A reply is partial when it streamed text or ran tools; the load banner and typing dots are not text.
                const msg = LLMAState.messages.find(m => m.id === assistantMsgId);
                const partial = !!streamedRaw || !!(msg && (msg.toolCalls || []).length);
                if (partial) llmaAppendErrorToBubble(bubble, message);
                else llmaShowErrorInBubble(bubble, message);
                // The stored reply keeps what streamed and its error, as the server's copy does.
                if (msg && partial) {
                    msg.content = llmaStripToolTags(streamedRaw);
                    msg.rawContent = streamedRaw;
                    msg.meta = Object.assign({}, msg.meta, { error: message });
                    LLMAState.activeLeafId = assistantMsgId;
                }
                if (!partial) {
                    llmaActions.markUnsaved(msg);
                    llmaActions.noteFailure(assistantMsg.parentId, message);
                }
                // No frame came with it: the connection itself failed, and the server may still be writing.
                if (!data) {
                    llmaActions.markLost(msg);
                }
                llmaA11y.announceFailure(message);
                llmaFinishStreamState();
                // The error block and the Retry row came in under what was showing: follow them, unless the user has scrolled up to read.
                llmaScrollToBottom();
                if (data?.refused && opts.refused) {
                    opts.refused(message);
                }
            });
        } catch (ex) {
            llmaShowErrorInBubble(bubble, ex);
            llmaActions.markUnsaved(assistantMsg);
            llmaActions.noteFailure(assistantMsg.parentId, ex);
            llmaA11y.announceFailure(ex);
            llmaSetStreaming(false);
            llmaScrollToBottom();
        }
    }

    /** Abort an in-flight stream, persisting whatever text arrived so far. */
    function llmaStopGeneration() {
        // Tell the server to actually cancel generation; closing the socket below only detaches this
        // client from it; the model would otherwise keep running to completion in the background.
        // The local UI reset happens immediately regardless of whether this round-trips. The server saves what it had a moment
        // later, so the chat is read again then: the page and the server agree on where the next message goes.
        if (LLMAState.activeThreadId) {
            llmaRequest('LLMAssistantStopGeneration', { threadId: LLMAState.activeThreadId }).catch(() => {})
                .finally(() => setTimeout(llmaSaveActiveThread, 750));
        }
        if (LLMAState._activeSocket) {
            try { LLMAState._activeSocket.close(); } catch (_) {}
            LLMAState._activeSocket = null;
        }
        if (LLMAState.isGenerating) {
            llmaA11y.announce(translate('Reply stopped'));
        }
        // Compare mode persists each lane's partial text and finalizes its columns.
        if (typeof llmaCompareHandleStop === 'function' && llmaCompareHandleStop()) {
            llmaSetStreaming(false);
            llmaUsage.refreshAfterStop();
            return;
        }
        const stopped = LLMAState.messages.find(m => m.id === LLMAState._streamingMsgId);
        if (stopped) {
            // Draws what is waiting to be drawn, as text that has ended, so no render comes after the Stop and nothing stays "thinking".
            LLMAState._streamingRenderer?.flushRender();
            llmaActions.noteFailure(stopped.parentId, null);
            const bubble = document.querySelector(`[data-msg-id="${stopped.id}"] .llma-msg-bubble`);
            // A reply that ran a tool and said nothing still shows its dots, and the reply is not drawn again if it was stored.
            bubble?.querySelector('.llma-typing')?.remove();
            llmaSettleToolCalls(bubble, stopped);
            if (LLMAState._streamingText || (stopped.toolCalls || []).length) {
                // The server keeps a reply that had text or ran a tool, as the tip of the branch: so does the page.
                LLMAState.activeLeafId = stopped.id;
                if (LLMAState._streamingText) {
                    stopped.content = LLMAState._streamingText;
                    if (bubble && !(stopped.toolCalls || []).length) {
                        llmaSetRenderedHtml(bubble, llmaRenderMarkdown(LLMAState._streamingText));
                        llmaPostRenderMermaid(bubble);
                    }
                }
            }
            else {
                // Stopped before anything arrived: nothing is saved, so the row says so instead of showing dots for good.
                llmaActions.markUnsaved(stopped);
                if (bubble) {
                    bubble.innerHTML = `<div class="llma-msg-stopped">${llmaEscapeHtml(translate('Stopped before a reply arrived.'))}</div>`;
                }
            }
        }
        LLMAState._streamingMsgId = null;
        LLMAState._streamingText = '';
        llmaSetStreaming(false);
        llmaUsage.refreshAfterStop();
    }

    /** Toggle the generating UI state (send/stop buttons, input disabled). File-private. */
    /** Terminal state of a chat stream, shared by a finished reply and a failed one: detaches the socket and refreshes the
     *  thread list, context bar and token counts. The caller keeps its own bubble and completion callback. */
    function llmaFinishStreamState() {
        // The socket stays open after the reply (a title or a memory notice may still arrive on it); it is remembered so opening another chat can close it.
        LLMAState._lingerSocket = LLMAState._activeSocket || LLMAState._lingerSocket;
        LLMAState._activeSocket = null;
        LLMAState._streamingMsgId = null;
        LLMAState._streamingText = '';
        llmaSetStreaming(false);
        llmaSaveActiveThread();
        llmaUpdateContextBar();
        llmaCodeMode.onTurnFinished();
        // Fire-and-forget exact-count refresh: lands async and patches the context bar once the server responds.
        if (typeof llmaRefreshExactTotalTokens === 'function') llmaRefreshExactTotalTokens();
    }

    function llmaSetStreaming(active) {
        LLMAState.isGenerating = active;
        const sendBtn = document.getElementById('llma-send-btn');
        const stopBtn = document.getElementById('llma-stop-btn');
        const input   = document.getElementById('llma-input');
        if (sendBtn) sendBtn.style.display = active ? 'none' : '';
        if (stopBtn) stopBtn.style.display = active ? '' : 'none';
        if (input) input.disabled = active;
        // A reply that has ended (finished, stopped, or failed) has nothing left waiting for an answer, and a Stop closes the socket
        // before any result for the waiting call can arrive, so drop its Allow / Deny row here.
        if (!active) {
            document.querySelectorAll('.llma-tool-approval').forEach(row => row.remove());
        }
        llmaA11y.setBusy(active);
        // A new turn, however it was started, ends the first run.
        if (active) {
            llmaOnboarding.onSend();
        }
        // Greys out the actions that change the chat, and shows or removes the Retry row.
        llmaActions.syncTail();
    }

    // --- Public API (called by sibling files + other chat/ modules) ---
    window.llmaSendMessage    = llmaSendMessage;
    window.llmaDropUnstored   = llmaDropUnstored;
    window.llmaPutBackText    = llmaPutBackText;
    window.llmaBuildPayload   = llmaBuildPayload;
    window.llmaStreamResponse = llmaStreamResponse;
    window.llmaStopGeneration = llmaStopGeneration;
    window.llmaSetStreaming   = llmaSetStreaming;
})();

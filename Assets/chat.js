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
        // Prefer the persisted media URL (server-authoritative) over a legacy/in-memory base64.
        // <img src> handles both transparently.
        const imgSrc = (Array.isArray(msg.media) && msg.media[0]?.url) || msg.imageBase64 || null;
        llmaAppendMessageToDOM(msg.role, msg.content, imgSrc, msg.id, msg.meta);
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

// -- In-thread find --
// Cmd/Ctrl+Shift+F opens an inline find bar over the current thread's messages. Browser's
// native Cmd/Ctrl+F is left alone so power users keep it. Wraps matched text in <mark> elements
// scoped to .llma-msg-bubble; navigation between matches scrolls the active one into view.
let llmaFindState = { matches: [], index: -1, query: '' };

function llmaOpenFindBar() {
    const bar = document.getElementById('llma-find-bar');
    const input = document.getElementById('llma-find-input');
    if (!bar || !input) return;
    bar.style.display = '';
    input.focus();
    input.select();
}

function llmaCloseFindBar() {
    const bar = document.getElementById('llma-find-bar');
    if (!bar) return;
    bar.style.display = 'none';
    llmaClearFindHighlights();
    llmaFindState = { matches: [], index: -1, query: '' };
}

function llmaClearFindHighlights() {
    const container = document.getElementById('llma-messages');
    if (!container) return;
    // Replace each mark with its text. Walk a static copy of the NodeList since we mutate the DOM.
    const marks = Array.from(container.querySelectorAll('mark.llma-find-mark'));
    for (const m of marks) {
        const parent = m.parentNode;
        if (!parent) continue;
        parent.replaceChild(document.createTextNode(m.textContent || ''), m);
        parent.normalize();
    }
}

function llmaRunFind(query) {
    llmaClearFindHighlights();
    llmaFindState = { matches: [], index: -1, query: query || '' };
    const container = document.getElementById('llma-messages');
    const countEl = document.getElementById('llma-find-count');
    if (!container || !query) {
        if (countEl) countEl.textContent = '0 / 0';
        return;
    }
    const needle = query.toLowerCase();
    // Walk all text nodes inside message bubbles. Skip nodes inside <code>/<pre> only to keep
    // the highlight cheap on assistant messages with lots of code (search still hits inline text).
    const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT, {
        acceptNode: (node) => {
            if (!node.nodeValue || !node.nodeValue.toLowerCase().includes(needle)) return NodeFilter.FILTER_REJECT;
            if (node.parentElement?.closest('mark.llma-find-mark')) return NodeFilter.FILTER_REJECT;
            return NodeFilter.FILTER_ACCEPT;
        }
    });
    const toProcess = [];
    let n; while ((n = walker.nextNode())) toProcess.push(n);
    for (const text of toProcess) {
        const value = text.nodeValue;
        const lower = value.toLowerCase();
        let start = 0;
        const frag = document.createDocumentFragment();
        let idx;
        while ((idx = lower.indexOf(needle, start)) !== -1) {
            if (idx > start) frag.appendChild(document.createTextNode(value.slice(start, idx)));
            const mark = document.createElement('mark');
            mark.className = 'llma-find-mark';
            mark.textContent = value.slice(idx, idx + needle.length);
            frag.appendChild(mark);
            llmaFindState.matches.push(mark);
            start = idx + needle.length;
        }
        if (start < value.length) frag.appendChild(document.createTextNode(value.slice(start)));
        text.parentNode?.replaceChild(frag, text);
    }
    if (llmaFindState.matches.length > 0) {
        llmaFindState.index = 0;
        llmaScrollToCurrentMatch();
    }
    if (countEl) {
        countEl.textContent = llmaFindState.matches.length === 0
            ? '0 / 0'
            : `${llmaFindState.index + 1} / ${llmaFindState.matches.length}`;
    }
}

function llmaScrollToCurrentMatch() {
    const m = llmaFindState.matches[llmaFindState.index];
    if (!m) return;
    for (const other of llmaFindState.matches) other.classList.remove('active');
    m.classList.add('active');
    m.scrollIntoView({ block: 'center', behavior: 'smooth' });
}

function llmaFindNext(dir) {
    const len = llmaFindState.matches.length;
    if (len === 0) return;
    llmaFindState.index = (llmaFindState.index + dir + len) % len;
    llmaScrollToCurrentMatch();
    const countEl = document.getElementById('llma-find-count');
    if (countEl) countEl.textContent = `${llmaFindState.index + 1} / ${len}`;
}

function llmaSetupFindBar() {
    const input = document.getElementById('llma-find-input');
    const close = document.getElementById('llma-find-close');
    const prev  = document.getElementById('llma-find-prev');
    const next  = document.getElementById('llma-find-next');
    if (input) {
        input.addEventListener('input', llmaDebounce(() => llmaRunFind(input.value.trim()), 150));
        input.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                llmaFindNext(e.shiftKey ? -1 : 1);
            } else if (e.key === 'Escape') {
                e.preventDefault();
                llmaCloseFindBar();
            }
        });
    }
    close?.addEventListener('click', llmaCloseFindBar);
    prev?.addEventListener('click',  () => llmaFindNext(-1));
    next?.addEventListener('click',  () => llmaFindNext(+1));
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

    // File attach. The label opens a file picker via the hidden input; intercept the click
    // on the label so users on a confirmed text-only model get a toast instead of attaching
    // an image the backend will silently ignore.
    const attachLabel = document.getElementById('llma-attach-label');
    if (attachLabel) {
        attachLabel.addEventListener('click', (e) => {
            if (attachLabel.getAttribute('aria-disabled') === 'true') {
                e.preventDefault();
                llmaShowToast("This model doesn't support image input. Pick a vision model first.", 'info');
            }
        });
    }
    const attachFile = document.getElementById('llma-attach-file');
    if (attachFile) {
        attachFile.addEventListener('change', () => {
            if (attachFile.files?.length > 0) llmaHandleAttach(attachFile.files[0]);
        });
    }

    // Drag & drop on input area. Honors the same gate as the paperclip — dropping an image
    // onto a text-only model would just be silently discarded by the backend, so block early.
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
                    if (f.type.startsWith('image/')) {
                        if (attachLabel?.getAttribute('aria-disabled') === 'true') {
                            llmaShowToast("This model doesn't support image input.", 'info');
                            return;
                        }
                        llmaHandleAttach(f);
                        return;
                    }
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
    const payload = llmaBuildPayload(message, 'chat');
    if (mediaPayload) {
        payload.media = mediaPayload;
    }

    llmaStreamResponse(payload);
}

// -- Build Payload --
// Server is authoritative for chat history — we send the active threadId and the new message;
// the server loads the thread, appends, generates, and persists. We do NOT send history arrays.
function llmaBuildPayload(message, instructionId, options = {}) {
    const payload = { message, instructionId };
    if (LLMAState.activeThreadId) payload.threadId = LLMAState.activeThreadId;
    if (LLMAState.currentModel) payload.model = LLMAState.currentModel;

    // Thread params
    const tp = LLMAState.threadParams[LLMAState.activeThreadId] || {};
    const d  = LLMAState.settings?.defaults || {};
    const get = (k) => tp[k] ?? d[k];

    if (get('temperature') !== undefined) payload.temperature = get('temperature');
    if (get('maxTokens'))   payload.maxTokens = get('maxTokens');
    if (get('contextMessages') > 0) payload.maxContextMessages = get('contextMessages');
    // Seed: -1 means "random, let the backend pick"; only forward when the user pinned a value.
    const seed = get('seed');
    if (typeof seed === 'number' && seed >= 0) payload.seed = seed;
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

// Replaces (or removes) the bubble's typing indicator with a labeled spinner. Used for slow
// backend startup events — most importantly LlamaSharp model loads which can take 10+ seconds
// on a cold start. Passing label=null clears the indicator (called once the first real chunk arrives).
function llmaShowLoadStatusInBubble(bubble, label) {
    if (!bubble) return;
    let banner = bubble.querySelector('.llma-load-status');
    if (label === null || label === undefined) {
        banner?.remove();
        return;
    }
    if (!banner) {
        // Hide the typing dots while the status banner is up; we re-insert dots once load finishes.
        const typing = bubble.querySelector('.llma-typing');
        if (typing) typing.style.display = 'none';
        banner = document.createElement('div');
        banner.className = 'llma-load-status';
        banner.innerHTML = '<span class="llma-spinner" aria-hidden="true"></span><span class="llma-load-status-text"></span>';
        bubble.insertBefore(banner, bubble.firstChild);
    }
    const text = banner.querySelector('.llma-load-status-text');
    if (text) text.textContent = label;
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

async function llmaDeleteMessage(msgId) {
    if (!LLMAState.activeThreadId || !msgId) return;
    // Optimistic local removal so the UI feels snappy; if the server rejects we re-fetch to recover.
    const prevMessages = LLMAState.messages;
    LLMAState.messages = prevMessages.filter(m => m.id !== msgId);
    const el = document.querySelector(`[data-msg-id="${msgId}"]`);
    if (el) el.remove();
    llmaRebuildAssetsForThread();
    llmaUpdateContextBar();
    if (typeof llmaUpdatePanelStats === 'function') llmaUpdatePanelStats();
    try {
        const res = await llmaRequest('LLMAssistantDeleteMessage', { threadId: LLMAState.activeThreadId, messageId: msgId });
        if (!res?.success) {
            llmaShowToast(res?.error || 'Failed to delete message', 'error');
            await llmaReloadActiveThread();
            return;
        }
    } catch {
        llmaShowToast('Failed to delete message', 'error');
        await llmaReloadActiveThread();
        return;
    }
    // Sidebar metadata refresh (title may have changed if first message was deleted, etc.)
    llmaSaveActiveThread();
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
    saveBtn.addEventListener('click', async () => {
        const newContent = textarea.value.trim();
        if (!newContent || !LLMAState.activeThreadId) return;
        // Optimistic local update; revert by re-rendering on failure.
        msg.content = newContent;
        bubble.textContent = newContent;
        try {
            const res = await llmaRequest('LLMAssistantEditMessage', {
                threadId: LLMAState.activeThreadId,
                messageId: msgId,
                content: newContent,
            });
            if (!res?.success) {
                llmaShowToast(res?.error || 'Failed to save edit', 'error');
                await llmaReloadActiveThread();
                return;
            }
            llmaSaveActiveThread();
        } catch {
            llmaShowToast('Failed to save edit', 'error');
            await llmaReloadActiveThread();
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

    // Vision action buttons.
    // "Caption \u25be" opens an inline style picker that runs caption_image directly via the tool
    // execute endpoint \u2014 no chat round-trip, no thread persistence. Result actions: copy /
    // send-to-chat / send-to-prompt.
    const captionWrap = document.createElement('div');
    captionWrap.className = 'llma-attach-caption-wrap';

    const captionBtn = document.createElement('button');
    captionBtn.className = 'llma-msg-action-btn';
    captionBtn.textContent = 'Caption \u25be';
    captionBtn.addEventListener('click', () => llmaToggleQuickCaption(captionWrap));
    captionWrap.appendChild(captionBtn);

    preview.appendChild(captionWrap);

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

// Toggles the quick-caption popover under the "Caption" button. Renders style picker + Run +
// (after run) the caption text + copy/send-to-chat/send-to-prompt actions.
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
        result.textContent = 'Captioning\u2026';
        try {
            // Pass the data URI directly \u2014 ImageInputResolver accepts it, no upload needed.
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

// Renders the caption text + a row of action buttons below it.
function llmaRenderQuickCaptionResult(container, caption) {
    container.innerHTML = '';
    const text = document.createElement('div');
    text.className = 'llma-quick-caption-text';
    text.textContent = caption;
    container.appendChild(text);
    const actions = document.createElement('div');
    actions.className = 'llma-quick-caption-actions';
    const copyBtn = document.createElement('button');
    copyBtn.className = 'basic-button';
    copyBtn.textContent = 'Copy';
    copyBtn.addEventListener('click', () => {
        navigator.clipboard.writeText(caption).then(() => llmaShowToast('Copied', 'success'));
    });
    const chatBtn = document.createElement('button');
    chatBtn.className = 'basic-button';
    chatBtn.textContent = 'Send to chat';
    chatBtn.addEventListener('click', () => {
        const input = document.getElementById('llma-input');
        if (input) input.value = caption;
    });
    const promptBtn = document.createElement('button');
    promptBtn.className = 'basic-button';
    promptBtn.textContent = 'Send to prompt';
    promptBtn.addEventListener('click', () => llmaSendToPromptBox(caption));
    actions.append(copyBtn, chatBtn, promptBtn);
    container.appendChild(actions);
}

function llmaClearAttachment() {
    LLMAState.attachedImage = null;
    const preview = document.getElementById('llma-attachment-preview');
    if (preview) { preview.innerHTML = ''; preview.style.display = 'none'; }
}

// Shared helper: uploads the currently-attached image to the per-user uploads dir and returns
// `{ url, mediaType }` (the shape both the WS media payload and the DOM render expect), or null
// on failure (toast surfaced). Caller must clear the attachment after a successful return.
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
    // Persist the tool name + args on the bubble so D3's retry button can re-issue the same call
    // without needing to round-trip through the message history (which the user might have edited).
    wrap.setAttribute('data-tool-name', call.name || '');
    try { wrap.setAttribute('data-tool-args', JSON.stringify(call.arguments ?? {})); }
    catch { wrap.setAttribute('data-tool-args', '{}'); }

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
    // Spinner stays in the DOM next to the status text while the call is pending.
    // The tool-result handler swaps the bubble class from .pending to .success/.error,
    // which hides the spinner via a CSS rule keyed on the parent .pending state.
    status.innerHTML = '<span class="llma-spinner" aria-hidden="true"></span><span>running\u2026</span>';
    header.appendChild(status);

    const toggle = document.createElement('button');
    toggle.className = 'llma-tool-call-toggle';
    toggle.textContent = '\u25be'; // down-arrow
    toggle.title = 'Toggle details';
    toggle.setAttribute('aria-expanded', 'true');
    toggle.setAttribute('aria-label', 'Toggle tool call details');
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
        const collapsed = wrap.classList.toggle('collapsed');
        toggle.textContent = collapsed ? '\u25b8' : '\u25be';
        toggle.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
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
    } else if (success && name === 'file_write') {
        const info = document.createElement('div');
        info.className = 'llma-tool-result-fileinfo';
        info.textContent = `${result.path || ''} (${result.bytesWritten ?? '?'} bytes)`;
        resultWrap.appendChild(info);

        if (result.url) {
            const linkWrap = document.createElement('div');
            linkWrap.className = 'llma-tool-result-filelink';
            const a = document.createElement('a');
            a.href = result.url;
            a.target = '_blank';
            a.rel = 'noopener noreferrer';
            a.textContent = result.url;
            a.addEventListener('click', (e) => {
                // Left-click opens the in-tab artifact viewer; allow normal browser behavior
                // for ctrl/cmd-click, middle-click, etc.
                if (e.button != 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) {
                    return;
                }
                if (typeof llmaOpenAsset === 'function') {
                    e.preventDefault();
                    llmaRebuildAssetsForThread?.();
                    const msgEl = bubble.closest?.('[data-msg-id]');
                    const msgId = msgEl ? msgEl.getAttribute('data-msg-id') : null;
                    const assetId = msgId ? `${msgId}-tool-${toolResult.id}` : null;
                    if (assetId) {
                        llmaOpenAsset(assetId);
                    }
                }
            });
            linkWrap.appendChild(a);
            resultWrap.appendChild(linkWrap);
        }
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

    // Retry affordance — only when the call failed AND the wrapper has the original args.
    // Re-runs via LLMAssistantExecuteTool (the standalone tool runner) and shows the new
    // result in place. Does NOT amend the conversation history server-side — purely a manual
    // "did the tool start working?" check the user can fire without re-prompting the LLM.
    if (!success && callBubble) {
        const retryBtn = document.createElement('button');
        retryBtn.className = 'basic-button llma-tool-retry-btn';
        retryBtn.type = 'button';
        retryBtn.textContent = 'Retry';
        retryBtn.title = 'Re-run this tool with the same arguments. Does not affect the chat history.';
        retryBtn.addEventListener('click', () => llmaRetryToolCall(callBubble, retryBtn));
        resultWrap.appendChild(retryBtn);
    }

    slot.appendChild(resultWrap);
}

// Re-runs a tool call standalone. Doesn't mutate the saved thread — the original tool result
// stays in the LLM's view of history. This is purely a user-facing "did the tool start working?"
// check after, eg, fixing a config / restoring network access.
async function llmaRetryToolCall(callBubble, button) {
    const toolName = callBubble.getAttribute('data-tool-name');
    let args = {};
    try { args = JSON.parse(callBubble.getAttribute('data-tool-args') || '{}'); }
    catch { /* leave empty — server will error and we'll display that */ }
    if (button) { button.disabled = true; button.textContent = 'Retrying…'; }
    const result = await llmaUserAction(
        () => llmaRequest('LLMAssistantExecuteTool', { toolId: toolName, arguments: JSON.stringify(args) }),
        'Retry failed'
    );
    if (button) { button.disabled = false; button.textContent = 'Retry'; }
    if (!result) return;
    // Surface success/failure as a toast — the original error bubble stays put (it's part of
    // the persisted transcript) but the user gets confirmation of the new attempt's outcome.
    const innerResult = result.result || result;
    const ok = innerResult?.success !== false && !innerResult?.error;
    if (ok) {
        llmaShowToast(`${toolName} succeeded on retry.`, 'info');
    } else {
        llmaShowToast(`${toolName} still failing: ${innerResult?.error || 'unknown error'}`, 'error');
    }
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

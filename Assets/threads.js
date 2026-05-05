/* ================================================================
   LLM Assistant - threads.js
   Thread sidebar: list, search, create, switch, delete, export.
   ================================================================ */

'use strict';

// -- Load / Fetch --
async function llmaLoadThreads() {
    try {
        const result = await llmaRequest('LLMAssistantGetThreads', {});
        LLMAState.threads = Array.isArray(result?.threads) ? result.threads : [];
    } catch {
        LLMAState.threads = [];
    }
    llmaRenderThreadList(LLMAState.threads);
}

// -- Render Sidebar --
function llmaRenderThreadList(threads) {
    const list = document.getElementById('llma-thread-list');
    if (!list) return;

    if (threads.length === 0) {
        list.innerHTML = '<div class="llma-empty-state">No chats yet.<br>Choose an assistant to begin.</div>';
        return;
    }

    const groups = llmaGroupByDate([...threads].sort((a, b) => new Date(b.updated || b.created) - new Date(a.updated || a.created)));
    let html = '';

    for (const [label, group] of Object.entries(groups)) {
        if (group.length === 0) continue;
        html += `<div class="llma-thread-group-label">${llmaEscapeHtml(label)}</div>`;
        for (const thread of group) {
            const isActive  = thread.id === LLMAState.activeThreadId;
            const assistant = LLMAState.assistants.find(a => a.id === thread.assistantId);
            const dotColor  = assistant?.color || 'var(--emphasis)';
            html += `
                <div class="llma-thread-item${isActive ? ' active' : ''}"
                     data-id="${llmaEscapeHtml(thread.id)}"
                     title="${llmaEscapeHtml(thread.title || 'Untitled')} \u2014 ${llmaRelativeTime(thread.updated || thread.created)}"
                     role="button" tabindex="0">
                    <div class="llma-thread-dot" style="background:${dotColor}"></div>
                    <span class="llma-thread-name">${llmaEscapeHtml(thread.title || 'Untitled')}</span>
                    <button class="llma-thread-del" data-id="${llmaEscapeHtml(thread.id)}"
                            title="Delete thread" aria-label="Delete thread">&times;</button>
                </div>`;
        }
    }

    list.innerHTML = html;
    llmaBindThreadListEvents(list);
}

function llmaBindThreadListEvents(list) {
    list.querySelectorAll('.llma-thread-item').forEach(item => {
        item.addEventListener('click', (e) => {
            if (e.target.classList.contains('llma-thread-del')) return;
            llmaSwitchThread(item.dataset.id);
        });
        item.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                llmaSwitchThread(item.dataset.id);
            }
        });
    });
    list.querySelectorAll('.llma-thread-del').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            llmaDeleteThread(btn.dataset.id);
        });
    });
}

// -- Create Thread --
// Server creates the thread (assigns id/timestamps/title); we trust the returned blob.
async function llmaCreateThread(assistantId) {
    const assistant = LLMAState.assistants.find(a => a.id === assistantId);
    const initialTitle = assistant ? `Chat with ${assistant.name}` : 'New Chat';

    let thread;
    try {
        const result = await llmaRequest('LLMAssistantCreateThread', { assistantId: assistantId || '', title: initialTitle });
        if (!result?.success || !result.thread) {
            llmaShowToast(result?.error || 'Failed to create thread', 'error');
            return;
        }
        thread = typeof result.thread === 'string' ? JSON.parse(result.thread) : result.thread;
    } catch {
        llmaShowToast('Failed to create thread', 'error');
        return;
    }

    LLMAState.threads.unshift({
        id:           thread.id,
        title:        thread.title,
        assistantId:  thread.assistantId,
        created:      thread.createdAt || thread.created,
        updated:      thread.updatedAt || thread.updated,
        messageCount: 0,
    });

    LLMAState.activeThreadId    = thread.id;
    LLMAState.messages          = [];
    LLMAState.assets            = [];
    LLMAState.activeAssistantId = assistantId || LLMAState.activeAssistantId;
    llmaSetSessionState({ activeThreadId: thread.id });

    const titleEl = document.getElementById('llma-thread-title');
    if (titleEl) titleEl.textContent = thread.title;

    llmaShowChatPanel();
    llmaRenderThreadList(LLMAState.threads);
    llmaUpdateContextBar();

    if (LLMAState.activeAssistantId) {
        llmaRenderAssistantPanel(LLMAState.activeAssistantId);
    }
    // Empty new thread → show the personalized assistant greeting above the input.
    if (typeof llmaRenderEmptyChatGreeting === 'function') {
        llmaRenderEmptyChatGreeting();
    }

    // Focus input
    setTimeout(() => document.getElementById('llma-input')?.focus(), 100);
}

// -- Switch Thread --
async function llmaSwitchThread(threadId) {
    if (LLMAState.isGenerating) {
        llmaShowToast('Stop generation before switching threads', 'info');
        return;
    }

    try {
        const result = await llmaRequest('LLMAssistantGetThread', { threadId });
        const thread = result?.thread
            ? (typeof result.thread === 'string' ? JSON.parse(result.thread) : result.thread)
            : null;
        if (!thread) { llmaShowToast('Thread not found', 'error'); return; }

        LLMAState.activeThreadId    = thread.id;
        LLMAState.messages          = Array.isArray(thread.messages) ? thread.messages : [];
        LLMAState.activeAssistantId = thread.assistantId || LLMAState.activeAssistantId;
        // Invalidate the exact token count — it's for the previous thread.
        LLMAState.exactTokenCount = null;
        LLMAState.exactTokenCountForLen = -1;

        // Restore per-thread parameter overrides (model, temperature, etc.)
        if (thread.params && typeof thread.params === 'object') {
            LLMAState.threadParams[thread.id] = thread.params;
        }

        // Restore persisted assets if present; otherwise rebuild from messages.
        if (Array.isArray(thread.assets) && thread.assets.length > 0) {
            LLMAState.assets = thread.assets;
            if (typeof llmaRenderAssetSidebar === 'function') {
                llmaRenderAssetSidebar();
            }
        } else if (typeof llmaRebuildAssetsForThread === 'function') {
            llmaRebuildAssetsForThread();
        }

        // Persist active thread ID server-side so a headless client can resume.
        llmaSetSessionState({ activeThreadId: thread.id });

        const titleEl = document.getElementById('llma-thread-title');
        if (titleEl) titleEl.textContent = thread.title || 'Untitled';

        llmaShowChatPanel();
        llmaRenderMessageHistory(LLMAState.messages);
        llmaUpdateContextBar();
        llmaRenderThreadList(LLMAState.threads);

        if (LLMAState.activeAssistantId) {
            llmaRenderAssistantPanel(LLMAState.activeAssistantId);
        }

        // Request exact count for the freshly-loaded thread (fire-and-forget).
        if (typeof llmaRefreshExactTotalTokens === 'function') {
            llmaRefreshExactTotalTokens();
        }

        // Close sidebar on mobile
        if (window.innerWidth <= 680) {
            document.getElementById('llma-sidebar')?.classList.remove('sidebar-open');
        }
    } catch {
        llmaShowToast('Failed to load thread', 'error');
    }
}

// -- Reload active thread --
// Re-fetches the active thread from the server and re-renders the message history.
// Used to recover after a server-side mutation (eg edit/delete message) fails or returns
// an authoritative thread state we should adopt.
async function llmaReloadActiveThread() {
    if (!LLMAState.activeThreadId) return;
    let thread = null;
    try {
        const result = await llmaRequest('LLMAssistantGetThread', { threadId: LLMAState.activeThreadId });
        if (result?.thread) {
            thread = typeof result.thread === 'string' ? JSON.parse(result.thread) : result.thread;
        }
    } catch { /* fall through */ }
    if (!thread) return;
    LLMAState.messages = Array.isArray(thread.messages) ? thread.messages : [];
    llmaRenderMessageHistory(LLMAState.messages);
    llmaRebuildAssetsForThread();
    llmaUpdateContextBar();
    if (typeof llmaUpdatePanelStats === 'function') llmaUpdatePanelStats();
    // Update sidebar metadata too.
    const meta = LLMAState.threads.find(t => t.id === LLMAState.activeThreadId);
    if (meta) {
        meta.title        = thread.title || meta.title;
        meta.updated      = thread.updatedAt || thread.updated || meta.updated;
        meta.messageCount = LLMAState.messages.length;
        const titleEl = document.getElementById('llma-thread-title');
        if (titleEl) titleEl.textContent = meta.title;
        llmaRenderThreadList(LLMAState.threads);
    }
}

// -- Session state helpers --
// Per-user active state (activeThreadId, currentModel, etc.) is persisted server-side
// via LLMAssistantGetSessionState / LLMAssistantSetSessionState so headless clients
// can resume exactly where the UI left off.
async function llmaGetSessionState() {
    try {
        const result = await llmaRequest('LLMAssistantGetSessionState', {});
        return result?.state || {};
    } catch {
        return {};
    }
}

async function llmaSetSessionState(patch) {
    if (!patch || typeof patch !== 'object') return;
    try {
        await llmaRequest('LLMAssistantSetSessionState', { state: patch });
    } catch {
        // Non-fatal — UI can proceed without server-side echo.
    }
}

// -- Delete Thread --
async function llmaDeleteThread(threadId) {
    if (!confirm('Delete this chat? This cannot be undone.')) return;

    try {
        await llmaRequest('LLMAssistantDeleteThread', { threadId });
    } catch {
        llmaShowToast('Failed to delete thread', 'error');
        return;
    }

    LLMAState.threads = LLMAState.threads.filter(t => t.id !== threadId);

    if (LLMAState.activeThreadId === threadId) {
        LLMAState.activeThreadId = null;
        LLMAState.messages       = [];
        LLMAState.assets         = [];
        llmaSetSessionState({ activeThreadId: null });
        llmaShowWelcome();
    }

    llmaRenderThreadList(LLMAState.threads);
    llmaShowToast('Chat deleted', 'info');
}

// -- Save Thread (sidebar metadata refresh) --
// Server-authoritative chat: every write (append message on send, edit/delete via dedicated
// endpoints, rename via dedicated endpoint) is server-side. This function never PUTs the full
// thread anymore (LLMAssistantSaveThread was removed). It just re-fetches the saved thread
// from the server so the sidebar's title / updated / messageCount stay in sync after a mutation.
// Use llmaReloadActiveThread() if you also need to re-render the message history itself.
async function llmaSaveActiveThread() {
    if (!LLMAState.activeThreadId) return;
    const meta = LLMAState.threads.find(t => t.id === LLMAState.activeThreadId);
    if (!meta) return;
    let saved = null;
    try {
        const result = await llmaRequest('LLMAssistantGetThread', { threadId: LLMAState.activeThreadId });
        if (result?.success && result.thread) {
            saved = typeof result.thread === 'string' ? JSON.parse(result.thread) : result.thread;
        }
    } catch {
        return;
    }
    if (!saved) return;
    // Trust the server's view of the thread.
    meta.title        = saved.title || meta.title;
    meta.updated      = saved.updatedAt || saved.updated || meta.updated;
    meta.messageCount = Array.isArray(saved.messages) ? saved.messages.length : meta.messageCount;

    const titleEl = document.getElementById('llma-thread-title');
    if (titleEl) titleEl.textContent = meta.title;
    llmaRenderThreadList(LLMAState.threads);
}

// -- Export Thread --
function llmaExportThread(format) {
    if (!LLMAState.messages.length) {
        llmaShowToast('No messages to export', 'info');
        return;
    }

    const meta      = LLMAState.threads.find(t => t.id === LLMAState.activeThreadId);
    const title     = meta?.title || 'chat';
    const assistant = LLMAState.assistants.find(a => a.id === LLMAState.activeAssistantId);
    const safeName  = title.replace(/[^a-z0-9]+/gi, '_').toLowerCase();
    const timestamp = new Date().toISOString().slice(0, 10);

    if (format === 'json') {
        const data = {
            title,
            assistant: assistant ? { name: assistant.name, category: assistant.category } : null,
            exported:  new Date().toISOString(),
            messages:  LLMAState.messages.map(m => ({ role: m.role, content: m.content, timestamp: m.timestamp })),
        };
        llmaDownloadFile(`${safeName}_${timestamp}.json`, JSON.stringify(data, null, 2), 'application/json');
    } else if (format === 'md') {
        const lines = [`# ${title}`, '', `*Exported: ${new Date().toLocaleDateString()}*`, ''];
        if (assistant) lines.push(`*Assistant: ${assistant.name}*`, '');
        for (const msg of LLMAState.messages) {
            const role = msg.role === 'user' ? '**You**' : `**${assistant?.name || 'Assistant'}**`;
            lines.push(`${role}`, '', msg.content, '', '---', '');
        }
        llmaDownloadFile(`${safeName}_${timestamp}.md`, lines.join('\n'), 'text/markdown');
    } else if (format === 'txt') {
        const lines = [title, '='.repeat(title.length), ''];
        for (const msg of LLMAState.messages) {
            const role = msg.role === 'user' ? 'You' : (assistant?.name || 'Assistant');
            lines.push(`[${role}]`, msg.content, '');
        }
        llmaDownloadFile(`${safeName}_${timestamp}.txt`, lines.join('\n'), 'text/plain');
    }

    llmaShowToast(`Exported as ${format.toUpperCase()}`, 'success');
}

// -- Search --
function llmaFilterThreads(query) {
    if (!query.trim()) {
        llmaRenderThreadList(LLMAState.threads);
        return;
    }
    const lower    = query.toLowerCase();
    const filtered = LLMAState.threads.filter(t => t.title?.toLowerCase().includes(lower));
    llmaRenderThreadList(filtered);
}

// -- UI State Helpers --
function llmaShowWelcome() {
    const welcome = document.getElementById('llma-welcome');
    const chat    = document.getElementById('llma-chat-panel');
    if (welcome) welcome.style.display = '';
    if (chat)    chat.style.display    = 'none';
    LLMAState.activeThreadId = null;
    LLMAState.messages = [];
    const titleEl = document.getElementById('llma-thread-title');
    if (titleEl) titleEl.textContent = 'LLM Assistant';
    llmaUpdateContextBar();
    // Refresh the cached user profile so the personalized hero reflects any
    // memory_write tool calls from the previous conversation. Fire-and-forget
    // — we render now with what we have, then re-render once the fetch lands.
    if (typeof llmaLoadUserProfile === 'function') {
        llmaLoadUserProfile().then(() => {
            if (typeof llmaRenderPersonalizedWelcome === 'function') {
                llmaRenderPersonalizedWelcome();
            }
        }).catch(() => {});
    }
    llmaRenderWelcomeAssistants();
    llmaRenderAssistantPanelEmpty();
}

function llmaShowChatPanel() {
    const welcome = document.getElementById('llma-welcome');
    const chat    = document.getElementById('llma-chat-panel');
    if (welcome) welcome.style.display = 'none';
    if (chat)    chat.style.display    = '';
}

function llmaUpdateContextBar() {
    const label  = document.getElementById('llma-ctx-label');
    const fill   = document.getElementById('llma-ctx-fill');
    const tokens = document.getElementById('llma-ctx-tokens');

    if (!LLMAState.activeThreadId || LLMAState.messages.length === 0) {
        if (label)  label.textContent  = 'No active thread';
        if (fill)   fill.style.width   = '0%';
        if (tokens) tokens.textContent = '';
        return;
    }

    const count     = LLMAState.messages.length;
    const maxCtx    = LLMAState.settings?.defaults?.contextMessages || 0;
    // Prefer the exact token count when it's valid for the current conversation length.
    const useExact = typeof LLMAState.exactTokenCount === 'number'
        && LLMAState.exactTokenCountForLen === count;
    const tokCount  = useExact ? LLMAState.exactTokenCount : llmaApproxTokens(LLMAState.messages);
    const pct       = maxCtx > 0 ? Math.min(100, (count / maxCtx) * 100) : Math.min(100, (tokCount / 4096) * 100);

    if (label)  label.textContent  = `${count} message${count !== 1 ? 's' : ''}`;
    if (fill)   fill.style.width   = `${pct.toFixed(0)}%`;
    if (tokens && LLMAState.showTokens) {
        const prefix = useExact && LLMAState.exactTokenCountIsExact ? '' : '~';
        tokens.textContent = `${prefix}${tokCount.toLocaleString()} tokens`;
    }
}

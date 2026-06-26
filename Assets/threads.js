/* ================================================================
   LLM Assistant - threads.js
   Thread sidebar: list, search, create, switch, delete, export.
   ================================================================ */

'use strict';

// -- Bulk-select state --
// When select mode is on, each thread row gets a checkbox; selected IDs accumulate in this Set.
// The bulk bar (count + Delete + Cancel) is shown while selectMode is true.
let llmaThreadSelectMode = false;
const llmaSelectedThreadIds = new Set();

// -- Load / Fetch --
// Initial-load failure leaves the sidebar with the empty "New Chat" CTA state so the user
// can still pick a starting assistant. Console-logged but not toasted — the welcome banner
// (driven by llmaLoadAssistants/Models errors in A2) is the canonical surface for boot issues.
async function llmaLoadThreads() {
    try {
        const result = await llmaRequest('LLMAssistantGetThreads', {});
        LLMAState.threads = Array.isArray(result?.threads) ? result.threads : [];
    } catch (e) {
        console.error('[LLMAssistant] Failed to load threads:', e);
        LLMAState.threads = [];
    }
    llmaRenderThreadList(LLMAState.threads);
}

// -- Render Sidebar --
function llmaRenderThreadList(threads) {
    const list = document.getElementById('llma-thread-list');
    if (!list) return;

    // The "+" new-chat icon and the multi-select toggle only make sense once chats exist — the empty
    // state carries its own primary "New Chat" button — so hide them when the list is empty.
    const hasChats = threads.length > 0;
    document.getElementById('llma-new-thread-btn')?.style.setProperty('display', hasChats ? '' : 'none');
    document.getElementById('llma-thread-select-toggle')?.style.setProperty('display', hasChats ? '' : 'none');

    if (!hasChats) {
        list.innerHTML = `
            <div class="llma-threads-empty">
                <button class="basic-button llma-empty-new-btn" type="button">+ New Chat</button>
                <div class="llma-threads-empty-sub">Pick an assistant to start a conversation.</div>
            </div>`;
        // Reuse the existing new-chat handler bound to #llma-new-thread-btn (hidden while empty).
        list.querySelector('.llma-empty-new-btn')?.addEventListener('click',
            () => document.getElementById('llma-new-thread-btn')?.click());
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
            const checked = llmaSelectedThreadIds.has(thread.id) ? 'checked' : '';
            const checkbox = llmaThreadSelectMode
                ? `<input type="checkbox" class="llma-thread-checkbox" data-id="${llmaEscapeHtml(thread.id)}" ${checked} aria-label="Select chat">`
                : '';
            html += `
                <div class="llma-thread-item${isActive ? ' active' : ''}${llmaThreadSelectMode ? ' select-mode' : ''}"
                     data-id="${llmaEscapeHtml(thread.id)}"
                     title="${llmaEscapeHtml(thread.title || 'Untitled')} \u2014 ${llmaRelativeTime(thread.updated || thread.created)}"
                     role="button" tabindex="0">
                    ${checkbox}
                    <div class="llma-thread-dot" style="background:${dotColor}"></div>
                    <span class="llma-thread-name">${llmaEscapeHtml(thread.title || 'Untitled')}</span>
                    <button class="llma-thread-rename" data-id="${llmaEscapeHtml(thread.id)}"
                            title="Rename thread" aria-label="Rename thread">&#9998;</button>
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
            // Swallow clicks on hover-only action buttons; they handle their own events.
            if (e.target.closest('.llma-thread-del, .llma-thread-rename, .llma-thread-rename-input')) return;
            // In select mode, clicking the row toggles the checkbox instead of switching threads.
            if (llmaThreadSelectMode) {
                if (!e.target.classList.contains('llma-thread-checkbox')) {
                    const box = item.querySelector('.llma-thread-checkbox');
                    if (box) {
                        box.checked = !box.checked;
                        llmaToggleSelectedThread(item.dataset.id, box.checked);
                    }
                }
                return;
            }
            llmaSwitchThread(item.dataset.id);
        });
        item.addEventListener('keydown', (e) => {
            // F2 starts rename on the focused item (matches OS file-manager convention).
            if (e.key === 'F2') {
                e.preventDefault();
                llmaBeginRenameThread(item);
                return;
            }
            if (e.key === 'Enter' || e.key === ' ') {
                // Ignore Enter when the user is typing in the inline rename input.
                if (e.target.classList?.contains('llma-thread-rename-input')) return;
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
    list.querySelectorAll('.llma-thread-rename').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            const item = btn.closest('.llma-thread-item');
            if (item) llmaBeginRenameThread(item);
        });
    });
    list.querySelectorAll('.llma-thread-checkbox').forEach(box => {
        box.addEventListener('click', (e) => e.stopPropagation());
        box.addEventListener('change', () => llmaToggleSelectedThread(box.dataset.id, box.checked));
    });
}

// Toggle bulk-select mode. Re-renders the sidebar so each row picks up / drops its checkbox.
function llmaToggleThreadSelectMode(force) {
    const next = typeof force === 'boolean' ? force : !llmaThreadSelectMode;
    llmaThreadSelectMode = next;
    if (!next) {
        llmaSelectedThreadIds.clear();
    }
    const toggleBtn = document.getElementById('llma-thread-select-toggle');
    if (toggleBtn) toggleBtn.setAttribute('aria-pressed', next ? 'true' : 'false');
    document.getElementById('llma-bulk-bar').style.display = next ? '' : 'none';
    llmaRenderThreadList(LLMAState.threads);
    llmaUpdateBulkCount();
}

function llmaToggleSelectedThread(id, selected) {
    if (selected) llmaSelectedThreadIds.add(id);
    else llmaSelectedThreadIds.delete(id);
    llmaUpdateBulkCount();
}

function llmaUpdateBulkCount() {
    const count = llmaSelectedThreadIds.size;
    const label = document.getElementById('llma-bulk-count');
    if (label) label.textContent = `${count} selected`;
    const del = document.getElementById('llma-bulk-delete');
    if (del) del.disabled = count === 0;
}

// Confirms then deletes every selected thread by calling the single-thread endpoint in parallel.
// Errors per thread are toasted individually so a partial failure doesn't lose the user's
// progress on the others. Exits select mode on completion regardless of partial errors.
async function llmaBulkDeleteThreads() {
    const ids = Array.from(llmaSelectedThreadIds);
    if (ids.length === 0) return;
    if (!confirm(`Delete ${ids.length} chat${ids.length === 1 ? '' : 's'}? This cannot be undone.`)) return;
    let ok = 0, failed = 0;
    await Promise.all(ids.map(async (id) => {
        try {
            await llmaRequest('LLMAssistantDeleteThread', { threadId: id });
            ok++;
            // Drop locally so a failed re-fetch doesn't strand a stale entry in the sidebar.
            LLMAState.threads = LLMAState.threads.filter(t => t.id !== id);
            if (LLMAState.activeThreadId === id) {
                LLMAState.activeThreadId = null;
                LLMAState.messages = [];
                LLMAState.assets = [];
                llmaSetSessionState({ activeThreadId: null });
                llmaShowWelcome();
            }
        } catch (e) {
            failed++;
            console.error('[LLMAssistant] Bulk delete failed for thread', id, e);
        }
    }));
    llmaSelectedThreadIds.clear();
    llmaToggleThreadSelectMode(false);
    if (failed === 0) llmaShowToast(`Deleted ${ok} chat${ok === 1 ? '' : 's'}.`, 'info');
    else llmaShowToast(`Deleted ${ok}, failed ${failed}.`, failed === ok + failed ? 'error' : 'info');
}

// One-shot wiring of the bulk-bar buttons. Idempotent — guards via dataset flag so callers
// can invoke it multiple times during page setup without stacking listeners.
function llmaSetupBulkBar() {
    const toggle = document.getElementById('llma-thread-select-toggle');
    if (toggle && !toggle.dataset.llmaBound) {
        toggle.dataset.llmaBound = '1';
        toggle.addEventListener('click', () => llmaToggleThreadSelectMode());
    }
    const cancel = document.getElementById('llma-bulk-cancel');
    if (cancel && !cancel.dataset.llmaBound) {
        cancel.dataset.llmaBound = '1';
        cancel.addEventListener('click', () => llmaToggleThreadSelectMode(false));
    }
    const del = document.getElementById('llma-bulk-delete');
    if (del && !del.dataset.llmaBound) {
        del.dataset.llmaBound = '1';
        del.addEventListener('click', llmaBulkDeleteThreads);
    }
}

// Swaps the thread name span for an inline input. Enter commits via LLMAssistantRenameThread,
// Esc / blur cancels. On commit success we update the local thread row in-place rather than
// re-rendering the whole list so focus & scroll position survive.
function llmaBeginRenameThread(itemEl) {
    if (!itemEl) return;
    const nameSpan = itemEl.querySelector('.llma-thread-name');
    if (!nameSpan || itemEl.querySelector('.llma-thread-rename-input')) return;
    const threadId = itemEl.dataset.id;
    const original = (LLMAState.threads.find(t => t.id === threadId)?.title) || nameSpan.textContent || '';
    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'llma-thread-rename-input';
    input.value = original;
    input.setAttribute('aria-label', 'Rename thread');
    input.maxLength = 200;
    nameSpan.replaceWith(input);
    input.focus();
    input.select();

    let committed = false;
    const restore = (newTitle) => {
        const span = document.createElement('span');
        span.className = 'llma-thread-name';
        span.textContent = newTitle;
        // Guard against the input already being detached (re-render mid-commit).
        if (input.parentNode) input.replaceWith(span);
    };
    const commit = async () => {
        if (committed) return;
        committed = true;
        const next = input.value.trim();
        if (!next || next === original) {
            restore(original);
            return;
        }
        try {
            const res = await llmaRequest('LLMAssistantRenameThread', { threadId, title: next });
            if (res?.success === false) {
                llmaShowToast(res.error || 'Rename failed', 'error');
                restore(original);
                return;
            }
            const t = LLMAState.threads.find(x => x.id === threadId);
            if (t) {
                t.title = next;
                t.updated = new Date().toISOString();
            }
            restore(next);
            const titleEl = document.getElementById('llma-thread-title');
            if (LLMAState.activeThreadId === threadId && titleEl) titleEl.textContent = next;
            llmaShowToast('Renamed', 'info');
        } catch (e) {
            llmaShowToast(llmaShortError(e) || 'Rename failed', 'error');
            restore(original);
        }
    };
    input.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') { e.preventDefault(); commit(); }
        else if (e.key === 'Escape') { e.preventDefault(); committed = true; restore(original); }
    });
    input.addEventListener('blur', commit);
    input.addEventListener('click', (e) => e.stopPropagation());
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
// Tool calls are included by default in json/md exports (caller can opt out via opts.includeToolCalls=false).
// txt deliberately stays clean — text export is for sharing with non-technical readers.
function llmaExportThread(format, opts = {}) {
    if (!LLMAState.messages.length) {
        llmaShowToast('No messages to export', 'info');
        return;
    }

    const includeToolCalls = opts.includeToolCalls !== false;
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
            messages:  LLMAState.messages.map(m => {
                const out = { role: m.role, content: m.content, timestamp: m.timestamp };
                if (includeToolCalls && Array.isArray(m.toolCalls) && m.toolCalls.length > 0) {
                    out.toolCalls = m.toolCalls;
                }
                if (m.meta) out.meta = m.meta;
                return out;
            }),
        };
        llmaDownloadFile(`${safeName}_${timestamp}.json`, JSON.stringify(data, null, 2), 'application/json');
    } else if (format === 'md') {
        const lines = [`# ${title}`, '', `*Exported: ${new Date().toLocaleDateString()}*`, ''];
        if (assistant) lines.push(`*Assistant: ${assistant.name}*`, '');
        for (const msg of LLMAState.messages) {
            const role = msg.role === 'user' ? '**You**' : `**${assistant?.name || 'Assistant'}**`;
            lines.push(`${role}`, '', msg.content, '');
            if (includeToolCalls && Array.isArray(msg.toolCalls) && msg.toolCalls.length > 0) {
                lines.push('> **Tool calls:**');
                for (const tc of msg.toolCalls) {
                    const argsStr   = llmaTruncateForExport(tc.arguments);
                    const resultStr = llmaTruncateForExport(tc.result);
                    lines.push(`> - \`${tc.name || '?'}\` — \`${argsStr}\` → \`${resultStr}\``);
                }
                lines.push('');
            }
            lines.push('---', '');
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

// Compact tool-call JSON to a single line and cap at 200 chars so the Markdown bullet stays readable.
// JSON export keeps full content; this helper is only for the human-facing MD summary.
function llmaTruncateForExport(value) {
    let str;
    try { str = typeof value === 'string' ? value : JSON.stringify(value); }
    catch { str = String(value); }
    str = (str || '').replace(/\s+/g, ' ').replace(/`/g, 'ˋ');
    if (str.length > 200) str = str.slice(0, 197) + '…';
    return str;
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
    const bar    = document.querySelector('.llma-context-bar');
    const label  = document.getElementById('llma-ctx-label');
    const fill   = document.getElementById('llma-ctx-fill');
    const tokens = document.getElementById('llma-ctx-tokens');

    // Nothing meaningful to show without a conversation — hide the whole bar rather than displaying a
    // contradictory "No active thread" line + empty track next to the top-bar heading.
    if (!LLMAState.activeThreadId || LLMAState.messages.length === 0) {
        if (bar) bar.style.display = 'none';
        return;
    }
    if (bar) bar.style.display = '';

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

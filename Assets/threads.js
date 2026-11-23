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

// -- Branch (tree) model helpers --
// A thread stores ALL message nodes across every branch (LLMAState.allNodes); each node has a parentId,
// and the thread has an activeLeafId. The conversation the user sees (LLMAState.messages) is the linear
// path root→activeLeafId. Forking/editing/regenerating just add a node and move the active leaf.

function llmaParentId(node) {
    const p = node ? node.parentId : undefined;
    return (p === undefined || p === null || p === '') ? null : p;
}

// Walk parentId from the active leaf up to the root, then reverse → the active conversation path.
function llmaComputeActivePath(allNodes, leafId) {
    const path = [];
    if (!Array.isArray(allNodes) || allNodes.length === 0) return path;
    const byId = new Map();
    for (const n of allNodes) { if (n && n.id) byId.set(n.id, n); }
    let leaf = leafId;
    if (!leaf || !byId.has(leaf)) leaf = allNodes[allNodes.length - 1]?.id;
    const seen = new Set();
    let cur = leaf;
    while (cur && byId.has(cur) && !seen.has(cur)) {
        seen.add(cur);
        const node = byId.get(cur);
        path.push(node);
        cur = llmaParentId(node);
    }
    path.reverse();
    return path;
}

// Ingest a server thread blob into branch state: full node set, active leaf, and the active path.
function llmaIngestThread(thread) {
    LLMAState.allNodes = Array.isArray(thread.messages) ? thread.messages : [];
    LLMAState.activeLeafId = thread.activeLeafId
        || (LLMAState.allNodes.length ? LLMAState.allNodes[LLMAState.allNodes.length - 1].id : null);
    LLMAState.messages = llmaComputeActivePath(LLMAState.allNodes, LLMAState.activeLeafId);
    return LLMAState.messages;
}

function llmaChildrenOf(nodeId) {
    return (LLMAState.allNodes || []).filter(n => llmaParentId(n) === (nodeId ?? null));
}

// All nodes sharing a parent with `node` (in creation order) — the set of branches at that point.
function llmaSiblingsOf(node) {
    if (!node) return [];
    const pid = llmaParentId(node);
    return (LLMAState.allNodes || []).filter(n => llmaParentId(n) === pid);
}

// Follow the most-recent child chain down to a leaf (a node with no children).
function llmaDescendToLeaf(nodeId) {
    let cur = nodeId;
    const guard = new Set();
    while (cur && !guard.has(cur)) {
        guard.add(cur);
        const kids = llmaChildrenOf(cur);
        if (kids.length === 0) break;
        cur = kids[kids.length - 1].id;
    }
    return cur;
}

// Move the thread's active leaf (Fork + branch switcher): optimistically re-render the new path, then
// persist server-side and adopt the authoritative thread.
async function llmaSetActiveLeaf(leafId, opts = {}) {
    if (!LLMAState.activeThreadId || !leafId) return;
    LLMAState.activeLeafId = leafId;
    LLMAState.messages = llmaComputeActivePath(LLMAState.allNodes, leafId);
    llmaRenderMessageHistory(LLMAState.messages);
    llmaUpdateContextBar();
    if (typeof llmaUpdatePanelStats === 'function') llmaUpdatePanelStats();
    try {
        const res = await llmaRequest('LLMAssistantSetActiveLeaf', { threadId: LLMAState.activeThreadId, messageId: leafId });
        if (res?.success && res.thread) {
            const t = typeof res.thread === 'string' ? JSON.parse(res.thread) : res.thread;
            llmaIngestThread(t);
        } else if (res && res.success === false) {
            llmaShowToast(res.error || 'Failed to switch branch', 'error');
            await llmaReloadActiveThread();
        }
    } catch {
        llmaShowToast('Failed to switch branch', 'error');
        await llmaReloadActiveThread();
        return;
    }
    if (opts.focusInput) {
        setTimeout(() => document.getElementById('llma-input')?.focus(), 50);
    }
}

// Switch the active branch at a divergence point: pick the prev/next sibling of `node`, descend to its
// leaf, and make that the active path.
async function llmaSwitchBranch(nodeId, dir) {
    if (LLMAState.isGenerating) { llmaShowToast('Stop generation before switching branches', 'info'); return; }
    const node = (LLMAState.allNodes || []).find(n => n.id === nodeId);
    if (!node) return;
    const sibs = llmaSiblingsOf(node);
    if (sibs.length < 2) return;
    const idx = sibs.findIndex(n => n.id === nodeId);
    const target = sibs[idx + dir];
    if (!target) return;
    await llmaSetActiveLeaf(llmaDescendToLeaf(target.id));
}

// Jump directly to a specific sibling branch (from the branch-picker dropdown).
async function llmaSwitchToBranch(targetNodeId) {
    if (LLMAState.isGenerating) { llmaShowToast('Stop generation before switching branches', 'info'); return; }
    if (!targetNodeId) return;
    await llmaSetActiveLeaf(llmaDescendToLeaf(targetNodeId));
}

// Fork: make this message the tip of the active branch; the next message sent branches from here.
async function llmaForkFromMessage(msgId) {
    if (LLMAState.isGenerating) { llmaShowToast('Stop generation before forking', 'info'); return; }
    await llmaSetActiveLeaf(msgId, { focusInput: true });
    llmaShowToast('Forked — your next message starts a new branch', 'info');
}

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
    llmaSessions.onThreadsLoaded(LLMAState.threads);
    llmaLibrary.refresh();
}

// -- Branch picker (sidebar) --
// Chats with branches (an edit or regenerate created a sibling somewhere — see the server's
// `hasBranches` flag) get a small toggle to expand a flat list of every branch's endpoint (leaf),
// so you can jump straight to a specific version of the conversation without opening the chat first.
const llmaExpandedBranchThreads = new Set();
// Cache of fetched branch node arrays, keyed by thread id. Only used for chats OTHER than the
// active one — the active chat's nodes can change live, so its branches are always read fresh
// from LLMAState.allNodes instead of this cache (see llmaRenderThreadBranches).
const llmaBranchDataCache = new Map();

// A leaf is any node nothing else points to as a parent — ie the tip of one branch.
function llmaComputeLeaves(allNodes) {
    if (!Array.isArray(allNodes) || allNodes.length === 0) return [];
    const hasChildren = new Set();
    for (const n of allNodes) {
        const pid = llmaParentId(n);
        if (pid !== null) hasChildren.add(pid);
    }
    return allNodes.filter(n => n && n.id && !hasChildren.has(n.id));
}

function llmaLeafPreviewLabel(leaf) {
    const raw = (leaf.content || '').replace(/\s+/g, ' ').trim();
    const text = raw ? (raw.length > 46 ? raw.slice(0, 46) + '…' : raw) : translate('(empty)');
    return `${leaf.role === 'user' ? translate('You') : translate('Reply')}: ${text}`;
}

async function llmaToggleBranchExpand(threadId) {
    if (llmaExpandedBranchThreads.has(threadId)) {
        llmaExpandedBranchThreads.delete(threadId);
        llmaRenderThreadList(LLMAState.threads);
        return;
    }
    llmaExpandedBranchThreads.add(threadId);
    if (threadId !== LLMAState.activeThreadId && !llmaBranchDataCache.has(threadId)) {
        llmaRenderThreadList(LLMAState.threads); // paints a "Loading…" row while the fetch is in flight
        try {
            const result = await llmaRequest('LLMAssistantGetThread', { threadId });
            const thread = result?.thread ? (typeof result.thread === 'string' ? JSON.parse(result.thread) : result.thread) : null;
            llmaBranchDataCache.set(threadId, Array.isArray(thread?.messages) ? thread.messages : []);
        } catch {
            llmaBranchDataCache.set(threadId, []);
        }
    }
    llmaRenderThreadList(LLMAState.threads);
}

// Switches to `threadId` (if not already active) and jumps straight to `leafId` within it.
async function llmaJumpToBranch(threadId, leafId) {
    if (LLMAState.activeThreadId !== threadId) {
        await llmaSwitchThread(threadId);
    }
    if (LLMAState.activeThreadId === threadId && LLMAState.activeLeafId !== leafId) {
        await llmaSetActiveLeaf(leafId);
    }
}

// Builds the expanded branch sub-list HTML for one chat, or null if it isn't expanded.
function llmaRenderThreadBranchesHtml(thread) {
    if (!llmaExpandedBranchThreads.has(thread.id)) return '';
    const allNodes = thread.id === LLMAState.activeThreadId ? (LLMAState.allNodes || []) : llmaBranchDataCache.get(thread.id);
    if (!allNodes) {
        return `<div class="llma-thread-branches"><div class="llma-thread-branch-loading">${translate('Loading branches…')}</div></div>`;
    }
    const leaves = llmaComputeLeaves(allNodes).sort((a, b) => new Date(b.timestamp || 0) - new Date(a.timestamp || 0));
    if (leaves.length < 2) return '';
    const activeLeafId = thread.id === LLMAState.activeThreadId ? LLMAState.activeLeafId : null;
    const rows = leaves.map(leaf => `
        <button type="button" class="llma-thread-branch-row${leaf.id === activeLeafId ? ' active' : ''}"
                data-thread-id="${llmaEscapeHtml(thread.id)}" data-leaf-id="${llmaEscapeHtml(leaf.id)}">
            <span class="llma-thread-branch-text">${llmaEscapeHtml(llmaLeafPreviewLabel(leaf))}</span>
            <span class="llma-thread-branch-time">${leaf.timestamp ? llmaRelativeTime(leaf.timestamp) : ''}</span>
        </button>`).join('');
    return `<div class="llma-thread-branches">${rows}</div>`;
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
    document.getElementById('llma-thread-group-toggle')?.style.setProperty('display', hasChats ? '' : 'none');

    if (!hasChats) {
        list.innerHTML = `
            <div class="llma-threads-empty">
                <button class="basic-button llma-empty-new-btn" type="button">+ ${translate('New Chat')}</button>
                <div class="llma-threads-empty-sub">${translate('Pick an assistant to start a conversation.')}</div>
            </div>`;
        // Reuse the existing new-chat handler bound to #llma-new-thread-btn (hidden while empty).
        list.querySelector('.llma-empty-new-btn')?.addEventListener('click',
            () => document.getElementById('llma-new-thread-btn')?.click());
        return;
    }

    // Searching: one flat list, best match first, each chat with the snippets that matched (chat-library.js). Otherwise: sessions the assistant started are listed under the chat
    // that started them (and archived ones not at all), so only the others are grouped, and pinned chats are a group of their own at the top.
    let searching = llmaLibrary.searching;
    let split = searching
        ? { roots: llmaLibrary.visibleThreads(threads), kids: new Map() }
        : llmaSessions.split(threads.filter(t => llmaCodeMode.showsThread(t)));
    let roots = split.roots;
    let kids = split.kids;
    let entries;
    if (searching) {
        // No group label: the note above says how many there are.
        entries = [['', roots]];
    }
    else {
        let newestFirst = roots.sort((a, b) => new Date(b.updatedAt || b.createdAt) - new Date(a.updatedAt || a.createdAt));
        let pinned = newestFirst.filter(t => t.pinned && llmaLibrary.canPin(t));
        let rest = pinned.length ? newestFirst.filter(t => !pinned.includes(t)) : newestFirst;
        entries = Object.entries(llmaThreadGroupMode() == 'assistant' ? llmaGroupByAssistant(rest) : llmaGroupByDate(rest));
        if (pinned.length) {
            entries.unshift(['Pinned', pinned]);
        }
    }
    let html = searching ? llmaLibrary.statusHtml(roots.length) : '';

    for (const [label, group] of entries) {
        if (group.length === 0) continue;
        html += !label ? '' : `<div class="llma-thread-group-label${llmaThreadSelectMode ? ' selectable' : ''}" data-ids="${llmaEscapeHtml(group.map(t => t.id).join(','))}"${llmaThreadSelectMode ? ` title="${llmaEscapeHtml(translate('Select this group'))}"` : ''}>${llmaEscapeHtml(translate(label))}</div>`;
        const rows = [];
        const addRows = (thread, depth) => {
            rows.push([thread, depth]);
            if (!llmaSessions.collapsed.has(thread.id)) {
                for (const kid of kids.get(thread.id) || []) {
                    addRows(kid, depth + 1);
                }
            }
        };
        for (const thread of group) {
            addRows(thread, 0);
        }
        for (const [thread, depth] of rows) {
            const isActive  = thread.id === LLMAState.activeThreadId;
            const checked = llmaSelectedThreadIds.has(thread.id) ? 'checked' : '';
            const checkbox = llmaThreadSelectMode
                ? `<input type="checkbox" class="llma-thread-checkbox" data-id="${llmaEscapeHtml(thread.id)}" ${checked} aria-label="${llmaEscapeHtml(translate('Select chat'))}">`
                : '';
            const assistant = LLMAState.assistants.find(a => a.id === thread.assistantId);
            const badge = assistant
                ? `<span class="llma-thread-assist-badge" style="background:${llmaEscapeHtml(assistant.color || 'var(--emphasis)')}" title="${llmaEscapeHtml(assistant.name || translate('Assistant'))}">${llmaCategoryIcon(assistant.icon || assistant.category || 'chat')}</span>`
                : '';
            const expanded = llmaExpandedBranchThreads.has(thread.id);
            const branchToggle = thread.hasBranches
                ? `<button class="llma-thread-branch-toggle${expanded ? ' expanded' : ''}" data-id="${llmaEscapeHtml(thread.id)}"
                        title="${expanded ? llmaEscapeHtml(translate('Hide branches')) : llmaEscapeHtml(translate('Show branches'))}" aria-label="${llmaEscapeHtml(translate('Toggle branches'))}">&#9656;</button>`
                : '';
            const displayTitle = thread.title || translate('Untitled');
            let pinLabel = llmaEscapeHtml(thread.pinned ? translate('Unpin chat') : translate('Pin chat'));
            let pinButton = llmaLibrary.canPin(thread)
                ? `<button class="llma-thread-pin${thread.pinned ? ' pinned' : ''}" data-id="${llmaEscapeHtml(thread.id)}"
                        title="${pinLabel}" aria-label="${pinLabel}" aria-pressed="${thread.pinned ? 'true' : 'false'}">${thread.pinned ? '&#9733;' : '&#9734;'}</button>`
                : '';
            html += `
                <div class="llma-thread-item${isActive ? ' active' : ''}${llmaThreadSelectMode ? ' select-mode' : ''}${depth > 0 ? ' llma-thread-child' : ''}${thread.pinned ? ' pinned' : ''}"
                     data-id="${llmaEscapeHtml(thread.id)}"
                     data-depth="${depth}"
                     title="${llmaEscapeHtml(displayTitle)} \u2014 ${llmaEscapeHtml(llmaRelativeTime(thread.updatedAt || thread.createdAt))}"
                     role="button" tabindex="0">
                    ${checkbox}
                    ${llmaSessions.isSession(thread) ? llmaSessions.dotHtml(thread.sessionStatus) : badge}
                    <span class="llma-thread-name">${llmaEscapeHtml(displayTitle)}</span>
                    ${llmaSessions.parentAddonsHtml(thread, kids)}
                    ${thread.mode == 'code' ? `<span class="badge text-bg-secondary llma-thread-mode-badge">${translate('code')}</span>` : ''}
                    ${thread.scheduleId ? `<span class="badge text-bg-secondary llma-thread-mode-badge">${translate('schedule')}</span>` : ''}
                    ${branchToggle}
                    ${pinButton}
                    <button class="llma-thread-rename" data-id="${llmaEscapeHtml(thread.id)}"
                            title="${llmaEscapeHtml(translate('Rename chat'))}" aria-label="${llmaEscapeHtml(translate('Rename chat'))}">&#9998;</button>
                    <button class="llma-thread-del" data-id="${llmaEscapeHtml(thread.id)}"
                            title="${llmaEscapeHtml(translate('Delete chat'))}" aria-label="${llmaEscapeHtml(translate('Delete chat'))}">&times;</button>
                </div>
                ${llmaRenderThreadBranchesHtml(thread)}
                ${searching ? llmaLibrary.snippetsHtml(thread) : ''}`;
        }
    }
    if (searching) {
        html += llmaLibrary.moreHtml();
    }

    list.innerHTML = html;
    llmaBindThreadListEvents(list);
    if (llmaThreadSelectMode) {
        llmaUpdateBulkCount(); // the visible set changed (search, new chat): refresh Select-all state
    }
}

function llmaBindThreadListEvents(list) {
    list.querySelectorAll('.llma-thread-group-label.selectable').forEach(label => {
        label.addEventListener('click', () => {
            const ids = label.dataset.ids.split(',').filter(Boolean);
            const allOn = ids.every(id => llmaSelectedThreadIds.has(id));
            llmaSetThreadsSelected(ids, !allOn);
        });
    });
    list.querySelectorAll('.llma-thread-item').forEach(item => {
        item.addEventListener('click', (e) => {
            // Swallow clicks on hover-only action buttons; they handle their own events.
            if (e.target.closest('.llma-thread-del, .llma-thread-rename, .llma-thread-pin, .llma-thread-rename-input, .llma-thread-branch-toggle, .llma-session-toggle')) return;
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
                if (e.target.classList?.contains('llma-thread-rename-input') || e.target.closest?.('.llma-thread-pin')) return;
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
    for (let btn of list.querySelectorAll('.llma-thread-pin')) {
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            llmaLibrary.togglePin(btn.dataset.id);
        });
    }
    llmaLibrary.bind(list);
    llmaSessions.bind(list);
    list.querySelectorAll('.llma-thread-branch-toggle:not(.llma-session-toggle)').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            llmaToggleBranchExpand(btn.dataset.id);
        });
    });
    list.querySelectorAll('.llma-thread-branch-row').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            llmaJumpToBranch(btn.dataset.threadId, btn.dataset.leafId);
        });
    });
    list.querySelectorAll('.llma-thread-checkbox').forEach(box => {
        box.addEventListener('click', (e) => e.stopPropagation());
        box.addEventListener('change', () => llmaToggleSelectedThread(box.dataset.id, box.checked));
    });
}

/** Current sidebar grouping: 'date' (default) or 'assistant'. Remembered per browser. */
function llmaThreadGroupMode() {
    try {
        return localStorage.getItem('llma_thread_group') == 'assistant' ? 'assistant' : 'date';
    }
    catch {
        return 'date';
    }
}

/** Flip the sidebar grouping, remember it, and re-render. */
function llmaToggleThreadGroupMode() {
    const next = llmaThreadGroupMode() == 'assistant' ? 'date' : 'assistant';
    try {
        localStorage.setItem('llma_thread_group', next);
    }
    catch { /* private mode: the choice just won't persist */ }
    llmaSyncGroupToggle();
    llmaRenderThreadList(LLMAState.threads);
}

/** Keep the header pill's label in step with the grouping mode. */
function llmaSyncGroupToggle() {
    const btn = document.getElementById('llma-thread-group-toggle');
    if (btn) {
        btn.textContent = llmaThreadGroupMode() == 'assistant' ? translate('By assistant') : translate('By date');
    }
}

// Toggle bulk-select mode. Re-renders the sidebar so each row picks up / drops its checkbox.
function llmaToggleThreadSelectMode(force) {
    const next = typeof force === 'boolean' ? force : !llmaThreadSelectMode;
    llmaThreadSelectMode = next;
    if (!next) {
        llmaSelectedThreadIds.clear();
    }
    const toggleBtn = document.getElementById('llma-thread-select-toggle');
    if (toggleBtn) {
        toggleBtn.setAttribute('aria-pressed', next ? 'true' : 'false');
        toggleBtn.textContent = next ? translate('Done') : translate('Select');
    }
    document.getElementById('llma-bulk-bar').style.display = next ? '' : 'none';
    llmaRenderThreadList(LLMAState.threads);
    llmaUpdateBulkCount();
}

function llmaToggleSelectedThread(id, selected) {
    if (selected) llmaSelectedThreadIds.add(id);
    else llmaSelectedThreadIds.delete(id);
    llmaUpdateBulkCount();
}

/** Ids of the threads currently listed in the sidebar (respects the active search filter). */
function llmaVisibleThreadIds() {
    return Array.from(document.querySelectorAll('#llma-thread-list .llma-thread-item')).map(i => i.dataset.id);
}

/** Select or deselect a set of threads and sync the row checkboxes without a full re-render. */
function llmaSetThreadsSelected(ids, selected) {
    const wanted = new Set(ids);
    for (const id of ids) {
        if (selected) {
            llmaSelectedThreadIds.add(id);
        }
        else {
            llmaSelectedThreadIds.delete(id);
        }
    }
    for (const box of document.querySelectorAll('#llma-thread-list .llma-thread-checkbox')) {
        if (wanted.has(box.dataset.id)) {
            box.checked = selected;
        }
    }
    llmaUpdateBulkCount();
}

function llmaUpdateBulkCount() {
    const count = llmaSelectedThreadIds.size;
    const label = document.getElementById('llma-bulk-count');
    if (label) {
        label.textContent = `${count} ${translate('selected')}`;
        label.title = label.textContent; // full text when the narrow sidebar ellipsizes it
    }
    const all = document.getElementById('llma-bulk-all');
    if (all) {
        const visible = llmaVisibleThreadIds();
        const chosen = visible.filter(id => llmaSelectedThreadIds.has(id)).length;
        all.checked = visible.length > 0 && chosen == visible.length;
        all.indeterminate = chosen > 0 && chosen < visible.length;
    }
    const del = document.getElementById('llma-bulk-delete');
    if (del) del.disabled = count === 0;
}

// Confirms then deletes every selected thread by calling the single-thread endpoint in parallel.
// Errors per thread are toasted individually so a partial failure doesn't lose the user's
// progress on the others. Exits select mode on completion regardless of partial errors.
async function llmaBulkDeleteThreads() {
    const ids = Array.from(llmaSelectedThreadIds);
    if (ids.length === 0) return;
    const confirmNoun = ids.length === 1 ? translate('chat') : translate('chats');
    if (!confirm(`${translate('Delete')} ${ids.length} ${confirmNoun}? ${translate('This cannot be undone.')}`)) return;
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
    // The sessions the deleted chats started went with them: read the list again, and leave a chat that is gone.
    await llmaLoadThreads();
    if (LLMAState.activeThreadId && LLMAState.threads.length > 0 && !LLMAState.threads.some(t => t.id === LLMAState.activeThreadId)) {
        LLMAState.activeThreadId = null;
        LLMAState.messages = [];
        LLMAState.assets = [];
        llmaSetSessionState({ activeThreadId: null });
        llmaShowWelcome();
    }
    if (failed === 0) llmaShowToast(`${translate('Deleted')} ${ok} ${ok === 1 ? translate('chat') : translate('chats')}.`, 'info');
    else llmaShowToast(`${translate('Deleted')} ${ok}, ${translate('failed')} ${failed}.`, failed === ok + failed ? 'error' : 'info');
}

// One-shot wiring of the bulk-bar buttons. Idempotent — guards via dataset flag so callers
// can invoke it multiple times during page setup without stacking listeners.
function llmaSetupBulkBar() {
    const groupBtn = document.getElementById('llma-thread-group-toggle');
    if (groupBtn && !groupBtn.dataset.llmaBound) {
        groupBtn.dataset.llmaBound = '1';
        groupBtn.addEventListener('click', llmaToggleThreadGroupMode);
        llmaSyncGroupToggle();
    }
    const toggle = document.getElementById('llma-thread-select-toggle');
    if (toggle && !toggle.dataset.llmaBound) {
        toggle.dataset.llmaBound = '1';
        toggle.addEventListener('click', () => llmaToggleThreadSelectMode());
    }
    const all = document.getElementById('llma-bulk-all');
    if (all && !all.dataset.llmaBound) {
        all.dataset.llmaBound = '1';
        all.addEventListener('change', () => llmaSetThreadsSelected(llmaVisibleThreadIds(), all.checked));
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
    input.setAttribute('aria-label', translate('Rename thread'));
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
                t.updatedAt = new Date().toISOString();
            }
            restore(next);
            const titleEl = document.getElementById('llma-thread-title');
            if (LLMAState.activeThreadId === threadId && titleEl) titleEl.textContent = next;
            llmaLibrary.refresh();
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
        const result = await llmaRequest('LLMAssistantCreateThread', { assistantId: assistantId || '', title: initialTitle, mode: llmaCodeMode.modeForNewChat, rememberChat: LLMAState.activeThreadMemoryExtraction !== false });
        if (!result?.success || !result.thread) {
            llmaShowToast(result?.error || 'Failed to create chat', 'error');
            return;
        }
        thread = typeof result.thread === 'string' ? JSON.parse(result.thread) : result.thread;
    } catch {
        llmaShowToast('Failed to create chat', 'error');
        return;
    }

    LLMAState.threads.unshift({
        id:           thread.id,
        title:        thread.title,
        assistantId:  thread.assistantId,
        createdAt:    thread.createdAt,
        updatedAt:    thread.updatedAt,
        messageCount: 0,
        mode:         thread.mode,
    });

    LLMAState.activeThreadId    = thread.id;
    llmaContext.activeCompaction = null;
    LLMAState.messages          = [];
    LLMAState.allNodes          = [];
    LLMAState.activeLeafId      = null;
    LLMAState.assets            = [];
    LLMAState.activeAssistantId = assistantId || LLMAState.activeAssistantId;
    llmaSetSessionState({ activeThreadId: thread.id });

    // A Tools toggle flipped before this thread existed (LLMAState.activeThreadToolsEnabled was set
    // against no thread id) needs to land on the thread now that it has one, BEFORE the caller's first
    // message goes out — otherwise the override silently never took effect server-side.
    if (typeof LLMAState.activeThreadToolsEnabled === 'boolean') {
        try {
            await llmaRequest('LLMAssistantSetThreadToolsEnabled', { threadId: thread.id, enabled: LLMAState.activeThreadToolsEnabled });
        } catch { /* best-effort — worst case this send falls back to the assistant default */ }
    }
    if (typeof llmaUpdateToolsToggleBtn === 'function') llmaUpdateToolsToggleBtn();
    // "Don't remember this chat", flipped before the chat existed, was sent with the creation itself (rememberChat), so there is no second call that could fail and leave the chat remembered.
    if (typeof llmaUpdateMemoryToggleBtn === 'function') llmaUpdateMemoryToggleBtn();

    const titleEl = document.getElementById('llma-thread-title');
    if (titleEl) titleEl.textContent = thread.title;

    await llmaCodeMode.onThreadCreated(thread);
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
        llmaShowToast('Stop generation before switching chats', 'info');
        return;
    }

    try {
        const result = await llmaRequest('LLMAssistantGetThread', { threadId });
        const thread = result?.thread
            ? (typeof result.thread === 'string' ? JSON.parse(result.thread) : result.thread)
            : null;
        if (!thread) { llmaShowToast('Chat not found', 'error'); return; }

        LLMAState.activeThreadId    = thread.id;
        llmaIngestThread(thread); // sets allNodes / activeLeafId / messages (active path)
        // The summary standing in for the start of this chat on the branch showing; the marker is drawn with the messages.
        llmaContext.activeCompaction = result.activeCompaction || null;
        LLMAState.activeAssistantId = thread.assistantId || LLMAState.activeAssistantId;
        // null when the thread has no override (inherits the assistant's toolsEnabled default).
        LLMAState.activeThreadToolsEnabled = typeof thread.toolsEnabled === 'boolean' ? thread.toolsEnabled : null;
        if (typeof llmaUpdateToolsToggleBtn === 'function') llmaUpdateToolsToggleBtn();
        if (typeof llmaCloseLingeringSocket === 'function') llmaCloseLingeringSocket();
        LLMAState.activeThreadMemoryExtraction = thread.memoryExtraction === false ? false : null;
        if (typeof llmaUpdateMemoryToggleBtn === 'function') llmaUpdateMemoryToggleBtn();
        if (typeof llmaSetApprovalMode === 'function') llmaSetApprovalMode(thread.approvalMode || 'auto', false);
        llmaCodeMode.onThreadLoaded(thread);
        llmaSessions.onThreadLoaded(thread);
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
        if (titleEl) titleEl.textContent = thread.title || translate('Untitled');

        llmaShowChatPanel();
        // Restore per-thread VS mode BEFORE rendering so the wide layout + toggle are correct on first paint.
        if (typeof llmaRestoreCompareFromThread === 'function') llmaRestoreCompareFromThread();
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
        llmaShowToast('Failed to load chat', 'error');
    }
}

// -- Reload active thread --
// Re-fetches the active thread from the server and re-renders the message history.
// Used to recover after a server-side mutation (eg edit/delete message) fails or returns
// an authoritative thread state we should adopt.
async function llmaReloadActiveThread() {
    if (!LLMAState.activeThreadId) return;
    let thread = null;
    let compaction = null;
    try {
        const result = await llmaRequest('LLMAssistantGetThread', { threadId: LLMAState.activeThreadId });
        if (result?.thread) {
            thread = typeof result.thread === 'string' ? JSON.parse(result.thread) : result.thread;
            compaction = result.activeCompaction || null;
        }
    } catch { /* fall through */ }
    if (!thread) return;
    llmaIngestThread(thread); // sets allNodes / activeLeafId / messages (active path)
    llmaContext.activeCompaction = compaction;
    LLMAState.activeThreadToolsEnabled = typeof thread.toolsEnabled === 'boolean' ? thread.toolsEnabled : null;
    if (typeof llmaUpdateToolsToggleBtn === 'function') llmaUpdateToolsToggleBtn();
    if (typeof llmaCloseLingeringSocket === 'function') llmaCloseLingeringSocket();
    LLMAState.activeThreadMemoryExtraction = thread.memoryExtraction === false ? false : null;
    if (typeof llmaUpdateMemoryToggleBtn === 'function') llmaUpdateMemoryToggleBtn();
    if (typeof llmaSetApprovalMode === 'function') llmaSetApprovalMode(thread.approvalMode || 'auto', false);
    llmaRenderMessageHistory(LLMAState.messages);
    llmaSessions.onThreadLoaded(thread);
    llmaRebuildAssetsForThread();
    llmaUpdateContextBar();
    if (typeof llmaUpdatePanelStats === 'function') llmaUpdatePanelStats();
    // Update sidebar metadata too.
    const meta = LLMAState.threads.find(t => t.id === LLMAState.activeThreadId);
    if (meta) {
        meta.title        = thread.title || meta.title;
        meta.updatedAt    = thread.updatedAt || meta.updatedAt;
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
    if (!confirm(translate('Delete this chat? This cannot be undone.'))) return;

    try {
        await llmaRequest('LLMAssistantDeleteThread', { threadId });
    } catch {
        llmaShowToast('Failed to delete chat', 'error');
        return;
    }

    // The sessions the chat started went with it, at any depth.
    const gone = new Set([threadId]);
    let grew = true;
    while (grew) {
        grew = false;
        for (const t of LLMAState.threads) {
            if (t.parentThreadId && gone.has(t.parentThreadId) && !gone.has(t.id)) {
                gone.add(t.id);
                grew = true;
            }
        }
    }
    LLMAState.threads = LLMAState.threads.filter(t => !gone.has(t.id));

    if (gone.has(LLMAState.activeThreadId)) {
        LLMAState.activeThreadId = null;
        LLMAState.messages       = [];
        LLMAState.assets         = [];
        llmaSetSessionState({ activeThreadId: null });
        llmaShowWelcome();
    }

    llmaRenderThreadList(LLMAState.threads);
    llmaLibrary.refresh();
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
    // A report from a session that finished was put in this chat just before the message that was sent: show it (and the branch it sits on).
    if (Array.isArray(saved.messages) && saved.messages.some(m => m.kind == 'session_report' && !(LLMAState.allNodes || []).some(n => n.id == m.id))) {
        llmaReloadActiveThread();
    }
    // Trust the server's view of the thread.
    meta.title        = saved.title || meta.title;
    meta.updatedAt    = saved.updatedAt || meta.updatedAt;
    meta.messageCount = Array.isArray(saved.messages) ? saved.messages.length : meta.messageCount;

    const titleEl = document.getElementById('llma-thread-title');
    if (titleEl) titleEl.textContent = meta.title;
    llmaRenderThreadList(LLMAState.threads);
    // What was just said may now be found by the search that is on.
    llmaLibrary.refresh();
}

// -- Export Thread --
// JSON (every branch, can be imported again) and Markdown (the conversation on screen) come from the server's export route (chat-library.js); plain text is made here, since
// it is only for sharing with non-technical readers and has no route.
function llmaExportThread(format) {
    if (!LLMAState.messages.length) {
        llmaShowToast('No messages to export', 'info');
        return;
    }
    if (format === 'json' || format === 'md') {
        llmaLibrary.exportActive(format === 'md' ? 'markdown' : 'json');
        return;
    }

    const meta      = LLMAState.threads.find(t => t.id === LLMAState.activeThreadId);
    const title     = meta?.title || 'chat';
    const assistant = LLMAState.assistants.find(a => a.id === LLMAState.activeAssistantId);
    const safeName  = title.replace(/[^a-z0-9]+/gi, '_').toLowerCase();
    const timestamp = new Date().toISOString().slice(0, 10);

    if (format === 'txt') {
        const lines = [title, '='.repeat(title.length), ''];
        for (const msg of LLMAState.messages) {
            const role = msg.role === 'user' ? 'You' : (assistant?.name || 'Assistant');
            lines.push(`[${role}]`, msg.content, '');
        }
        llmaDownloadFile(`${safeName}_${timestamp}.txt`, lines.join('\n'), 'text/plain');
        llmaShowToast(`${translate('Exported as')} ${format.toUpperCase()}`, 'success');
    }
}

// -- Search --
// The box's text goes to chat-library.js: titles filter at once, messages are searched on the server after a pause.
function llmaFilterThreads(query) {
    llmaLibrary.onInput(query);
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
    llmaContext.updateBar();
}

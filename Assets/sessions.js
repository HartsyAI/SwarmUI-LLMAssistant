/**
 * Sessions: chats the assistant started and directs (see the README, "Sessions"). A session is an ordinary chat with a parent, so the chat list shows it nested under the
 * chat that started it, with a dot for how it is doing; opening one shows a banner with its status, Stop and Archive, and the tool call it is waiting for the user to answer.
 *
 * Built from Bootstrap's badge and spinner, core's basic-button and the list's own row, so it adds only the indent and the dot's width (llma-layout.css). The list is polled
 * (every few seconds, only while some session is working) rather than pushed: a session runs with no socket, so there is nothing to push on.
 */
class LLMASessions {

    constructor() {
        /** Ids of the chats whose sessions are folded away in the list. */
        this.collapsed = new Set();
        /** The poll timer while any session is working, else null. */
        this.timer = null;
        /** How often the list is read while a session is working, in milliseconds. */
        this.pollMs = 2500;
        /** The status each chat had at the last read, to know when the open chat's session changed. */
        this.lastStatus = new Map();
    }

    /** Whether a chat in the list is a session. */
    isSession(thread) {
        return !!(thread && thread.parentThreadId);
    }

    /** Whether the status means a turn is, or is about to be, in progress. */
    isActive(status) {
        return status == 'queued' || status == 'running' || status == 'awaiting_approval';
    }

    /** The words for a status. */
    label(status) {
        let labels = {
            queued: 'Queued', running: 'Running', awaiting_approval: 'Needs approval', idle: 'Idle',
            done: 'Done', failed: 'Failed', interrupted: 'Interrupted', archived: 'Archived',
        };
        return translate(labels[status] || status || '');
    }

    /** The dot for a status: a spinner while working, otherwise a small round badge in the colour that says how it ended. */
    dotHtml(status) {
        let title = llmaEscapeHtml(this.label(status));
        if (status == 'running') {
            return `<span class="llma-session-dot" data-status="${status}" title="${title}"><span class="spinner-border spinner-border-sm text-secondary" role="status" aria-label="${title}"></span></span>`;
        }
        let colors = { awaiting_approval: 'warning', done: 'success', failed: 'danger', interrupted: 'danger' };
        return `<span class="llma-session-dot" data-status="${llmaEscapeHtml(status || '')}" title="${title}"><span class="badge rounded-pill text-bg-${colors[status] || 'secondary'} p-1"><span class="visually-hidden">${title}</span></span></span>`;
    }

    /** Splits the chats for the list: the ones shown at the top level, and each chat's sessions (oldest first). A session whose parent is not in the list is shown on its own. */
    split(threads) {
        let live = threads.filter(t => t.sessionStatus != 'archived');
        let ids = new Set(live.map(t => t.id));
        let kids = new Map();
        let roots = [];
        for (let thread of live) {
            if (this.isSession(thread) && ids.has(thread.parentThreadId)) {
                if (!kids.has(thread.parentThreadId)) {
                    kids.set(thread.parentThreadId, []);
                }
                kids.get(thread.parentThreadId).push(thread);
            }
            else {
                roots.push(thread);
            }
        }
        for (let list of kids.values()) {
            list.sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt));
        }
        return { roots, kids };
    }

    /** All the sessions below a chat, at any depth, for its count. */
    descendants(thread, kids) {
        let found = [];
        for (let kid of kids.get(thread.id) || []) {
            found.push(kid, ...this.descendants(kid, kids));
        }
        return found;
    }

    /** The fold toggle and count a chat with sessions shows in its row, with a dot when one needs the user. */
    parentAddonsHtml(thread, kids) {
        let below = this.descendants(thread, kids);
        if (below.length == 0) {
            return '';
        }
        let open = !this.collapsed.has(thread.id);
        let waiting = below.some(t => t.sessionStatus == 'awaiting_approval');
        let working = below.some(t => this.isActive(t.sessionStatus));
        let title = llmaEscapeHtml(open ? translate('Hide sessions') : translate('Show sessions'));
        return `<button class="llma-thread-branch-toggle llma-session-toggle${open ? ' expanded' : ''}" data-id="${llmaEscapeHtml(thread.id)}" title="${title}" aria-label="${title}" aria-expanded="${open}">&#9656;</button>`
            + `<span class="badge ${waiting ? 'text-bg-warning' : 'text-bg-secondary'} llma-session-count" title="${llmaEscapeHtml(translate('Sessions'))}">${below.length}${working && !waiting ? '<span class="spinner-border spinner-border-sm ms-1" aria-hidden="true"></span>' : ''}</span>`;
    }

    /** Wires the fold toggles in a freshly drawn list. */
    bind(list) {
        for (let button of list.querySelectorAll('.llma-session-toggle')) {
            button.addEventListener('click', (e) => {
                e.stopPropagation();
                if (this.collapsed.has(button.dataset.id)) {
                    this.collapsed.delete(button.dataset.id);
                }
                else {
                    this.collapsed.add(button.dataset.id);
                }
                llmaRenderThreadList(LLMAState.threads);
            });
        }
    }

    /** Called when the chat list was read: keeps polling while a session is working, and notices when the open chat's session changed. */
    onThreadsLoaded(threads) {
        let working = threads.some(t => this.isActive(t.sessionStatus));
        if (working && !this.timer) {
            this.timer = setInterval(() => this.poll(), this.pollMs);
        }
        else if (!working && this.timer) {
            clearInterval(this.timer);
            this.timer = null;
        }
        let changed = false;
        for (let thread of threads) {
            if (thread.id == LLMAState.activeThreadId && this.isSession(thread) && this.lastStatus.has(thread.id) && this.lastStatus.get(thread.id) != thread.sessionStatus) {
                changed = true;
            }
            this.lastStatus.set(thread.id, thread.sessionStatus);
        }
        this.refreshCards();
        if (changed && !LLMAState.isGenerating) {
            // The open session's turn started, ended or reached a call that needs the user: show the chat as it is now.
            llmaReloadActiveThread();
        }
    }

    /** Reads the list again. */
    async poll() {
        // Redrawing the list would throw away a name being typed into it.
        if (document.hidden || LLMAState.isGenerating || document.querySelector('.llma-thread-rename-input')) {
            return;
        }
        await llmaLoadThreads();
    }

    /** Draws (or removes) the banner on top of the open chat: what it is, how it is doing, Stop and Archive, and the call waiting for an answer. */
    onThreadLoaded(thread) {
        document.getElementById('llma-session-banner')?.remove();
        if (!this.isSession(thread)) {
            return;
        }
        this.lastStatus.set(thread.id, thread.sessionStatus);
        let status = thread.sessionStatus || 'idle';
        let parent = LLMAState.threads.find(t => t.id == thread.parentThreadId);
        let banner = document.createElement('div');
        banner.id = 'llma-session-banner';
        banner.className = 'alert alert-secondary d-flex flex-wrap align-items-center gap-2 mb-0 rounded-0 py-2';
        let state = document.createElement('span');
        state.innerHTML = `${this.dotHtml(status)} <strong>${llmaEscapeHtml(this.label(status))}</strong>`;
        banner.appendChild(state);
        let from = document.createElement('span');
        from.className = 'flex-grow-1';
        from.append(translate('Started by') + ' ');
        if (parent) {
            let link = document.createElement('a');
            link.href = '#';
            link.textContent = parent.title || translate('Untitled');
            link.addEventListener('click', (e) => {
                e.preventDefault();
                llmaSwitchThread(parent.id);
            });
            from.appendChild(link);
        }
        else {
            from.append(translate('another chat'));
        }
        banner.appendChild(from);
        if (this.isActive(status)) {
            banner.appendChild(this.button(translate('Stop'), () => this.stop(thread.id)));
        }
        banner.appendChild(this.button(translate('Archive'), () => this.archive(thread.id)));
        if (status == 'awaiting_approval' && thread.pendingApproval) {
            banner.appendChild(this.approvalRow(thread.pendingApproval));
        }
        else if (this.isActive(status)) {
            let note = document.createElement('div');
            note.className = 'w-100 small text-secondary';
            note.textContent = translate('Working in the background. Its reply appears here when the turn ends.');
            banner.appendChild(note);
        }
        let messages = document.getElementById('llma-messages');
        messages?.parentElement?.insertBefore(banner, messages);
    }

    /** A basic button with a handler. */
    button(text, onClick, primary = false) {
        let button = document.createElement('button');
        button.type = 'button';
        button.className = `basic-button${primary ? ' btn-primary' : ''}`;
        button.textContent = text;
        button.addEventListener('click', onClick);
        return button;
    }

    /** The call the session is waiting on, with the same three answers as a call in the open chat. */
    approvalRow(pending) {
        let row = document.createElement('div');
        row.className = 'llma-session-approval w-100 d-flex flex-wrap align-items-center gap-2';
        let question = document.createElement('span');
        question.textContent = `${translate('Allow')} ${String(pending.name || '').replace(/_/g, ' ')}?`;
        row.appendChild(question);
        let detail = document.createElement('code');
        detail.className = 'text-break';
        let args = JSON.stringify(pending.arguments || {});
        detail.textContent = args.length > 160 ? args.slice(0, 160) + '…' : args;
        row.appendChild(detail);
        for (let choice of [{ decision: 'allow', label: 'Allow once', primary: true }, { decision: 'allow_chat', label: 'Allow for this chat', primary: false }, { decision: 'deny', label: 'Deny', primary: false }]) {
            let button = this.button(translate(choice.label), () => this.answer(row, pending.approvalId, choice.decision), choice.primary);
            button.dataset.decision = choice.decision;
            row.appendChild(button);
        }
        return row;
    }

    /** Sends the user's answer, then shows the session as it is now. */
    async answer(row, approvalId, decision) {
        for (let button of row.querySelectorAll('button')) {
            button.disabled = true;
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantResolveToolApproval', { approvalId: approvalId, decision: decision }), translate('Could not send your answer'));
        row.textContent = reply && reply.resolved ? (decision == 'deny' ? translate('Declined.') : translate('Allowed.')) : translate('This request has already ended.');
        await llmaLoadThreads();
    }

    /** Stops the session's turn. */
    async stop(threadId) {
        await llmaUserAction(() => llmaRequest('LLMAssistantStopGeneration', { threadId: threadId }), translate('Could not stop the session'));
        await llmaLoadThreads();
        if (threadId == LLMAState.activeThreadId) {
            await llmaReloadActiveThread();
        }
    }

    /** Puts the session away: it leaves the list. */
    async archive(threadId) {
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantArchiveSession', { threadId: threadId }), translate('Could not archive the session'));
        if (!reply) {
            return;
        }
        await llmaLoadThreads();
        if (threadId == LLMAState.activeThreadId) {
            llmaShowWelcome();
            llmaRenderThreadList(LLMAState.threads);
        }
    }

    /** A compact card for a session tool's result in the chat that started it: the session, how it is doing now, and a button to open it. */
    renderResultCard(wrap, name, result) {
        let sessions = Array.isArray(result.sessions) ? result.sessions : (result.id ? [result] : []);
        for (let session of sessions) {
            if (!session.id) {
                continue;
            }
            let row = document.createElement('div');
            row.className = 'llma-session-card d-flex align-items-center gap-2 mt-1';
            row.dataset.sessionId = session.id;
            row.dataset.initialStatus = session.status || '';
            let dot = document.createElement('span');
            dot.className = 'llma-session-card-dot';
            row.appendChild(dot);
            let title = document.createElement('span');
            title.className = 'flex-grow-1 text-break';
            title.textContent = session.title || session.id;
            row.appendChild(title);
            let badge = document.createElement('span');
            badge.className = 'badge text-bg-secondary llma-session-card-status';
            row.appendChild(badge);
            row.appendChild(this.button(translate('Open'), () => llmaSwitchThread(session.id)));
            wrap.appendChild(row);
        }
        // A session just started is not in the list yet: read it again so its row appears (and polling starts).
        if (sessions.some(s => s.id && !LLMAState.threads.some(t => t.id == s.id))) {
            llmaLoadThreads();
        }
        this.refreshCards();
    }

    /** Brings every session card in the open chat up to date with the list. */
    refreshCards() {
        for (let card of document.querySelectorAll('.llma-session-card')) {
            let thread = LLMAState.threads.find(t => t.id == card.dataset.sessionId);
            let status = thread ? (thread.sessionStatus || 'idle') : (card.dataset.initialStatus || 'idle');
            if (thread && thread.sessionStatus == 'archived') {
                status = 'archived';
            }
            card.querySelector('.llma-session-card-dot').innerHTML = this.dotHtml(status);
            card.querySelector('.llma-session-card-status').textContent = this.label(status);
            let open = card.querySelector('button');
            if (open) {
                open.disabled = !thread || status == 'archived';
            }
        }
    }
}

let llmaSessions = new LLMASessions();

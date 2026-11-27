/**
 * Chat library: searching what was said in the chats (not only their titles), pinning chats to the top of the list, importing a chat from a file, and exporting one.
 * See the README, "Chat library". Everything here calls a route (LLMAssistantSearchThreads, LLMAssistantPinThread, LLMAssistantImportThread, LLMAssistantExportThread), so the same
 * can be done over the API.
 *
 * Search: every keystroke filters the list by title at once; once the query is long enough a (debounced) request asks the server to look through the messages too. A reply for a query
 * that is no longer the one in the box is dropped, so a slow answer can never replace a newer one. The list itself is drawn by llmaRenderThreadList (threads.js), which asks this
 * class whether a search is on and, if so, which chats and snippets to show, so every other place that redraws the list keeps the results instead of wiping them.
 *
 * Built from the list's own rows, core's basic-button and the extension's modal (llma-overlay / llma-modal), so it adds only the snippet, the pin and the result notes (llma-layout.css).
 */
class LLMALibrary {

    constructor() {
        /** What is in the search box, trimmed. */
        this.query = '';
        /** Counts up with every change of the query, so a reply can tell whether it still answers the question. */
        this.seq = 0;
        /** The server's results for this.query, best first: [{ threadId, title, snippets }]; null until they arrive (or when the search failed). */
        this.results = null;
        /** How many chats matched in all, and whether there are more than are listed. */
        this.total = 0;
        this.hasMore = false;
        /** A request for this.query is out. */
        this.loading = false;
        /** The last request failed: only the title matches are shown, with a note. */
        this.failed = false;
        /** The server searched some chats by title only (the user has a very large history). */
        this.partial = false;
        /** The smallest query the server is asked about (the server refuses anything shorter). */
        this.minChars = 2;
        /** How long to wait after the last keystroke before asking the server, in milliseconds. */
        this.debounceMs = 250;
        /** How many chats to ask for at a time. */
        this.pageSize = 20;
        /** Per chat, the pin requests in flight ({ tail, pending }), chained so they reach the server in the order they were clicked. */
        this.pinQueue = new Map();
        /** The file being imported, as text, while its preview is open. */
        this.importText = null;
        this.runLater = llmaDebounce(() => this.run(), this.debounceMs);
    }

    // ---- Search ----

    /** Whether the list is showing search results (the box holds something). */
    get searching() {
        return this.query.length > 0;
    }

    /** Called with the box's text on every keystroke: the title filter shows at once, the server search follows after a pause. */
    onInput(value) {
        let query = (value || '').trim();
        if (query == this.query) {
            return;
        }
        this.query = query;
        this.seq++;
        this.results = null;
        this.total = 0;
        this.hasMore = false;
        this.failed = false;
        this.partial = false;
        this.loading = query.length >= this.minChars;
        llmaRenderThreadList(LLMAState.threads);
        if (this.loading) {
            this.runLater();
        }
    }

    /** The chat list changed (a chat was renamed, deleted, imported or answered in): asks the server again, keeping the results on screen until the new ones arrive. */
    refresh() {
        if (!this.searching || this.query.length < this.minChars) {
            return;
        }
        this.seq++;
        this.failed = false;
        this.loading = true;
        this.runLater();
    }

    /** Clears the box and the results. */
    clear() {
        let box = document.getElementById('llma-thread-search');
        if (box) {
            box.value = '';
        }
        this.onInput('');
    }

    /** Asks the server for the first page of results for the current query. */
    async run() {
        if (this.query.length < this.minChars) {
            return;
        }
        let seq = this.seq;
        let query = this.query;
        await this.fetchPage(query, seq, 0);
    }

    /** Asks for the next page and adds it to the list. */
    async more() {
        if (!this.hasMore || this.loading || !this.results) {
            return;
        }
        this.loading = true;
        llmaRenderThreadList(LLMAState.threads);
        await this.fetchPage(this.query, this.seq, this.results.length);
    }

    /** One request. A reply is used only if the query has not changed since (the sequence number is still the one it was sent with). */
    async fetchPage(query, seq, offset) {
        let reply = null;
        let failed = false;
        try {
            reply = await llmaRequest('LLMAssistantSearchThreads', { query, limit: this.pageSize, offset });
            failed = !reply || reply.success == false;
        }
        catch (e) {
            failed = true;
        }
        if (seq != this.seq || query != this.query) {
            return;
        }
        this.loading = false;
        if (failed) {
            this.failed = true;
            this.results = offset == 0 ? null : this.results;
        }
        else {
            this.failed = false;
            let results = Array.isArray(reply.results) ? reply.results : [];
            this.results = offset == 0 ? results : (this.results || []).concat(results);
            this.total = reply.total || 0;
            this.hasMore = !!reply.hasMore;
            this.partial = !!reply.partial;
        }
        llmaRenderThreadList(LLMAState.threads);
    }

    /** The chats to list while searching, best first, as sidebar rows: the server's results once they are in, until then the chats whose title has the query. */
    visibleThreads(threads) {
        // Sessions that were archived are not listed, so they are not found either.
        let shown = threads.filter(t => t.sessionStatus != 'archived');
        if (this.results) {
            let byId = new Map(shown.map(t => [t.id, t]));
            return this.results.map(r => byId.get(r.threadId)).filter(Boolean);
        }
        let lower = this.query.toLowerCase();
        return shown.filter(t => (t.title || '').toLowerCase().includes(lower))
            .sort((a, b) => new Date(b.updatedAt || b.createdAt) - new Date(a.updatedAt || a.createdAt));
    }

    /** The snippets under a chat's row (the part of its messages that matched), or ''. */
    snippetsHtml(thread) {
        if (!this.results) {
            return '';
        }
        let result = this.results.find(r => r.threadId == thread.id);
        if (!result || !Array.isArray(result.snippets) || result.snippets.length == 0) {
            return '';
        }
        let rows = result.snippets.map(s => `
            <button type="button" class="llma-thread-snippet" data-thread-id="${llmaEscapeHtml(thread.id)}" data-msg-id="${llmaEscapeHtml(s.messageId || '')}">
                <span class="llma-thread-snippet-who">${llmaEscapeHtml(s.role == 'user' ? translate('You') : translate('Reply'))}</span>
                <span class="llma-thread-snippet-text">${this.highlight(s.text, s.ranges)}</span>
            </button>`).join('');
        return `<div class="llma-thread-snippets">${rows}</div>`;
    }

    /** Snippet text with the matches in <mark>: the text is escaped piece by piece, so nothing in a message can add markup. */
    highlight(text, ranges) {
        let out = '';
        let at = 0;
        for (let range of ranges || []) {
            let start = range[0];
            let end = start + range[1];
            if (start < at || end > text.length) {
                continue;
            }
            out += llmaEscapeHtml(text.slice(at, start)) + `<mark class="llma-search-hit">${llmaEscapeHtml(text.slice(start, end))}</mark>`;
            at = end;
        }
        return out + llmaEscapeHtml(text.slice(at));
    }

    /** The note above the results: what is happening or what was found. */
    statusHtml(listed) {
        let text;
        if (this.query.length < this.minChars) {
            text = translate('Type at least 2 characters to search messages.');
        }
        else if (this.failed) {
            text = translate('Message search is unavailable. Showing title matches.');
        }
        else if (this.loading && !this.results) {
            text = translate('Searching messages…');
        }
        else if (listed == 0) {
            text = translate('No chats match.');
        }
        else {
            // What is counted is what is listed, so the note never disagrees with the rows (a result whose chat is not in the list is not shown); with more to load it says of how many.
            text = this.hasMore && this.total > listed
                ? `${listed} ${translate('of')} ${this.total} ${translate('chats')}`
                : `${listed} ${listed == 1 ? translate('chat') : translate('chats')}`;
            if (this.partial) {
                text += ' · ' + translate('some very large chats were searched by title only');
            }
        }
        return `<div class="llma-search-status" role="status">${llmaEscapeHtml(text)}</div>`;
    }

    /** The button under the results that asks for the next page. */
    moreHtml() {
        if (!this.hasMore) {
            return '';
        }
        return `<div class="llma-search-more-wrap"><button type="button" class="basic-button llma-search-more"${this.loading ? ' disabled' : ''}>${llmaEscapeHtml(translate('Show more'))}</button></div>`;
    }

    /** Wires the snippets and the "Show more" button in the list that was just drawn. */
    bind(list) {
        for (let btn of list.querySelectorAll('.llma-thread-snippet')) {
            btn.addEventListener('click', () => this.openSnippet(btn.dataset.threadId, btn.dataset.msgId));
        }
        list.querySelector('.llma-search-more')?.addEventListener('click', () => this.more());
    }

    /** Opens the chat a snippet came from and shows the message: on another branch, that branch is made the one showing. */
    async openSnippet(threadId, messageId) {
        if (LLMAState.activeThreadId != threadId) {
            await llmaSwitchThread(threadId);
        }
        if (LLMAState.activeThreadId != threadId || !messageId) {
            return;
        }
        let onPath = LLMAState.messages.some(m => m.id == messageId);
        if (!onPath && (LLMAState.allNodes || []).some(n => n.id == messageId)) {
            await llmaSetActiveLeaf(llmaDescendToLeaf(messageId));
        }
        let row = document.querySelector(`#llma-messages [data-msg-id="${CSS.escape(messageId)}"]`);
        if (row) {
            row.scrollIntoView({ block: 'center' });
            row.classList.add('llma-msg-found');
            setTimeout(() => row.classList.remove('llma-msg-found'), 2200);
        }
    }

    // ---- Pin ----

    /** Pins or unpins a chat. The list changes at once; the request goes after any earlier pin request for the same chat, so quick clicks reach the server in order. */
    async togglePin(threadId) {
        let thread = LLMAState.threads.find(t => t.id == threadId);
        if (!thread) {
            return;
        }
        let pinned = !thread.pinned;
        this.setPinned(thread, pinned);
        llmaRenderThreadList(LLMAState.threads);
        let queue = this.pinQueue.get(threadId);
        if (!queue) {
            queue = { tail: Promise.resolve(), pending: 0 };
            this.pinQueue.set(threadId, queue);
        }
        queue.pending++;
        queue.tail = queue.tail.then(() => this.sendPin(threadId, pinned, queue));
        await queue.tail;
    }

    /** One pin request. A request the server refuses puts the list back as the server has it (the list is read again), never as it was before the click, since a click after it may
     * already have changed it. When it is the last one queued for the chat, the row shows what the server now has; the chat is looked up again by id, as the list may have been read anew meanwhile. */
    async sendPin(threadId, pinned, queue) {
        let error = null;
        try {
            let reply = await llmaRequest('LLMAssistantPinThread', { threadId, pinned });
            if (reply?.success == false) {
                error = reply.error || 'Could not pin the chat';
            }
        }
        catch (e) {
            error = llmaShortError(e) || 'Could not pin the chat';
        }
        queue.pending--;
        if (queue.pending == 0 && this.pinQueue.get(threadId) == queue) {
            this.pinQueue.delete(threadId);
        }
        // Whatever happens drawing the list, the queue carries on: a request waiting behind this one must still be sent.
        try {
            if (error) {
                llmaShowToast(error, 'error');
                await llmaLoadThreads();
                return;
            }
            let thread = LLMAState.threads.find(t => t.id == threadId);
            if (queue.pending == 0 && thread && !!thread.pinned != pinned) {
                this.setPinned(thread, pinned);
                llmaRenderThreadList(LLMAState.threads);
            }
        }
        catch (e) {
            console.error('[LLMAssistant] Could not redraw the chat list after a pin:', e);
        }
    }

    /** Sets or clears the pin flag on a row of the list. */
    setPinned(thread, pinned) {
        if (pinned) {
            thread.pinned = true;
        }
        else {
            delete thread.pinned;
        }
    }

    /** Whether a chat's row offers a pin: a session is listed under the chat that started it, so it cannot be pinned on its own. */
    canPin(thread) {
        return !llmaSessions.isSession(thread);
    }

    // ---- Export ----

    /** Downloads the open chat through the export route. `format` is 'json' (every branch, can be imported again) or 'markdown'. */
    async exportActive(format) {
        let threadId = LLMAState.activeThreadId;
        if (!threadId) {
            llmaShowToast('No messages to export', 'info');
            return;
        }
        let reply;
        try {
            reply = await llmaRequest('LLMAssistantExportThread', { threadId, format });
        }
        catch (e) {
            llmaShowToast(llmaShortError(e) || 'Export failed', 'error');
            return;
        }
        if (!reply?.success) {
            llmaShowToast(reply?.error || 'Export failed', 'error');
            return;
        }
        llmaDownloadFile(reply.filename || (format == 'markdown' ? 'chat.md' : 'chat.json'), reply.content, format == 'markdown' ? 'text/markdown' : 'application/json');
        llmaShowToast(`${translate('Exported as')} ${format == 'markdown' ? 'MARKDOWN' : 'JSON'}`, 'success');
    }

    // ---- Import ----

    /** Opens the file picker. */
    chooseFile() {
        let input = document.getElementById('llma-import-file');
        if (input) {
            input.value = '';
            input.click();
        }
    }

    /** Reads the chosen file and shows what would be imported. */
    async onFileChosen(file) {
        if (!file) {
            return;
        }
        if (file.size > 4 * 1024 * 1024) {
            llmaShowToast('That file is too large to be a chat export.', 'error');
            return;
        }
        let text;
        try {
            text = await file.text();
        }
        catch (e) {
            llmaShowToast('Could not read the file', 'error');
            return;
        }
        let reply;
        try {
            reply = await llmaRequest('LLMAssistantImportThread', { content: text, preview: true });
        }
        catch (e) {
            // A refusal comes back as an error: its text says what is wrong with the file.
            llmaShowToast(llmaShortError(e) || 'Could not read the file', 'error');
            return;
        }
        if (!reply?.success) {
            llmaShowToast(reply?.error || 'Could not import the file', 'error');
            return;
        }
        this.importText = text;
        this.showPreview(reply.summary, file.name);
    }

    /** The preview dialog: the title, how many messages, what will be left out, and the first few messages. */
    showPreview(summary, fileName) {
        let overlay = document.getElementById('llma-import-overlay');
        let body = document.getElementById('llma-import-body');
        if (!overlay || !body) {
            return;
        }
        let assistant = LLMAState.assistants.find(a => a.id == summary.assistantId);
        let warnings = (summary.warnings || []).map(w => `<li>${llmaEscapeHtml(w)}</li>`).join('');
        let sample = (summary.sample || []).map(m => `
            <div class="llma-import-sample-row"><span class="llma-import-sample-who">${llmaEscapeHtml(m.role == 'user' ? translate('You') : translate('Reply'))}</span> <span>${llmaEscapeHtml(m.text)}</span></div>`).join('');
        let count = summary.messageCount;
        body.innerHTML = `
            <div class="llma-import-title" id="llma-import-title">${llmaEscapeHtml(summary.title || translate('Untitled'))}</div>
            <div class="llma-import-facts">
                ${count} ${count == 1 ? translate('message') : translate('messages')}${summary.hasBranches ? ' · ' + llmaEscapeHtml(translate('has branches')) : ''}
                ${assistant ? ' · ' + llmaEscapeHtml(assistant.name || '') : ''}
                <span class="llma-import-file">${llmaEscapeHtml(fileName || '')}</span>
            </div>
            ${warnings ? `<ul class="llma-import-warnings">${warnings}</ul>` : ''}
            <div class="llma-import-sample">${sample}</div>
            <div class="llma-settings-note">${llmaEscapeHtml(translate('It is added as a new chat. Nothing in your other chats changes.'))}</div>`;
        document.getElementById('llma-import-status').textContent = '';
        document.getElementById('llma-import-confirm').disabled = false;
        overlay.style.display = '';
        document.getElementById('llma-import-confirm').focus();
    }

    /** Closes the import dialog and forgets the file it showed. */
    closePreview() {
        this.importText = null;
        let overlay = document.getElementById('llma-import-overlay');
        if (overlay) {
            overlay.style.display = 'none';
        }
    }

    /** Imports what the preview showed, then opens the new chat. */
    async confirmImport() {
        if (!this.importText) {
            return;
        }
        let confirm = document.getElementById('llma-import-confirm');
        let status = document.getElementById('llma-import-status');
        confirm.disabled = true;
        status.textContent = translate('Importing…');
        let reply;
        try {
            reply = await llmaRequest('LLMAssistantImportThread', { content: this.importText });
        }
        catch (e) {
            reply = { success: false, error: llmaShortError(e) || 'Could not import the file' };
        }
        if (!reply?.success) {
            status.textContent = reply?.error || translate('Could not import the file');
            confirm.disabled = false;
            return;
        }
        this.closePreview();
        if (reply.thread) {
            LLMAState.threads = LLMAState.threads.filter(t => t.id != reply.thread.id);
            LLMAState.threads.unshift(reply.thread);
        }
        else {
            await llmaLoadThreads();
        }
        llmaRenderThreadList(LLMAState.threads);
        this.refresh();
        llmaShowToast('Chat imported', 'success');
        await llmaSwitchThread(reply.threadId);
    }

    /** One-time wiring of the sidebar's import button, the file input and the dialog. */
    setup() {
        let wire = (id, event, handler) => {
            let el = document.getElementById(id);
            // One flag per element and event: the search box has two listeners.
            if (el && !el.dataset['llmaBound' + event]) {
                el.dataset['llmaBound' + event] = '1';
                el.addEventListener(event, handler);
            }
        };
        wire('llma-thread-import-btn', 'click', () => this.chooseFile());
        wire('llma-import-file', 'change', (e) => this.onFileChosen(e.target.files?.[0]));
        wire('llma-import-close', 'click', () => this.closePreview());
        wire('llma-import-cancel', 'click', () => this.closePreview());
        wire('llma-import-confirm', 'click', () => this.confirmImport());
        wire('llma-import-overlay', 'click', (e) => {
            if (e.target.id == 'llma-import-overlay') {
                this.closePreview();
            }
        });
        wire('llma-import-overlay', 'keydown', (e) => {
            if (e.key == 'Escape') {
                this.closePreview();
            }
        });
        wire('llma-thread-search', 'input', (e) => this.onInput(e.target.value));
        wire('llma-thread-search', 'keydown', (e) => {
            if (e.key == 'Escape' && this.searching) {
                e.preventDefault();
                this.clear();
            }
        });
    }
}

let llmaLibrary = new LLMALibrary();

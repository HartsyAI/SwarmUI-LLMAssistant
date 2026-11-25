/**
 * Code mode: the Chat / Code switch, the workspace picker in the composer, and the Workspace panel on the right (the workspace's files, what the
 * assistant changed with a way to undo it, a live terminal for its commands, and the chat's task list), plus the "plan ready" card and the Chat / Code
 * filter of the chat list.
 *
 * A chat in code mode works in a workspace folder and brings the code tools with it (see the README, "Code tools and workspaces"). The panel is built
 * from Bootstrap's nav-tabs, list-group and badge, so it carries almost no styling of its own (llma-code.css only lays out the two areas).
 */
class LLMACodeMode {

    constructor() {
        /** The mode of the chat showing (or, with no chat yet, of the next one): 'chat' or 'code'. */
        this.mode = 'chat';
        /** The mode the chat showing has saved. It can be 'code' before the permission is known (a chat restored at start-up), so it is kept apart from <c>mode</c>. */
        this.savedMode = 'chat';
        /** Whether the user holds llm_code_mode. */
        this.canUse = false;
        /** The workspaces the user may use: {id, label, userOwned}. */
        this.workspaces = [];
        /** The workspace id saved on the chat showing, or '' for the user's default. */
        this.workspaceId = '';
        /** The workspace to give the next chat, picked before it existed. */
        this.pendingWorkspace = '';
        /** The chat's task list: [{content, status}]. */
        this.todos = [];
        /** The panel tab showing: 'files', 'changes', 'terminal' or 'tasks'. */
        this.tab = 'files';
        /** The files the assistant's file tools changed, as the server lists them: {path, status, added, removed, binary, large, revertable, conflict}. */
        this.changes = [];
        /** Paths of the changes whose diff is open, and the diffs loaded for them (by path). */
        this.openDiffs = new Set();
        this.diffs = new Map();
        /** What commands printed while they ran, by tool call id: {lines: [{stream, text}]}. A reloaded chat has only each command's saved result. */
        this.live = new Map();
        /** Folder listings by path ('' is the top), as the server sent them. */
        this.listings = new Map();
        /** Paths of the folders opened in the tree. */
        this.openFolders = new Set();
        /** The file in the viewer: {path, startLine, endLine, totalLines, content, truncated}, or null. */
        this.viewing = null;
        /** Whether the model changed something this turn, so the tree needs reloading when it ends. */
        this.dirty = false;
        /** Which chats the sidebar lists: 'all', 'chat' or 'code'. */
        this.filter = 'all';
    }

    /** Wires the controls and finds out what the user may do. Called once when the tab is set up. */
    async setup() {
        for (let radio of document.querySelectorAll('input[name="llma-mode"]')) {
            radio.addEventListener('change', () => {
                if (radio.checked) {
                    this.setMode(radio.id == 'llma-mode-code' ? 'code' : 'chat', true);
                }
            });
        }
        for (let radio of document.querySelectorAll('input[name="llma-mode-filter"]')) {
            radio.addEventListener('change', () => {
                if (radio.checked) {
                    this.filter = radio.dataset.filter;
                    llmaRenderThreadList(LLMAState.threads);
                }
            });
        }
        document.getElementById('llma-workspace-select')?.addEventListener('change', event => this.onPickWorkspace(event.target.value));
        document.getElementById('llma-plan-accept')?.addEventListener('click', () => this.acceptPlan());
        document.getElementById('llma-plan-keep')?.addEventListener('click', () => this.hidePlanCard());
        await this.loadWorkspaces();
        // A chat restored while the permission was still unknown: now that it is, a code chat is a code chat.
        if (this.canUse && this.savedMode == 'code' && LLMAState.activeThreadId) {
            this.mode = 'code';
            this.refreshChanges();
        }
        this.apply();
    }

    /** Asks the server which workspaces the user may use, and whether code mode is open to them at all. */
    async loadWorkspaces() {
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantGetWorkspaces', {});
        }
        catch (ex) {
            reply = null;
        }
        this.canUse = !!reply?.canUseCode;
        this.workspaces = reply?.workspaces || [];
    }

    /** True when the chat showing is in code mode. */
    get isCode() {
        return this.mode == 'code';
    }

    /** Switches mode. With a chat open the choice is saved on it; with none it applies to the next chat. */
    async setMode(mode, persist) {
        if (mode == 'code' && !this.canUse) {
            llmaShowToast(translate('Code mode needs the LLM Code Mode permission.'), 'error');
            this.apply();
            return;
        }
        this.mode = mode;
        if (mode == 'code' && LLMAState.approvalMode == 'auto') {
            // A coding session starts careful: changes and commands ask first. The user can pick Auto or Plan.
            llmaSetApprovalMode('ask', persist);
        }
        this.apply();
        if (mode == 'code' && persist) {
            this.openPanel();
        }
        if (mode == 'code' && LLMAState.activeThreadId) {
            this.refreshChanges();
        }
        if (persist && LLMAState.activeThreadId) {
            let reply = await llmaUserAction(() => llmaRequest('LLMAssistantSetThreadMode', { threadId: LLMAState.activeThreadId, mode: mode }), translate('Could not change the mode of this chat'));
            let meta = LLMAState.threads.find(t => t.id == LLMAState.activeThreadId);
            if (reply && meta) {
                meta.mode = mode;
                llmaRenderThreadList(LLMAState.threads);
            }
        }
    }

    /** Opens the right panel when it is collapsed (as the split bar's button does, so the choice sticks): choosing Code mode is asking to see the workspace. */
    openPanel() {
        let container = document.getElementById('llma-container');
        if (!container || !container.classList.contains('panel-collapsed') || window.innerWidth <= 1050) {
            return;
        }
        container.classList.remove('panel-collapsed');
        try {
            localStorage.setItem('llma_panel_collapsed', 'false');
        }
        catch (ex) {
            // Storage can be blocked; the panel is open for this visit regardless.
        }
        let button = document.getElementById('llma-split-right-btn');
        if (button) {
            button.innerHTML = '&#x21DB;';
        }
    }

    /** Called when a chat is loaded or created: its mode, workspace and task list become what shows. */
    onThreadLoaded(thread) {
        this.savedMode = thread?.mode == 'code' ? 'code' : 'chat';
        this.mode = this.savedMode == 'code' && this.canUse ? 'code' : 'chat';
        this.workspaceId = thread?.workspace || '';
        this.todos = Array.isArray(thread?.todos) ? thread.todos : [];
        this.listings.clear();
        this.openFolders.clear();
        this.viewing = null;
        this.changes = [];
        this.openDiffs.clear();
        this.diffs.clear();
        this.live.clear();
        this.hidePlanCard();
        this.apply();
        if (this.isCode) {
            this.refreshChanges(thread?.id);
        }
    }

    /** Called right after a new chat was created: the workspace picked before it existed is saved on it. */
    async onThreadCreated(thread) {
        this.onThreadLoaded(thread);
        if (this.isCode && this.pendingWorkspace) {
            let id = this.pendingWorkspace;
            this.pendingWorkspace = '';
            await this.setWorkspace(id);
        }
    }

    /** The mode the next chat should be created with. */
    get modeForNewChat() {
        return this.isCode ? 'code' : 'chat';
    }

    /** Shows everything that depends on the mode: the switch, the picker, the panel, the sidebar filter and the page's own marker. */
    apply() {
        let code = this.isCode;
        let codeRadio = document.getElementById('llma-mode-code');
        let chatRadio = document.getElementById('llma-mode-chat');
        if (codeRadio) {
            codeRadio.disabled = !this.canUse;
            codeRadio.checked = code;
            let label = document.querySelector('label[for="llma-mode-code"]');
            if (label) {
                label.title = this.canUse ? translate('Code mode: the chat works in a workspace and can read, change and run things in it') : translate('Code mode needs the LLM Code Mode permission');
            }
        }
        if (chatRadio) {
            chatRadio.checked = !code;
        }
        document.getElementById('llma-container')?.classList.toggle('llma-code-mode', code);
        let picker = document.getElementById('llma-workspace-select');
        if (picker) {
            picker.style.display = code ? '' : 'none';
            this.fillPicker();
        }
        let filter = document.getElementById('llma-thread-mode-filter');
        if (filter) {
            filter.style.display = this.canUse ? '' : 'none';
        }
        let label = document.querySelector('.llma-panel-header-label');
        if (label) {
            label.textContent = code ? translate('Workspace') : translate('Assistant');
        }
        if (code) {
            this.renderPanel();
        }
        else if (typeof llmaRenderAssistantPanel == 'function') {
            llmaRenderAssistantPanel(LLMAState.activeAssistantId);
        }
    }

    // ---- workspace picker -------------------------------------------------------------------------------

    /** Fills the composer's workspace dropdown. */
    fillPicker() {
        let picker = document.getElementById('llma-workspace-select');
        if (!picker) {
            return;
        }
        let current = this.workspaceId || this.pendingWorkspace || 'user:default';
        let options = this.workspaces.map(w => `<option value="${llmaEscapeHtml(w.id)}">${llmaEscapeHtml(w.userOwned ? w.label : `${w.label} (${translate('shared folder')})`)}</option>`);
        options.push(`<option value="__new__">${translate('New workspace…')}</option>`);
        picker.innerHTML = options.join('');
        picker.value = this.workspaces.some(w => w.id == current) ? current : (this.workspaces[0]?.id || '');
        picker.title = translate('The folder this chat reads, searches, changes and runs commands in');
    }

    /** The dropdown changed: switch workspace, or make a new one. */
    async onPickWorkspace(value) {
        if (value == '__new__') {
            let name = (prompt(translate('Name the new workspace (lowercase letters, digits, - and _)'), '') || '').trim();
            if (name == '') {
                this.fillPicker();
                return;
            }
            let reply = await llmaUserAction(() => llmaRequest('LLMAssistantCreateWorkspace', { name: name }), translate('Could not make the workspace'));
            if (!reply) {
                this.fillPicker();
                return;
            }
            await this.loadWorkspaces();
            value = reply.id;
        }
        await this.setWorkspace(value);
    }

    /** Points the chat at a workspace (or remembers it for the chat that is about to be made). */
    async setWorkspace(id) {
        if (!LLMAState.activeThreadId) {
            this.pendingWorkspace = id;
            this.fillPicker();
            return;
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantSetThreadWorkspace', { threadId: LLMAState.activeThreadId, workspaceId: id }), translate('Could not change the workspace'));
        if (reply) {
            this.workspaceId = id;
            this.listings.clear();
            this.openFolders.clear();
            this.viewing = null;
            // The changes belong to the workspace they were made in: the list starts over.
            this.changes = [];
            this.openDiffs.clear();
            this.diffs.clear();
            this.refreshChanges();
        }
        this.fillPicker();
        this.renderPanel();
    }

    // ---- the panel ----------------------------------------------------------------------------------------

    /** Draws the Workspace panel: Files, Changes, Terminal and Tasks tabs. */
    renderPanel() {
        let inner = document.getElementById('llma-panel-inner');
        if (!inner || !this.isCode) {
            return;
        }
        let open = n => (this.tab == n ? ' active' : '');
        let badge = n => (n > 0 ? ` <span class="badge text-bg-secondary">${n}</span>` : '');
        // A list of buttons rather than a tab list: the reload button sits in the same row, which a tab list may not hold.
        let current = n => (this.tab == n ? ' aria-current="true"' : '');
        inner.innerHTML = `<ul class="nav nav-tabs llma-code-tabs">`
            + `<li class="nav-item"><button type="button" class="nav-link${open('files')}" data-code-tab="files"${current('files')}>${translate('Files')}</button></li>`
            + `<li class="nav-item"><button type="button" class="nav-link${open('changes')}" data-code-tab="changes"${current('changes')}>${translate('Changes')}${badge(this.changes.length)}</button></li>`
            + `<li class="nav-item"><button type="button" class="nav-link${open('terminal')}" data-code-tab="terminal"${current('terminal')}>${translate('Terminal')}${badge(this.bashCalls().length)}</button></li>`
            + `<li class="nav-item"><button type="button" class="nav-link${open('tasks')}" data-code-tab="tasks"${current('tasks')}>${translate('Tasks')}${badge(this.todos.length)}</button></li>`
            + `<li class="nav-item ms-auto"><button type="button" class="btn btn-sm llma-code-refresh" title="${translate('Reload the file list')}" aria-label="${translate('Reload the file list')}">&#x21BB;</button></li>`
            + '</ul><div class="llma-code-body" id="llma-code-body"></div>';
        for (let button of inner.querySelectorAll('[data-code-tab]')) {
            button.addEventListener('click', () => {
                this.tab = button.dataset.codeTab;
                this.renderPanel();
                // The redraw replaced the button that was pressed: focus goes to its successor.
                inner.querySelector(`[data-code-tab="${this.tab}"]`)?.focus();
                if (this.tab == 'changes') {
                    // Files change outside the chat too (the user's editor, a command): opening the tab looks again.
                    this.refreshChanges();
                }
            });
        }
        inner.querySelector('.llma-code-refresh').addEventListener('click', () => this.reload());
        if (this.tab == 'tasks') {
            this.renderTasks();
        }
        else if (this.tab == 'changes') {
            this.renderChanges();
        }
        else if (this.tab == 'terminal') {
            this.renderTerminal();
        }
        else {
            this.renderFiles();
        }
    }

    /** Drops what was loaded and shows the tab again (the tree, or the list of changes). */
    async reload() {
        this.listings.clear();
        if (this.tab == 'changes') {
            await this.refreshChanges();
            return;
        }
        if (this.tab == 'files') {
            await this.renderFiles();
        }
    }

    // ---- changes ------------------------------------------------------------------------------------------

    /** Asks the server which files the assistant's file tools changed, and redraws the tab (and its badge). */
    async refreshChanges(threadId) {
        threadId = threadId || LLMAState.activeThreadId;
        if (!threadId || !this.canUse) {
            this.changes = [];
            return;
        }
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantListWorkspaceChanges', { threadId: threadId });
        }
        catch (ex) {
            reply = null;
        }
        if (threadId != LLMAState.activeThreadId) {
            return;
        }
        this.changes = reply?.success ? reply.changes : [];
        this.diffs.clear();
        if (this.isCode) {
            this.renderPanel();
        }
    }

    /** The Changes tab: every file the assistant changed, each with its diff to open and a way to put it back. */
    renderChanges() {
        let body = document.getElementById('llma-code-body');
        if (!body) {
            return;
        }
        if (this.changes.length == 0) {
            body.innerHTML = `<div class="text-muted p-2">${translate('No changes. Files the assistant writes or edits show here with what changed, and can be put back.')}<br><span class="small">${translate('Changes made by commands it runs are not tracked.')}</span></div>`;
            return;
        }
        let kinds = { created: ['text-bg-success', translate('new')], modified: ['text-bg-primary', translate('edited')], deleted: ['text-bg-danger', translate('deleted')] };
        let rows = this.changes.map(change => {
            let [style, label] = kinds[change.status] || kinds.modified;
            let counts = change.added == null ? '' : `<span class="text-success">+${change.added}</span> <span class="text-danger">&minus;${change.removed}</span>`;
            let note = change.binary ? translate('binary file') : change.large ? translate('too large to show') : '';
            let open = this.openDiffs.has(change.path);
            return `<li class="list-group-item bg-transparent llma-change" data-path="${llmaEscapeHtml(change.path)}">`
                + `<div class="d-flex align-items-center gap-2">`
                + `<button type="button" class="btn btn-sm llma-change-toggle" aria-expanded="${open}" title="${translate('Show what changed')}">${open ? '&#9662;' : '&#9656;'}</button>`
                + `<span class="badge ${style}">${label}</span>`
                + `<span class="flex-grow-1 text-truncate llma-change-path" title="${llmaEscapeHtml(change.path)}">${llmaEscapeHtml(change.path)}</span></div>`
                // The panel is narrow: the path gets a row of its own and the counts and the button sit under it.
                + `<div class="d-flex align-items-center gap-2 ps-4 pt-1">`
                + `<span class="small flex-grow-1">${counts || llmaEscapeHtml(note)}</span>`
                + `<button type="button" class="basic-button llma-change-revert"${change.revertable ? '' : ' disabled'} title="${change.revertable ? translate('Put the file back as it was before the assistant changed it') : translate('The earlier version was too large to keep')}">${translate('Revert')}</button>`
                + `</div>`
                + `${change.conflict ? `<div class="small text-warning ps-4">${translate('Changed since the assistant last wrote it.')}</div>` : ''}`
                + `<div class="llma-change-diff"></div></li>`;
        }).join('');
        body.innerHTML = `<div class="d-flex align-items-center gap-2 border-bottom px-2 py-1">`
            + `<span class="flex-grow-1 small">${this.changes.length == 1 ? translate('1 file changed') : translate('{0} files changed').replace('{0}', this.changes.length)}</span>`
            + `<button type="button" class="basic-button llma-change-revert-all">${translate('Revert all')}</button></div>`
            + `<ul class="list-group list-group-flush llma-changes-list">${rows}</ul>`;
        for (let row of body.querySelectorAll('.llma-change')) {
            let path = row.dataset.path;
            row.querySelector('.llma-change-toggle').addEventListener('click', () => this.toggleDiff(path));
            row.querySelector('.llma-change-path').addEventListener('click', () => this.toggleDiff(path));
            row.querySelector('.llma-change-revert').addEventListener('click', () => this.revert(path));
            if (this.openDiffs.has(path)) {
                this.drawDiff(row, path);
            }
        }
        body.querySelector('.llma-change-revert-all').addEventListener('click', () => this.revert(''));
    }

    /** Opens or closes one file's diff. */
    async toggleDiff(path) {
        if (this.openDiffs.has(path)) {
            this.openDiffs.delete(path);
        }
        else {
            this.openDiffs.add(path);
        }
        this.renderChanges();
    }

    /** Fills a change row with its diff, loading it the first time. */
    async drawDiff(row, path) {
        let holder = row.querySelector('.llma-change-diff');
        if (!this.diffs.has(path)) {
            let reply = await llmaUserAction(() => llmaRequest('LLMAssistantGetWorkspaceChange', { threadId: LLMAState.activeThreadId, path: path }), translate('Could not load the change'));
            if (!reply) {
                this.openDiffs.delete(path);
                return;
            }
            this.diffs.set(path, reply.change);
        }
        let change = this.diffs.get(path);
        if (!holder || !document.body.contains(holder)) {
            return;
        }
        if (change.diff) {
            holder.appendChild(llmaRenderDiff(change.diff));
            if (change.diffTruncated) {
                let note = document.createElement('div');
                note.className = 'small text-muted px-2';
                note.textContent = translate('Only the first part of the change is shown.');
                holder.appendChild(note);
            }
        }
        else {
            holder.innerHTML = `<div class="small text-muted px-2 py-1">${change.binary ? translate('This is not a text file, so there is no diff to show.') : change.large ? translate('The file is too large to show a diff.') : translate('Nothing differs in the lines.')}</div>`;
        }
    }

    /** Puts one file (or, with an empty path, every changed file) back as it was. A file changed since the assistant wrote it asks before it is overwritten. */
    async revert(path) {
        let what = path == '' ? translate('Put every changed file back as it was before the assistant changed it?') : translate('Put {0} back as it was before the assistant changed it?').replace('{0}', path);
        if (!confirm(what)) {
            return;
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantRevertWorkspaceChange', { threadId: LLMAState.activeThreadId, path: path }), translate('Could not revert'));
        if (reply && reply.failed?.length > 0) {
            // Some files were put back and some could not be: say which.
            llmaShowToast(`${translate('Could not put back')} ${reply.failed.map(f => f.path).join(', ')}: ${reply.failed[0].error}`, 'error');
        }
        if (reply && reply.conflicts?.length > 0) {
            let list = reply.conflicts.join(', ');
            if (confirm(translate('{0} changed since the assistant last wrote it. Reverting would undo that too. Revert anyway?').replace('{0}', list))) {
                await llmaUserAction(() => llmaRequest('LLMAssistantRevertWorkspaceChange', { threadId: LLMAState.activeThreadId, path: path, force: true }), translate('Could not revert'));
            }
        }
        this.listings.clear();
        this.viewing = null;
        await this.refreshChanges();
    }

    // ---- terminal -----------------------------------------------------------------------------------------

    /** The commands of this chat, in order: {id, command, workingDirectory, result}, from the saved tool calls of the messages showing. */
    bashCalls() {
        let calls = [];
        for (let message of LLMAState.messages || []) {
            for (let call of message.toolCalls || []) {
                if (call.name != 'code_bash') {
                    continue;
                }
                let args = call.arguments;
                if (typeof args == 'string') {
                    try {
                        args = JSON.parse(args);
                    }
                    catch (ex) {
                        args = {};
                    }
                }
                calls.push({ id: call.id, command: args?.command || '', result: call.result || null });
            }
        }
        return calls;
    }

    /** The Terminal tab: each command the assistant ran, with what it printed (live while it runs) and how it ended. */
    renderTerminal() {
        let body = document.getElementById('llma-code-body');
        if (!body) {
            return;
        }
        let calls = this.bashCalls();
        if (calls.length == 0) {
            body.innerHTML = `<div class="text-muted p-2">${translate('No commands yet. When the assistant runs a command, its output shows here as it happens.')}</div>`;
            return;
        }
        body.innerHTML = `<div class="llma-terminal" tabindex="0" role="log" aria-label="${translate('Terminal')}"></div>`;
        let screen = body.querySelector('.llma-terminal');
        for (let call of calls) {
            screen.appendChild(this.terminalEntry(call));
        }
        screen.scrollTop = screen.scrollHeight;
    }

    /** One command as terminal rows: the prompt line, its output, and its status. */
    terminalEntry(call) {
        let entry = document.createElement('div');
        entry.className = 'llma-terminal-entry';
        entry.dataset.id = call.id;
        let prompt = document.createElement('div');
        prompt.className = 'llma-terminal-cmd';
        prompt.textContent = `$ ${call.command}`;
        entry.appendChild(prompt);
        let result = call.result;
        // The live lines keep stdout and stderr in the order they came; a reloaded chat has only the saved result, which holds them apart.
        let lines = this.live.get(call.id)?.lines || [];
        if (lines.length == 0 && result) {
            lines = [...this.splitLines(result.stdout, 'stdout'), ...this.splitLines(result.stderr, 'stderr')];
        }
        let out = document.createElement('div');
        out.className = 'llma-terminal-out';
        for (let line of lines) {
            out.appendChild(this.terminalRow(line.stream, line.text));
        }
        entry.appendChild(out);
        let status = document.createElement('div');
        status.className = 'llma-terminal-status';
        if (!result) {
            status.textContent = translate('running…');
        }
        else if (result.timedOut) {
            status.textContent = translate('stopped: it took too long');
        }
        else if (result.exitCode != null) {
            status.textContent = `[${translate('exit')} ${result.exitCode}]`;
        }
        else {
            status.textContent = result.error || translate('did not run');
        }
        entry.appendChild(status);
        return entry;
    }

    /** One line of output as a row. */
    terminalRow(stream, text) {
        let row = document.createElement('div');
        row.className = stream == 'stderr' ? 'llma-terminal-err' : 'llma-terminal-line';
        row.textContent = text;
        return row;
    }

    /** Text split into lines for the terminal, without the empty line a final newline leaves. */
    splitLines(text, stream) {
        if (!text) {
            return [];
        }
        return text.replace(/\n$/, '').split('\n').map(line => ({ stream: stream, text: line }));
    }

    /** A tool call began: a command gets its row in the Terminal tab at once. */
    onToolCall(call) {
        if (call?.name == 'code_bash' && this.isCode) {
            if (this.tab == 'terminal') {
                this.renderTerminal();
            }
            this.updateTerminalBadge();
        }
    }

    /** Rewrites the command count on the Terminal tab without redrawing the panel (which would reset the tree's scroll and the viewer). */
    updateTerminalBadge() {
        let button = document.querySelector('.llma-code-tabs [data-code-tab="terminal"]');
        if (!button) {
            return;
        }
        let count = this.bashCalls().length;
        button.innerHTML = `${translate('Terminal')}${count > 0 ? ` <span class="badge text-bg-secondary">${count}</span>` : ''}`;
    }

    /** A running command printed something: it is kept, and shown at once when the Terminal tab is open. */
    onToolOutput(output) {
        if (!output?.id || typeof output.text != 'string') {
            return;
        }
        let record = this.live.get(output.id);
        if (!record) {
            record = { lines: [] };
            this.live.set(output.id, record);
        }
        let added = this.splitLines(output.text, output.stream);
        record.lines.push(...added);
        if (!this.isCode || this.tab != 'terminal') {
            return;
        }
        let screen = document.querySelector('#llma-code-body .llma-terminal');
        let entry = screen?.querySelector(`.llma-terminal-entry[data-id="${CSS.escape(output.id)}"]`);
        if (!screen || !entry) {
            this.renderTerminal();
            return;
        }
        // Follow the output only while the user is at the bottom: scrolling up to read must not be undone.
        let atBottom = screen.scrollHeight - screen.scrollTop - screen.clientHeight < 40;
        let out = entry.querySelector('.llma-terminal-out');
        for (let line of added) {
            out.appendChild(this.terminalRow(line.stream, line.text));
        }
        if (atBottom) {
            screen.scrollTop = screen.scrollHeight;
        }
    }

    /** The Tasks tab: the chat's task list, with a badge for each status. */
    renderTasks() {
        let body = document.getElementById('llma-code-body');
        if (!body) {
            return;
        }
        if (this.todos.length == 0) {
            body.innerHTML = `<div class="text-muted p-2">${translate('No tasks yet. When the assistant plans a job with several steps, its task list shows here.')}</div>`;
            return;
        }
        let badges = { completed: ['text-bg-success', translate('done')], in_progress: ['text-bg-primary', translate('working')], pending: ['text-bg-secondary', translate('to do')] };
        let rows = this.todos.map(todo => {
            let [style, label] = badges[todo.status] || badges.pending;
            return `<li class="list-group-item bg-transparent d-flex align-items-start gap-2 llma-code-todo" data-status="${llmaEscapeHtml(todo.status)}">`
                + `<span class="badge ${style} mt-1">${label}</span><span class="flex-grow-1${todo.status == 'completed' ? ' text-decoration-line-through text-muted' : ''}">${llmaEscapeHtml(todo.content)}</span></li>`;
        }).join('');
        body.innerHTML = `<ul class="list-group list-group-flush">${rows}</ul>`;
    }

    /** The Files tab: the folder tree and, under it, the file being viewed. */
    async renderFiles() {
        let body = document.getElementById('llma-code-body');
        if (!body) {
            return;
        }
        if (!LLMAState.activeThreadId) {
            body.innerHTML = `<div class="text-muted p-2">${translate('Send a message to start the session. Its files show here.')}</div>`;
            return;
        }
        body.innerHTML = '<div class="llma-code-tree"></div><div class="llma-code-viewer"></div>';
        await this.drawTree();
        this.drawViewer();
    }

    /** Loads (and caches) one folder's listing. Returns its entries, or null when it could not be listed. */
    async listing(path) {
        if (this.listings.has(path)) {
            return this.listings.get(path);
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantListWorkspaceFiles', { threadId: LLMAState.activeThreadId, path: path }), translate('Could not list the folder'));
        if (!reply) {
            return null;
        }
        this.listings.set(path, reply);
        return reply;
    }

    /** Draws the tree from the top, opening the folders that are open. */
    async drawTree() {
        let tree = document.querySelector('#llma-code-body .llma-code-tree');
        if (!tree) {
            return;
        }
        let html = await this.treeHtml('', 0);
        if (!document.body.contains(tree)) {
            return;
        }
        tree.innerHTML = html == '' ? `<div class="text-muted p-2">${translate('The workspace is empty. Ask the assistant to create something.')}</div>` : `<ul class="list-group list-group-flush" role="group" aria-label="${translate('Files')}">${html}</ul>`;
        for (let row of tree.querySelectorAll('.llma-code-entry')) {
            row.addEventListener('click', () => this.onEntry(row.dataset.path, row.dataset.type));
            row.addEventListener('keydown', event => {
                if (event.key == 'Enter' || event.key == ' ') {
                    event.preventDefault();
                    this.onEntry(row.dataset.path, row.dataset.type);
                }
            });
        }
    }

    /** The rows of one folder (and of its open subfolders), as HTML. */
    async treeHtml(path, depth) {
        let listing = await this.listing(path);
        if (!listing) {
            return '';
        }
        let html = '';
        for (let entry of listing.entries) {
            let full = path == '' ? entry.name : `${path}/${entry.name}`;
            let isDir = entry.type == 'dir';
            let isOpen = isDir && this.openFolders.has(full);
            let current = this.viewing && this.viewing.path == full ? ' active' : '';
            html += `<li class="list-group-item list-group-item-action bg-transparent py-1 llma-code-entry${current}" data-path="${llmaEscapeHtml(full)}" data-type="${entry.type}" style="padding-left:${0.5 + depth * 1}rem" role="button" tabindex="0">`
                + `${isDir ? (isOpen ? '&#9662; ' : '&#9656; ') : '&nbsp;&nbsp; '}${llmaEscapeHtml(entry.name)}${isDir ? '/' : ''}</li>`;
            if (isOpen) {
                html += await this.treeHtml(full, depth + 1);
            }
        }
        if (listing.truncated) {
            html += `<li class="list-group-item bg-transparent text-muted small" role="presentation">${translate('Only the first entries are shown.')}</li>`;
        }
        return html;
    }

    /** A row was clicked: a folder opens or closes, a file opens in the viewer. */
    async onEntry(path, type) {
        if (type == 'dir') {
            if (this.openFolders.has(path)) {
                this.openFolders.delete(path);
            }
            else {
                this.openFolders.add(path);
            }
            await this.drawTree();
            return;
        }
        await this.openFile(path, 1);
    }

    /** Reads a range of a file into the viewer. */
    async openFile(path, offset) {
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantReadWorkspaceFile', { threadId: LLMAState.activeThreadId, path: path, offset: offset, limit: 2000 }), translate('Could not read the file'));
        if (!reply) {
            return;
        }
        this.viewing = reply;
        this.drawViewer();
        await this.drawTree();
    }

    /** Draws the file viewer under the tree: its name, a close button, and numbered lines. */
    drawViewer() {
        let viewer = document.querySelector('#llma-code-body .llma-code-viewer');
        if (!viewer) {
            return;
        }
        if (!this.viewing) {
            viewer.innerHTML = '';
            return;
        }
        let file = this.viewing;
        let lines = file.content.replace(/\n$/, '').split('\n');
        let numbered = lines.map((line, i) => `${String(file.startLine + i).padStart(4, ' ')}  ${line}`).join('\n');
        let more = file.truncated ? `<button type="button" class="basic-button llma-code-more">${translate('Show the next lines')}</button>` : '';
        viewer.innerHTML = `<div class="d-flex align-items-center gap-2 border-top border-bottom py-1 px-2"><b class="flex-grow-1 text-truncate llma-code-viewer-name">${llmaEscapeHtml(file.path)}</b>`
            + `<span class="text-muted small">${file.startLine}-${file.endLine} / ${file.totalLines}</span>`
            + `<button type="button" class="btn-close llma-code-close" aria-label="${translate('Close')}"></button></div>`
            + `<pre class="llma-code-file"></pre>${more}`;
        viewer.querySelector('.llma-code-file').textContent = numbered;
        viewer.querySelector('.llma-code-close').addEventListener('click', () => {
            this.viewing = null;
            this.renderFiles();
        });
        viewer.querySelector('.llma-code-more')?.addEventListener('click', () => this.openFile(file.path, file.endLine + 1));
    }

    // ---- turns --------------------------------------------------------------------------------------------

    /** A tool finished during a reply: the task list updates at once; a change marks the tree stale. */
    onToolResult(toolResult) {
        let name = toolResult?.name || '';
        let result = toolResult?.result || {};
        if (name == 'code_todo' && result.success && Array.isArray(result.todos)) {
            this.todos = result.todos;
            if (this.isCode) {
                this.renderPanel();
            }
        }
        if (['code_write', 'code_edit', 'code_bash'].includes(name)) {
            this.dirty = true;
        }
        if (name == 'code_bash' && this.isCode && this.tab == 'terminal') {
            // The command ended: its row gets its status.
            this.renderTerminal();
        }
    }

    /** A reply ended: the tree catches up with what the assistant changed, and a plan made in Plan mode offers to be accepted. */
    onTurnFinished() {
        if (!this.isCode) {
            return;
        }
        if (this.dirty) {
            this.dirty = false;
            this.listings.clear();
            if (this.tab == 'files') {
                this.renderFiles();
            }
            this.refreshChanges();
        }
        let last = [...LLMAState.messages].reverse().find(m => m.role == 'assistant');
        let planned = LLMAState.approvalMode == 'plan' && last && ((last.content || '').trim() != '' || (last.toolCalls || []).length > 0);
        document.getElementById('llma-plan-card').style.display = planned ? '' : 'none';
    }

    /** Hides the plan card. */
    hidePlanCard() {
        let card = document.getElementById('llma-plan-card');
        if (card) {
            card.style.display = 'none';
        }
    }

    /** Accepts the plan: from here changes ask first, and the assistant is told to go ahead. */
    acceptPlan() {
        this.hidePlanCard();
        llmaSetApprovalMode('ask', true);
        let input = document.getElementById('llma-input');
        if (input) {
            input.value = translate('The plan is accepted. Carry it out.');
            llmaSendMessage();
        }
    }

    // ---- chat list -----------------------------------------------------------------------------------------

    /** Whether a chat is listed under the current filter. */
    showsThread(thread) {
        if (this.filter == 'all') {
            return true;
        }
        return (thread.mode == 'code' ? 'code' : 'chat') == this.filter;
    }
}

llmaCodeMode = new LLMACodeMode();

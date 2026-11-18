/**
 * Code mode: the Chat / Code switch, the workspace picker in the composer, and the Workspace panel on the right (the workspace's files and the
 * chat's task list), plus the "plan ready" card and the Chat / Code filter of the chat list.
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
        /** The panel tab showing: 'files' or 'tasks'. */
        this.tab = 'files';
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
        this.hidePlanCard();
        this.apply();
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
        }
        this.fillPicker();
        this.renderPanel();
    }

    // ---- the panel ----------------------------------------------------------------------------------------

    /** Draws the Workspace panel: Files and Tasks tabs. */
    renderPanel() {
        let inner = document.getElementById('llma-panel-inner');
        if (!inner || !this.isCode) {
            return;
        }
        let open = n => (this.tab == n ? ' active' : '');
        inner.innerHTML = `<ul class="nav nav-tabs llma-code-tabs" role="tablist">`
            + `<li class="nav-item"><button type="button" class="nav-link${open('files')}" data-code-tab="files" role="tab">${translate('Files')}</button></li>`
            + `<li class="nav-item"><button type="button" class="nav-link${open('tasks')}" data-code-tab="tasks" role="tab">${translate('Tasks')}${this.todos.length ? ` <span class="badge text-bg-secondary">${this.todos.length}</span>` : ''}</button></li>`
            + `<li class="nav-item ms-auto"><button type="button" class="btn btn-sm llma-code-refresh" title="${translate('Reload the file list')}" aria-label="${translate('Reload the file list')}">&#x21BB;</button></li>`
            + '</ul><div class="llma-code-body" id="llma-code-body"></div>';
        for (let button of inner.querySelectorAll('[data-code-tab]')) {
            button.addEventListener('click', () => {
                this.tab = button.dataset.codeTab;
                this.renderPanel();
            });
        }
        inner.querySelector('.llma-code-refresh').addEventListener('click', () => this.reload());
        if (this.tab == 'tasks') {
            this.renderTasks();
        }
        else {
            this.renderFiles();
        }
    }

    /** Drops what was loaded and shows the tree again. */
    async reload() {
        this.listings.clear();
        await this.renderFiles();
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
        tree.innerHTML = html == '' ? `<div class="text-muted p-2">${translate('The workspace is empty. Ask the assistant to create something.')}</div>` : `<ul class="list-group list-group-flush">${html}</ul>`;
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
            html += `<li class="list-group-item bg-transparent text-muted small">${translate('Only the first entries are shown.')}</li>`;
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

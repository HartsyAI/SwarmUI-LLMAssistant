/**
 * The Memory tab of the settings dialog (see the README, "Per-user memory"): pause switches, the automatic extractor's settings, retention, and the manager for the notes the assistant has
 * saved about the user (grouped by kind, searchable, editable in place, pin / delete / delete selected / clear all, export and import with a preview), and the history of automatic changes
 * with Undo. Built from Bootstrap's list-group, badge and form classes and the extension's own `basic-button`, so it carries almost no styling of its own (see llma-settings.css).
 *
 * Every note's text, and every string a model could have written, goes through escapeHtml before it is put in the page. The server cuts long texts in the list; the full text is fetched
 * only when a note is edited.
 *
 * It also holds the small notice shown in the chat when the extractor saves something (LLMAMemoryNotice below) and the top bar's pause and "don't remember this chat" buttons.
 */
class LLMAMemorySettings {

    /** The labels the slot notes (the ones there is only one of) are shown with. */
    static SLOT_LABELS = { name: 'name', pronouns: 'pronouns', bio: 'bio', currentWork: 'current work' };

    /** The groups notes are listed in, in order, and what each is called. */
    static GROUPS = ['profile', 'preference', 'dislike', 'fact', 'project', 'style'];
    static GROUP_LABELS = { profile: 'Name and profile', preference: 'Preferences', dislike: 'Dislikes', fact: 'Facts', project: 'Projects', style: 'Style' };

    /** What a note's origin means, in words. */
    static ORIGIN_LABELS = { user: 'you', assistant: 'the assistant', explicit: 'you asked it to remember' };

    /** The kinds a note can be given in the editor. */
    static KINDS = ['preference', 'dislike', 'fact', 'project', 'style'];

    /** How many notes are drawn before "Show more" (the most a memory holds is 400). */
    static PAGE = 100;

    /** The longest import file the server reads (characters); a larger one is refused here without being sent. */
    static MAX_IMPORT_CHARS = 1500000;

    constructor() {
        /** The notes as the server sent them (in the order they appear in the prompt). */
        this.notes = [];
        /** The store's limits: { maxNotes, maxNoteChars, maxPinned, promptBudgetChars }. */
        this.limits = {};
        /** How the stored notes read: 'ok', 'lossy', 'corrupt' or 'newer'. */
        this.state = 'ok';
        /** The privacy controls as the server sent them: { pause: { saving, recall }, retention: { unusedDays, min, max }, admin: { extractionDisabled, canWrite }, extractionAllowed }. */
        this.controls = null;
        /** What the list is narrowed to. */
        this.query = '';
        this.kindFilter = '';
        /** The ids of the notes ticked for "Delete selected". */
        this.selected = new Set();
        /** The id of the note being edited, or ''. */
        this.editing = '';
        /** How many notes the list draws. */
        this.limit = LLMAMemorySettings.PAGE;
        /** The text of the file chosen for import, kept while its preview is shown. */
        this.pendingImport = null;
        this.wired = false;
    }

    /** What to tell the user about a stored value that did not read cleanly (empty when all is well). Nothing is lost silently: the next save keeps the original as a backup. */
    static stateNotice(state) {
        switch (state) {
            case 'corrupt':
                return 'The saved notes could not be read, so none are shown. They are not deleted: the next time something is saved, the unreadable value is kept as a backup and a fresh list is started. Clearing memory removes the backup too.';
            case 'lossy':
                return 'Some saved notes could not be read and are left out of this list. They are not deleted: the next time something is saved, the original is kept as a backup, and only the notes shown here carry on.';
            case 'newer':
                return 'These notes were saved by a newer version of this extension. They are shown, but nothing can be saved or changed until the extension is updated.';
            default:
                return '';
        }
    }

    /** The group a note is listed under. */
    static groupOf(note) {
        return note.slot ? 'profile' : note.negative ? 'dislike' : (LLMAMemorySettings.GROUPS.includes(note.kind) ? note.kind : 'fact');
    }

    /** The privacy controls: what the server last said, or (before it has) what the settings the page loaded carry. */
    controlsOrFallback() {
        if (this.controls) {
            return this.controls;
        }
        let s = (typeof LLMAState != 'undefined' && LLMAState.settings) || {};
        return {
            pause: { saving: s.memoryPause?.saving === true, recall: s.memoryPause?.recall === true },
            retention: { unusedDays: Number.isInteger(s.memoryRetention?.unusedDays) ? s.memoryRetention.unusedDays : 0, min: 7, max: 3650 },
            admin: { extractionDisabled: s.memoryAdmin?.extractionDisabled === true, canWrite: !!(typeof LLMAState != 'undefined' && LLMAState.canWriteShared) }
        };
    }

    /** The automatic extractor's settings as saved: { enabled, model, neverSave[] }. */
    extractionSettings() {
        let saved = (typeof LLMAState != 'undefined' && LLMAState.settings?.memoryExtraction) || {};
        return { enabled: saved.enabled === true, model: typeof saved.model == 'string' ? saved.model : '', neverSave: Array.isArray(saved.neverSave) ? saved.neverSave.filter(s => typeof s == 'string') : [] };
    }

    /** Asks the server for the privacy controls and keeps them (the top bar's buttons read them too). Returns whether it worked. */
    async loadControls() {
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantGetMemoryControls', {});
        }
        catch {
            reply = null;
        }
        if (reply?.success) {
            this.setControls(reply);
        }
        return !!reply?.success;
    }

    /** Keeps a controls reply and brings the top bar in step with it. */
    setControls(reply) {
        this.controls = { pause: reply.pause, retention: reply.retention, admin: reply.admin, extractionAllowed: reply.extractionAllowed };
        if (typeof LLMAState != 'undefined') {
            LLMAState.memoryControls = this.controls;
        }
        llmaUpdateMemoryToggleBtn();
    }

    /** Saves some of the user's own pause switches or the retention days; returns the server's reply (null when it failed, having said so). */
    async setControlsOnServer(change) {
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantSetMemoryControls', change);
        }
        catch (err) {
            llmaShowToast(llmaShortError(err) || 'Could not change that.', 'error');
            return null;
        }
        if (!reply?.success) {
            llmaShowToast(reply?.error || 'Could not change that.', 'error');
            return null;
        }
        this.setControls(reply);
        return reply;
    }

    /** Draws the pause switches, and where this chat and the assistants stand. */
    renderPause() {
        let box = document.getElementById('llma-memory-pause');
        if (!box) {
            return;
        }
        let c = this.controlsOrFallback();
        let paused = c.pause.saving || c.pause.recall;
        box.innerHTML = `<h6>${translate('Pause memory')}${paused ? ` <span class="badge text-bg-warning llma-memory-paused-badge">${translate('paused')}</span>` : ''}</h6>`
            + `<label class="d-block mb-1"><input type="checkbox" id="llma-mempause-saving" ${c.pause.saving ? 'checked' : ''}> ${translate('Pause saving')}</label>`
            + `<div class="small text-muted ms-4 mb-2">${translate('While on, nothing new is saved: not by the automatic extractor and not by the assistant\'s memory tool. Notes you edit here are not affected.')}</div>`
            + `<label class="d-block mb-1"><input type="checkbox" id="llma-mempause-recall" ${c.pause.recall ? 'checked' : ''}> ${translate('Pause recall')}</label>`
            + `<div class="small text-muted ms-4 mb-2">${translate('While on, saved notes are not given to the assistant: not in its prompt and not through its memory tool. Nothing is deleted.')}</div>`
            + `<div class="small mb-2" id="llma-memory-where"></div><hr>`;
        for (let [id, field] of [['llma-mempause-saving', 'savingPaused'], ['llma-mempause-recall', 'recallPaused']]) {
            document.getElementById(id).addEventListener('change', async (e) => {
                let reply = await this.setControlsOnServer({ [field]: e.target.checked });
                if (!reply) {
                    e.target.checked = !e.target.checked;
                }
                this.renderPause();
                this.renderExtraction();
            });
        }
        this.renderWhere();
    }

    /** Draws where this chat and the assistants stand (whether they are remembered), under the pause switches. */
    renderWhere() {
        let where = document.getElementById('llma-memory-where');
        if (!where) {
            return;
        }
        let c = this.controlsOrFallback();
        let html = '';
        if (c.admin.extractionDisabled) {
            html += `<div>${translate('An administrator has switched off automatic saving for everyone.')}</div>`;
        }
        else if (!this.extractionSettings().enabled) {
            html += `<div>${translate('Automatic saving is off (see "Save memories automatically" below).')}</div>`;
        }
        else {
            let remembers = LLMAState.activeThreadMemoryExtraction !== false;
            html += `<div>${translate('This chat:')} <strong id="llma-memory-chat-state">${translate(remembers ? 'remembered' : 'not remembered')}</strong> `
                + `<button type="button" class="basic-button" id="llma-memory-chat-btn">${translate(remembers ? 'Don\'t remember this chat' : 'Remember this chat')}</button></div>`;
            let quiet = (LLMAState.assistants || []).filter(a => a._effectiveMemoryExtraction === false).map(a => a.name || a.id);
            if (quiet.length > 0) {
                html += `<div class="text-muted">${translate('Assistants that do not save memories:')} ${quiet.map(n => escapeHtml(n)).join(', ')} <span>(${translate('change it in the assistant\'s own settings')})</span></div>`;
            }
        }
        where.innerHTML = html;
        document.getElementById('llma-memory-chat-btn')?.addEventListener('click', async () => {
            await llmaToggleThreadMemory();
            this.renderWhere();
        });
    }

    /** Draws the extractor's settings: the master switch (off by default), the model, and the never-save list. */
    renderExtraction() {
        let box = document.getElementById('llma-memory-extraction');
        if (!box) {
            return;
        }
        let s = this.extractionSettings();
        let c = this.controlsOrFallback();
        let locked = c.admin.extractionDisabled;
        let models = ((typeof LLMAState != 'undefined' && LLMAState.availableModels) || []).map(m => m.id).slice(0, 500);
        let options = models.map(n => `<option value="${escapeHtml(n)}"></option>`).join('');
        box.innerHTML = `<h6>${translate('Save memories automatically')}</h6>`
            + (locked ? `<div class="alert alert-warning py-2 llma-memory-admin-lock">${translate('An administrator has switched off automatic saving for everyone, so this cannot be turned on. The assistant can still save a note when you ask it to remember something.')}</div>` : '')
            + `<label class="d-block mb-1"><input type="checkbox" id="llma-memx-enabled" ${s.enabled ? 'checked' : ''} ${locked ? 'disabled' : ''}> ${translate('After a reply, look for things worth remembering about me')}</label>`
            + `<div class="small text-muted mb-2">${translate('Off by default. When on, a turn where you say something about yourself costs one extra model call after the reply (at most 20 an hour). It reads only what you and the assistant wrote, never tool results, web pages or attachments, and a turn where the assistant used a tool is read from your messages alone. Every change is shown in the chat with an Undo.')}</div>`
            + `<div class="mb-2"><label class="small" for="llma-memx-model">${translate('Model that does it')}</label>`
            + `<input type="text" class="form-control" id="llma-memx-model" list="llma-memx-models" placeholder="${escapeHtml(translate('Empty: the model that answered the chat'))}" value="${escapeHtml(s.model)}" autocomplete="off"><datalist id="llma-memx-models">${options}</datalist>`
            + `<div class="small text-muted">${translate('Any model from any backend. A small one is enough; a local model that is busy generating will make the extraction wait.')}</div></div>`
            + `<div class="mb-2"><label class="small" for="llma-memx-never">${translate('Never save (one per line)')}</label>`
            + `<textarea class="form-control" rows="3" id="llma-memx-never" placeholder="${escapeHtml(translate('A word or phrase, or /regular expression/'))}">${escapeHtml(s.neverSave.join('\n'))}</textarea>`
            + `<div class="small text-muted">${translate('A turn that mentions one of these is not sent to the model at all. Passwords, keys and card numbers are always left out.')}</div></div>`
            + `<button type="button" class="basic-button btn-primary" id="llma-memx-save">${translate('Save')}</button> <span class="small text-muted" id="llma-memx-out"></span>`
            + (c.admin.canWrite ? `<div class="mt-3 llma-memory-admin"><label class="d-block"><input type="checkbox" id="llma-memadmin-off" ${locked ? 'checked' : ''}> ${translate('Admin: switch off automatic saving for all users')}</label>`
                + `<div class="small text-muted ms-4">${translate('Users cannot turn it back on. It does not delete anything, and the memory tool and recall keep working.')}</div></div>` : '')
            + '<hr>';
        document.getElementById('llma-memx-save').addEventListener('click', () => this.saveExtraction());
        document.getElementById('llma-memadmin-off')?.addEventListener('change', async (e) => {
            let reply = null;
            try {
                reply = await llmaRequest('LLMAssistantSetMemoryAdmin', { extractionDisabled: e.target.checked });
            }
            catch (err) {
                llmaShowToast(llmaShortError(err) || 'Could not change that.', 'error');
            }
            if (reply?.success) {
                this.setControls(reply);
                llmaShowToast(e.target.checked ? 'Automatic saving is off for all users.' : 'Automatic saving may be turned on by each user.', 'info');
            }
            else {
                e.target.checked = !e.target.checked;
                if (reply?.error) {
                    llmaShowToast(reply.error, 'error');
                }
            }
            this.renderPause();
            this.renderExtraction();
        });
    }

    /** Saves the extractor's settings in the user's own settings. */
    async saveExtraction() {
        let out = document.getElementById('llma-memx-out');
        let never = document.getElementById('llma-memx-never').value.split('\n').map(s => s.trim()).filter(s => s.length > 0).slice(0, 50);
        LLMAState.settings = LLMAState.settings || {};
        LLMAState.settings.memoryExtraction = { enabled: document.getElementById('llma-memx-enabled').checked, model: document.getElementById('llma-memx-model').value.trim(), neverSave: never };
        await llmaSaveSettings();
        llmaUpdateMemoryToggleBtn();
        this.renderWhere();
        out.textContent = translate('Saved.');
    }

    /** Draws the retention setting: forget notes unused for N days (off by default). */
    renderRetention() {
        let box = document.getElementById('llma-memory-retention');
        if (!box) {
            return;
        }
        let r = this.controlsOrFallback().retention;
        box.innerHTML = `<h6>${translate('Forget unused notes')}</h6>`
            + `<div class="d-flex align-items-center gap-2 flex-wrap mb-1"><label for="llma-memret-days" class="small mb-0">${translate('Forget notes not used for')}</label>`
            + `<input type="number" class="form-control llma-memory-days" id="llma-memret-days" min="${r.min}" max="${r.max}" step="1" placeholder="${escapeHtml(translate('off'))}" value="${r.unusedDays > 0 ? r.unusedDays : ''}">`
            + `<span class="small">${translate('days')}</span><button type="button" class="basic-button" id="llma-memret-save">${translate('Save')}</button> <span class="small text-muted" id="llma-memret-out"></span></div>`
            + `<div class="small text-muted">${translate('Off when empty. A note counts as used when it is put in a reply\'s prompt (or from when it was saved, if never). Pinned notes and your name, pronouns, bio and current work are never forgotten. What is forgotten is listed under Recent automatic changes, where Undo brings it back.')}</div><hr>`;
        document.getElementById('llma-memret-save').addEventListener('click', async () => {
            let raw = document.getElementById('llma-memret-days').value.trim();
            let days = raw === '' ? 0 : Number(raw);
            let out = document.getElementById('llma-memret-out');
            if (!Number.isFinite(days) || days < 0 || (days > 0 && days < r.min) || days > r.max) {
                out.textContent = `${translate('Use a number from')} ${r.min} ${translate('to')} ${r.max}, ${translate('or leave it empty')}.`;
                return;
            }
            let reply = await this.setControlsOnServer({ unusedDays: Math.round(days) });
            if (!reply) {
                return;
            }
            this.renderRetention();
            document.getElementById('llma-memret-out').textContent = days === 0 ? translate('Off.') : `${translate('Saved.')}${reply.forgotten > 0 ? ` ${reply.forgotten} ${translate('notes forgotten.')}` : ''}`;
            if (reply.forgotten > 0) {
                await this.loadNotes();
                this.loadChanges();
            }
        });
    }

    /** Loads the recent automatic changes and draws them with an Undo each. */
    async loadChanges() {
        let box = document.getElementById('llma-memory-changes');
        if (!box) {
            return;
        }
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantGetMemoryChanges', {});
        }
        catch {
            reply = null;
        }
        let sets = reply?.success ? (reply.changes || []) : [];
        if (sets.length == 0) {
            box.innerHTML = '';
            return;
        }
        let html = `<h6 class="mt-3">${translate('Recent automatic changes')}</h6><ul class="list-group mb-2">`;
        for (let set of sets) {
            let why = set.reason == 'retention' ? `<div><span class="badge text-bg-secondary">${translate('forgotten: not used for a while')}</span></div>` : '';
            html += `<li class="list-group-item bg-transparent llma-memory-change" data-change-id="${escapeHtml(set.id)}"><div class="d-flex justify-content-between gap-2"><div class="small llma-memory-change-body">`
                + `<div class="text-muted">${escapeHtml(set.at ? new Date(set.at).toLocaleString() : '')}</div>${why}${LLMAMemoryNotice.linesHtml(set)}</div>`
                + `<div><button type="button" class="basic-button llma-memory-undo">${translate('Undo')}</button></div></div></li>`;
        }
        box.innerHTML = html + '</ul>';
        for (let button of box.querySelectorAll('.llma-memory-undo')) {
            button.addEventListener('click', async () => {
                let id = button.closest('.llma-memory-change').dataset.changeId;
                button.disabled = true;
                if (await LLMAMemoryNotice.undo(id)) {
                    await this.load();
                }
                else {
                    button.disabled = false;
                }
            });
        }
    }

    /** Loads everything the tab shows and draws it. */
    async load() {
        this.renderPause();
        this.renderExtraction();
        this.renderRetention();
        this.renderToolbar();
        this.loadControls().then(ok => {
            if (ok) {
                this.renderPause();
                this.renderExtraction();
                this.renderRetention();
            }
        });
        this.loadChanges();
        await this.loadNotes();
    }

    /** Loads the notes from the server and draws them. */
    async loadNotes() {
        let list = document.getElementById('llma-memory-list');
        if (!list) {
            return;
        }
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantGetUserProfile', { includeNotes: true });
        }
        catch {
            reply = null;
        }
        if (!reply || !reply.success) {
            list.innerHTML = `<div class="text-muted py-2">${translate('Memory is not available to you.')}</div>`;
            return;
        }
        this.notes = reply.notes || [];
        this.limits = reply.limits || {};
        this.state = reply.state || 'ok';
        let ids = new Set(this.notes.map(n => n.id));
        this.selected = new Set([...this.selected].filter(id => ids.has(id)));
        if (!ids.has(this.editing)) {
            this.editing = '';
        }
        this.render();
    }

    /** Draws the search box, the kind filter and the buttons above the list (once; they keep what was typed). */
    renderToolbar() {
        let bar = document.getElementById('llma-memory-toolbar');
        if (!bar || bar.dataset.ready) {
            this.wire();
            return;
        }
        bar.dataset.ready = '1';
        let kinds = ['<option value="">' + escapeHtml(translate('All kinds')) + '</option>']
            .concat(LLMAMemorySettings.GROUPS.map(g => `<option value="${g}">${escapeHtml(translate(LLMAMemorySettings.GROUP_LABELS[g]))}</option>`))
            .concat([`<option value="pinned">${escapeHtml(translate('Pinned'))}</option>`]);
        bar.innerHTML = `<h6>${translate('Your notes')}</h6>`
            + `<div class="llma-memory-toolbar">`
            + `<input type="search" class="form-control llma-memory-search" id="llma-memory-search" placeholder="${escapeHtml(translate('Search notes'))}" aria-label="${escapeHtml(translate('Search notes'))}" autocomplete="off">`
            + `<select class="form-select llma-memory-kind-filter" id="llma-memory-kind-filter" aria-label="${escapeHtml(translate('Filter by kind'))}">${kinds.join('')}</select></div>`
            + `<div class="llma-memory-toolbar mt-2">`
            + `<label class="mb-0 small"><input type="checkbox" id="llma-memory-select-all"> ${translate('Select all shown')}</label>`
            + `<button type="button" class="basic-button" id="llma-memory-delete-selected" disabled>${translate('Delete selected')}</button>`
            + `<button type="button" class="basic-button" id="llma-memory-export">${translate('Export')}</button>`
            + `<button type="button" class="basic-button" id="llma-memory-import">${translate('Import…')}</button>`
            + `<button type="button" class="basic-button llma-memory-danger" id="llma-memory-clear">${translate('Clear all')}</button>`
            + `<input type="file" id="llma-memory-import-file" accept=".json,application/json" hidden></div>`;
        this.wire();
    }

    /** Hooks up the toolbar and the list (once each: the list element stays, only what is inside it is redrawn). */
    wire() {
        if (this.wired || !document.getElementById('llma-memory-search')) {
            return;
        }
        this.wired = true;
        document.getElementById('llma-memory-search').addEventListener('input', (e) => {
            this.query = e.target.value;
            this.limit = LLMAMemorySettings.PAGE;
            this.render();
        });
        document.getElementById('llma-memory-kind-filter').addEventListener('change', (e) => {
            this.kindFilter = e.target.value;
            this.limit = LLMAMemorySettings.PAGE;
            this.render();
        });
        document.getElementById('llma-memory-select-all').addEventListener('change', (e) => {
            for (let note of this.filtered()) {
                if (e.target.checked) {
                    this.selected.add(note.id);
                }
                else {
                    this.selected.delete(note.id);
                }
            }
            this.render();
        });
        document.getElementById('llma-memory-delete-selected').addEventListener('click', () => this.deleteSelected());
        document.getElementById('llma-memory-export').addEventListener('click', () => this.exportNotes());
        document.getElementById('llma-memory-clear').addEventListener('click', () => this.clearAll());
        let file = document.getElementById('llma-memory-import-file');
        document.getElementById('llma-memory-import').addEventListener('click', () => file.click());
        file.addEventListener('change', async () => {
            let chosen = file.files?.[0];
            file.value = '';
            await this.previewImport(chosen);
        });
        let list = document.getElementById('llma-memory-list');
        list.addEventListener('click', (e) => this.onListClick(e));
        list.addEventListener('change', (e) => {
            if (e.target.classList.contains('llma-memory-select')) {
                let id = e.target.closest('.llma-memory-note').dataset.noteId;
                if (e.target.checked) {
                    this.selected.add(id);
                }
                else {
                    this.selected.delete(id);
                }
                this.updateSelectionUi();
            }
        });
        list.addEventListener('keydown', (e) => {
            if (e.key == 'Escape' && this.editing) {
                this.editing = '';
                this.render();
            }
        });
    }

    /** The notes that match the search and the kind filter. */
    filtered() {
        let q = this.query.trim().toLowerCase();
        return this.notes.filter(n => {
            if (this.kindFilter == 'pinned' ? !n.pinned : (this.kindFilter && LLMAMemorySettings.groupOf(n) != this.kindFilter)) {
                return false;
            }
            return !q || String(n.text).toLowerCase().includes(q);
        });
    }

    /** Draws the totals and the notes. */
    render() {
        let totals = document.getElementById('llma-memory-totals');
        let list = document.getElementById('llma-memory-list');
        if (!totals || !list) {
            return;
        }
        let shown = this.filtered();
        totals.textContent = `${this.notes.length} / ${this.limits.maxNotes ?? 0} ${translate('notes')}${shown.length != this.notes.length ? ` · ${shown.length} ${translate('shown')}` : ''}`;
        let notice = LLMAMemorySettings.stateNotice(this.state);
        let noticeHtml = notice ? `<div class="alert alert-warning py-2 llma-memory-notice" data-state="${escapeHtml(this.state)}">${escapeHtml(translate(notice))}</div>` : '';
        if (this.notes.length == 0) {
            list.innerHTML = noticeHtml + (this.state != 'ok' ? '' : `<div class="text-muted py-2">${translate('Nothing is remembered yet. Ask the assistant to remember something, or tell it about yourself.')}</div>`);
            this.updateSelectionUi();
            return;
        }
        if (shown.length == 0) {
            list.innerHTML = noticeHtml + `<div class="text-muted py-2">${translate('No note matches.')}</div>`;
            this.updateSelectionUi();
            return;
        }
        let drawn = shown.slice(0, this.limit);
        let html = noticeHtml;
        for (let group of LLMAMemorySettings.GROUPS) {
            let notes = drawn.filter(n => LLMAMemorySettings.groupOf(n) == group);
            if (notes.length == 0) {
                continue;
            }
            html += `<div class="llma-memory-group" data-group="${group}"><h6 class="mt-2">${translate(LLMAMemorySettings.GROUP_LABELS[group])} <span class="text-muted small">${shown.filter(n => LLMAMemorySettings.groupOf(n) == group).length}</span></h6><ul class="list-group mb-2">`;
            for (let note of notes) {
                html += note.id == this.editing ? this.editHtml(note) : this.noteHtml(note);
            }
            html += '</ul></div>';
        }
        if (shown.length > drawn.length) {
            html += `<button type="button" class="basic-button llma-memory-more">${translate('Show more')} (${shown.length - drawn.length})</button>`;
        }
        list.innerHTML = html;
        this.updateSelectionUi();
        list.querySelector('.llma-memory-edit-text')?.focus();
    }

    /** One note's row. */
    noteHtml(note) {
        let kind = note.negative ? 'dislike' : (note.slot ? (LLMAMemorySettings.SLOT_LABELS[note.slot] || note.slot) : note.kind);
        let pinned = note.pinned ? ` <span class="badge text-bg-info llma-memory-pinned" title="${escapeHtml(translate('Always in the assistant\'s prompt.'))}">${translate('pinned')}</span>` : '';
        let saved = note.created ? new Date(note.created).toLocaleDateString() : '';
        let used = note.lastUsed ? `${translate('last used')} ${new Date(note.lastUsed).toLocaleDateString()}` : translate('never used');
        let origin = LLMAMemorySettings.ORIGIN_LABELS[note.origin] || note.origin || '';
        let pin = note.slot ? '' : `<button type="button" class="basic-button llma-memory-pin" aria-pressed="${note.pinned ? 'true' : 'false'}">${translate(note.pinned ? 'Unpin' : 'Pin')}</button>`;
        return `<li class="list-group-item bg-transparent llma-memory-note" data-note-id="${escapeHtml(note.id)}"><div class="llma-memory-row">`
            + `<input type="checkbox" class="form-check-input llma-memory-select" aria-label="${escapeHtml(translate('Select this note'))}" ${this.selected.has(note.id) ? 'checked' : ''}>`
            + `<div class="llma-memory-main"><span class="badge text-bg-secondary">${escapeHtml(translate(kind))}</span>${pinned} <span class="text-break llma-memory-text">${escapeHtml(note.text)}${note.truncated ? '…' : ''}</span>`
            + `<br><small class="text-muted">${escapeHtml(translate('from'))} ${escapeHtml(translate(origin))}${saved ? ` · ${escapeHtml(translate('saved'))} ${escapeHtml(saved)}` : ''} · ${escapeHtml(used)}</small></div>`
            + `<div class="llma-memory-actions"><button type="button" class="basic-button llma-memory-edit">${translate('Edit')}</button>${pin}`
            + `<button type="button" class="basic-button llma-memory-delete">${translate('Delete')}</button></div></div></li>`;
    }

    /** One note's row while it is being edited. */
    editHtml(note) {
        let kind = note.negative ? 'dislike' : note.kind;
        let options = LLMAMemorySettings.KINDS.map(k => `<option value="${k}" ${k == kind ? 'selected' : ''}>${escapeHtml(translate(k))}</option>`).join('');
        let kindField = note.slot
            ? `<span class="badge text-bg-secondary">${escapeHtml(translate(LLMAMemorySettings.SLOT_LABELS[note.slot] || note.slot))}</span>`
            : `<select class="form-select form-select-sm llma-memory-edit-kind" aria-label="${escapeHtml(translate('Kind'))}">${options}</select>`;
        return `<li class="list-group-item bg-transparent llma-memory-note llma-memory-editing" data-note-id="${escapeHtml(note.id)}">`
            + `<textarea class="form-control mb-2 llma-memory-edit-text" rows="3" maxlength="${this.limits.maxNoteChars || 2000}" aria-label="${escapeHtml(translate('Note text'))}">${escapeHtml(note.text)}</textarea>`
            + `<div class="d-flex gap-2 align-items-center flex-wrap">${kindField}`
            + `<button type="button" class="basic-button btn-primary llma-memory-edit-save">${translate('Save')}</button>`
            + `<button type="button" class="basic-button llma-memory-edit-cancel">${translate('Cancel')}</button>`
            + `<span class="small text-danger llma-memory-edit-error" role="alert"></span></div></li>`;
    }

    /** Keeps the select-all box and the Delete selected button in step with what is ticked. */
    updateSelectionUi() {
        let button = document.getElementById('llma-memory-delete-selected');
        if (button) {
            button.disabled = this.selected.size == 0;
            button.textContent = this.selected.size > 0 ? `${translate('Delete selected')} (${this.selected.size})` : translate('Delete selected');
        }
        let all = document.getElementById('llma-memory-select-all');
        if (all) {
            let shown = this.filtered();
            all.checked = shown.length > 0 && shown.every(n => this.selected.has(n.id));
        }
    }

    /** What a click inside the list does (the buttons of a row). */
    async onListClick(e) {
        if (e.target.closest('.llma-memory-more')) {
            this.limit += LLMAMemorySettings.PAGE;
            this.render();
            return;
        }
        let row = e.target.closest('.llma-memory-note');
        let button = e.target.closest('button');
        if (!row || !button) {
            return;
        }
        let id = row.dataset.noteId;
        if (button.classList.contains('llma-memory-edit')) {
            await this.startEdit(id);
        }
        else if (button.classList.contains('llma-memory-edit-cancel')) {
            this.editing = '';
            this.render();
        }
        else if (button.classList.contains('llma-memory-edit-save')) {
            await this.saveEdit(id, row);
        }
        else if (button.classList.contains('llma-memory-pin')) {
            let note = this.notes.find(n => n.id == id);
            await this.updateNote({ id: id, pinned: !note?.pinned });
        }
        else if (button.classList.contains('llma-memory-delete')) {
            await this.deleteNotes([id]);
        }
    }

    /** Opens the editor on a note, with its whole text (the list cuts long ones). */
    async startEdit(id) {
        let note = this.notes.find(n => n.id == id);
        if (note?.truncated) {
            let reply = null;
            try {
                reply = await llmaRequest('LLMAssistantGetMemoryNote', { id: id });
            }
            catch {
                reply = null;
            }
            if (!reply?.success) {
                llmaShowToast(reply?.error || 'Could not open that note.', 'error');
                await this.loadNotes();
                return;
            }
            Object.assign(note, reply.note);
        }
        this.editing = id;
        this.render();
    }

    /** Saves the text and kind from the editor. */
    async saveEdit(id, row) {
        let text = row.querySelector('.llma-memory-edit-text').value;
        let kind = row.querySelector('.llma-memory-edit-kind')?.value;
        let error = row.querySelector('.llma-memory-edit-error');
        if (text.trim() === '') {
            error.textContent = translate('A note cannot be empty.');
            return;
        }
        let change = { id: id, text: text };
        if (kind) {
            change.kind = kind;
        }
        let ok = await this.updateNote(change, error);
        if (ok) {
            this.editing = '';
            this.render();
        }
    }

    /** Sends a change to one note and replaces it in the list. Returns whether it was saved; a refusal is shown in `errorEl` when given, else as a toast. */
    async updateNote(change, errorEl) {
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantUpdateMemoryNote', change);
        }
        catch (err) {
            reply = { success: false, error: llmaShortError(err) };
        }
        if (!reply?.success) {
            if (errorEl) {
                errorEl.textContent = reply?.error || translate('Could not save that.');
            }
            else {
                llmaShowToast(reply?.error || 'Could not save that.', 'error');
            }
            if (reply?.code == 'notfound') {
                await this.loadNotes();
            }
            return false;
        }
        let at = this.notes.findIndex(n => n.id == reply.note.id);
        if (at >= 0) {
            this.notes[at] = reply.note;
        }
        this.render();
        return true;
    }

    /** Deletes notes (final), asking first when there is more than one. */
    async deleteNotes(ids) {
        if (ids.length > 1 && !confirm(`${translate('Delete')} ${ids.length} ${translate('notes? This cannot be undone.')}`)) {
            return;
        }
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantDeleteMemoryNotes', { ids: ids });
        }
        catch (err) {
            reply = { success: false, error: llmaShortError(err) };
        }
        if (!reply?.success) {
            llmaShowToast(reply?.error || 'Could not delete that.', 'error');
            return;
        }
        for (let id of ids) {
            this.selected.delete(id);
        }
        llmaShowToast(reply.deleted == 1 ? 'Deleted 1 note.' : `Deleted ${reply.deleted} notes.`, 'success');
        await this.loadNotes();
    }

    /** Deletes the ticked notes. */
    async deleteSelected() {
        await this.deleteNotes([...this.selected]);
    }

    /** Deletes every note, after asking. */
    async clearAll() {
        if (!confirm(translate('Delete every note, and the backups and history kept with them? This cannot be undone.'))) {
            return;
        }
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantClearUserProfile', {});
        }
        catch (err) {
            reply = { success: false, error: llmaShortError(err) };
        }
        if (!reply?.success) {
            llmaShowToast(reply?.error || 'Could not clear memory.', 'error');
            return;
        }
        this.selected.clear();
        this.editing = '';
        this.closeImport();
        llmaShowToast('Memory cleared.', 'success');
        await this.loadNotes();
        this.loadChanges();
    }

    /** Downloads the notes as a JSON file. */
    async exportNotes() {
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantExportMemory', {});
        }
        catch (err) {
            reply = { success: false, error: llmaShortError(err) };
        }
        if (!reply?.success) {
            llmaShowToast(reply?.error || 'Could not export memory.', 'error');
            return;
        }
        let blob = new Blob([JSON.stringify(reply.export, null, 2)], { type: 'application/json' });
        let url = URL.createObjectURL(blob);
        let link = document.createElement('a');
        link.href = url;
        link.download = `llm-assistant-memory-${new Date().toISOString().slice(0, 10)}.json`;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    /** Reads a chosen file and asks the server what importing it would do; shows that, and changes nothing. */
    async previewImport(file) {
        if (!file) {
            return;
        }
        if (file.size > LLMAMemorySettings.MAX_IMPORT_CHARS * 3) {
            llmaShowToast('That file is too large to be a memory export.', 'error');
            return;
        }
        let data = await file.text();
        if (data.length > LLMAMemorySettings.MAX_IMPORT_CHARS) {
            llmaShowToast('That file is too large to be a memory export.', 'error');
            return;
        }
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantPreviewMemoryImport', { data: data });
        }
        catch (err) {
            reply = { success: false, error: llmaShortError(err) };
        }
        if (!reply?.success) {
            llmaShowToast(reply?.error || 'Could not read that file.', 'error');
            return;
        }
        this.pendingImport = data;
        this.renderImport(reply.preview);
    }

    /** Draws the import preview: what would be added, and why the rest would not. Nothing in it is a button that changes anything until "Add". */
    renderImport(p) {
        let box = document.getElementById('llma-memory-import-preview');
        if (!box) {
            return;
        }
        let skips = [['duplicates', 'already in memory'], ['conflicts', 'for a name, pronouns, bio or current work you already have (yours is kept)'], ['blocked', 'mention a password or something on your never-save list'],
            ['invalid', 'not readable as notes'], ['noRoom', 'do not fit (memory is full; nothing is dropped to make room)'], ['ignored', 'past the first 400 entries'], ['unpinned', 'added without the pin (too many pinned)']]
            .filter(([key]) => p[key] > 0)
            .map(([key, why]) => `<li><span class="llma-memory-import-${key}">${p[key]}</span> ${translate(why)}</li>`).join('');
        let added = (p.add || []).map(n => `<li class="list-group-item bg-transparent"><span class="badge text-bg-secondary">${escapeHtml(translate(n.slot ? (LLMAMemorySettings.SLOT_LABELS[n.slot] || n.slot) : n.kind))}</span> <span class="text-break">${escapeHtml(n.text)}</span></li>`).join('');
        box.innerHTML = `<div class="alert alert-info llma-memory-import"><h6>${translate('Import preview')}</h6>`
            + `<div class="mb-1"><strong class="llma-memory-import-add">${p.addCount}</strong> ${translate(p.addCount == 1 ? 'note will be added.' : 'notes will be added.')} ${translate('Nothing you have is replaced or removed.')}</div>`
            + (skips ? `<ul class="small mb-2">${skips}</ul>` : '')
            + (added ? `<ul class="list-group mb-2 llma-memory-import-list">${added}</ul>${p.addCount > (p.add || []).length ? `<div class="small text-muted mb-2">${translate('and')} ${p.addCount - p.add.length} ${translate('more')}</div>` : ''}` : '')
            + `<button type="button" class="basic-button btn-primary" id="llma-memory-import-apply" ${p.addCount == 0 ? 'disabled' : ''}>${translate('Add')} ${p.addCount} ${translate(p.addCount == 1 ? 'note' : 'notes')}</button> `
            + `<button type="button" class="basic-button" id="llma-memory-import-cancel">${translate('Cancel')}</button></div>`;
        document.getElementById('llma-memory-import-cancel').addEventListener('click', () => this.closeImport());
        document.getElementById('llma-memory-import-apply').addEventListener('click', () => this.applyImport());
    }

    /** Hides the import preview and forgets the file. */
    closeImport() {
        this.pendingImport = null;
        let box = document.getElementById('llma-memory-import-preview');
        if (box) {
            box.innerHTML = '';
        }
    }

    /** Adds the notes. The server works out again what to add, from the file and the notes as they are now. */
    async applyImport() {
        let data = this.pendingImport;
        if (!data) {
            return;
        }
        let button = document.getElementById('llma-memory-import-apply');
        if (button) {
            button.disabled = true;
        }
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantImportMemory', { data: data });
        }
        catch (err) {
            reply = { success: false, error: llmaShortError(err) };
        }
        if (!reply?.success) {
            llmaShowToast(reply?.error || 'Could not import that.', 'error');
            if (button) {
                button.disabled = false;
            }
            return;
        }
        this.closeImport();
        llmaShowToast(reply.preview.addCount == 1 ? 'Added 1 note.' : `Added ${reply.preview.addCount} notes.`, 'success');
        await this.loadNotes();
    }
}

/**
 * The small notice shown in the chat when the extractor saves something: what was added, updated, removed (or dropped to make room), with Undo. It does not block anything, stays for a while
 * (longer while the pointer is on it), and is dismissed with the cross. The server keeps the change either way, so a notice that was missed is still listed in Settings > Memory.
 */
class LLMAMemoryNotice {

    /** How long the notice stays when nobody touches it. */
    static SHOW_MS = 20000;

    /** The lines that say what changed, as HTML (note text is always escaped). */
    static linesHtml(set) {
        let line = (label, text, extra) => `<div class="llma-memory-notice-line"><span class="badge text-bg-secondary">${translate(label)}</span> <span class="text-break">${escapeHtml(text)}</span>${extra || ''}</div>`;
        let html = '';
        for (let n of set.added || []) {
            html += line('added', n.text);
        }
        for (let n of set.updated || []) {
            html += line('updated', n.text, n.previous ? ` <span class="text-muted">(${escapeHtml(translate('was'))}: ${escapeHtml(n.previous)})</span>` : '');
        }
        for (let n of set.removed || []) {
            html += line('removed', n.text);
        }
        for (let n of set.evicted || []) {
            html += line('dropped to make room', n.text);
        }
        return html;
    }

    /** Shows the notice for a change set the server sent (a memoryChanges frame on the chat socket). */
    static show(set) {
        let container = document.getElementById('llma-container');
        if (!container || !set || !set.id) {
            return;
        }
        container.querySelector('.llma-memory-notice')?.remove();
        let el = document.createElement('div');
        el.className = 'llma-memory-notice';
        el.setAttribute('role', 'status');
        el.setAttribute('aria-live', 'polite');
        el.dataset.changeId = set.id;
        el.innerHTML = `<div class="llma-memory-notice-head"><strong>${translate('Saved to memory')}</strong>`
            + `<span><button type="button" class="basic-button llma-memory-notice-undo">${translate('Undo')}</button>`
            + `<button type="button" class="llma-memory-notice-close" aria-label="${escapeHtml(translate('Dismiss'))}" title="${escapeHtml(translate('Dismiss'))}">&times;</button></span></div>`
            + `<div class="llma-memory-notice-body">${LLMAMemoryNotice.linesHtml(set)}</div>`;
        // Just above the composer, in the flow of the chat, so it never covers what is being typed.
        let wrap = container.querySelector('.llma-input-wrap');
        if (wrap?.parentElement) {
            wrap.parentElement.insertBefore(el, wrap);
        }
        else {
            container.appendChild(el);
        }
        let timer = null;
        let arm = () => {
            clearTimeout(timer);
            timer = setTimeout(() => el.remove(), LLMAMemoryNotice.SHOW_MS);
        };
        el.addEventListener('mouseenter', () => clearTimeout(timer));
        el.addEventListener('mouseleave', arm);
        el.querySelector('.llma-memory-notice-close').addEventListener('click', () => el.remove());
        el.querySelector('.llma-memory-notice-undo').addEventListener('click', async () => {
            clearTimeout(timer);
            let button = el.querySelector('.llma-memory-notice-undo');
            button.disabled = true;
            if (await LLMAMemoryNotice.undo(set.id)) {
                el.remove();
            }
            else {
                button.disabled = false;
                arm();
            }
        });
        arm();
    }

    /** Asks the server to undo a change set; says what happened. Returns whether it was undone (at least in part). */
    static async undo(changeId) {
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantUndoMemoryChange', { changeId: changeId });
        }
        catch (err) {
            llmaShowToast(llmaShortError(err) || 'Could not undo that.', 'error');
            return false;
        }
        if (!reply?.success) {
            llmaShowToast(reply?.error || 'Could not undo that.', 'error');
            return false;
        }
        if (reply.refused) {
            llmaShowToast('Memory could not be changed right now (see Settings > Memory).', 'error');
            return false;
        }
        if (!reply.found) {
            llmaShowToast('That change was already undone.', 'info');
            return true;
        }
        llmaShowToast(reply.skipped > 0 ? 'Undone, except notes that were changed since.' : 'Undone.', 'success');
        return true;
    }
}

let llmaMemorySettings = new LLMAMemorySettings();

/** The privacy controls the page knows (the server's last answer, else what the loaded settings carry): { pause: { saving, recall }, admin: { extractionDisabled } }. */
function llmaMemoryControlsNow() {
    if (typeof LLMAState == 'undefined') return { pause: {}, admin: {} };
    const s = LLMAState.settings || {};
    return LLMAState.memoryControls || { pause: { saving: s.memoryPause?.saving === true, recall: s.memoryPause?.recall === true }, admin: { extractionDisabled: s.memoryAdmin?.extractionDisabled === true } };
}

/** Whether the automatic extractor is switched on in the user's own settings and not switched off for everyone by an admin (the per-chat button is only shown then). */
function llmaMemoryExtractionOn() {
    return typeof LLMAState != 'undefined' && LLMAState.settings?.memoryExtraction?.enabled === true && llmaMemoryControlsNow().admin?.extractionDisabled !== true;
}

/** Asks the server for the privacy controls (pause, retention, the admin switch) and brings the top bar in step. Quiet: a failure leaves what the settings say. */
async function llmaRefreshMemoryControls() {
    await llmaMemorySettings.loadControls();
}

/** Keeps the top bar's pause button and "don't remember this chat" button in step with the pause switches, the master switch and this chat's own opt-out. */
function llmaUpdateMemoryToggleBtn() {
    const pause = document.getElementById('llma-memory-pause-btn');
    if (pause) {
        const c = llmaMemoryControlsNow();
        const paused = c.pause?.saving === true || c.pause?.recall === true;
        pause.style.display = llmaMemoryExtractionOn() || paused ? '' : 'none';
        pause.classList.toggle('on', paused);
        pause.setAttribute('aria-pressed', String(paused));
        pause.title = paused ? translate('Memory is paused (saving and recall). Click to resume.') : translate('Pause memory: nothing is saved or recalled until you resume.');
    }
    const btn = document.getElementById('llma-memory-toggle-btn');
    if (!btn) return;
    btn.style.display = llmaMemoryExtractionOn() ? '' : 'none';
    const remembers = LLMAState.activeThreadMemoryExtraction !== false;
    btn.classList.toggle('off', !remembers);
    btn.setAttribute('aria-pressed', String(remembers));
    const slash = btn.querySelector('.llma-memory-toggle-slash');
    if (slash) slash.style.display = remembers ? 'none' : '';
    // Reassigned every time this runs: translate() directly, never class="translate".
    btn.title = remembers ? translate('Memory is saved from this chat. Click to stop.') : translate('This chat is not remembered. Click to remember it again.');
}

/** The top bar's pause button: pauses both saving and recall, or resumes both (the Memory tab has them as two switches). */
async function llmaToggleMemoryPause() {
    const c = llmaMemoryControlsNow();
    const paused = c.pause?.saving === true || c.pause?.recall === true;
    const reply = await llmaMemorySettings.setControlsOnServer({ savingPaused: !paused, recallPaused: !paused });
    if (reply) {
        llmaShowToast(paused ? 'Memory resumed.' : 'Memory paused: nothing is saved or recalled.', 'info');
        llmaMemorySettings.renderPause();
    }
}

/** Flips this chat's opt-out and saves it on the server. A chat that does not exist yet carries the choice over when it is created. */
async function llmaToggleThreadMemory() {
    const remember = LLMAState.activeThreadMemoryExtraction === false;
    if (!LLMAState.activeThreadId) {
        LLMAState.activeThreadMemoryExtraction = remember ? null : false;
        llmaUpdateMemoryToggleBtn();
        return;
    }
    try {
        const result = await llmaRequest('LLMAssistantSetThreadMemory', { threadId: LLMAState.activeThreadId, enabled: remember });
        if (!result?.success) {
            llmaShowToast(result?.error || 'Could not change this setting.', 'error');
            return;
        }
        LLMAState.activeThreadMemoryExtraction = remember ? null : false;
        llmaUpdateMemoryToggleBtn();
        llmaShowToast(remember ? 'Memory may be saved from this chat.' : 'This chat will not be remembered.', 'info');
    } catch (e) {
        llmaShowToast(llmaShortError(e) || 'Could not change this setting.', 'error');
    }
}


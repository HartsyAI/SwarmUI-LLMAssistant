/**
 * The Memory tab of the settings dialog: a read-only list of the notes the assistant has saved about the user (see the README, "Per-user memory"). Each note shows its kind, where it came
 * from (the user, the assistant, or the memory tool), when it was saved and whether it is pinned. Reviewing, editing, deleting and undoing come with a later phase; for now the list is
 * for seeing what is remembered. Built from Bootstrap's list-group and badge, so it carries no styling of its own.
 *
 * It also holds the settings of the automatic after-turn extractor (off by default; the model, the never-save list), the list of its recent changes with Undo, and the small notice shown in the
 * chat when it saves something (see LLMAMemoryNotice below). The full review, edit and delete screen is a later phase.
 */
class LLMAMemorySettings {

    /** The labels the slot notes (the ones there is only one of) are shown with. */
    static SLOT_LABELS = { name: 'name', pronouns: 'pronouns', bio: 'bio', currentWork: 'current work' };

    constructor() {
        /** The notes as the server sent them (in the order they appear in the prompt). */
        this.notes = [];
        /** The store's limits: { maxNotes, maxNoteChars, maxPinned, promptBudgetChars }. */
        this.limits = {};
        /** How the stored notes read: 'ok', 'lossy', 'corrupt' or 'newer'. */
        this.state = 'ok';
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

    /** The automatic extractor's settings as saved: { enabled, model, neverSave[] }. */
    extractionSettings() {
        let saved = (typeof LLMAState != 'undefined' && LLMAState.settings?.memoryExtraction) || {};
        return { enabled: saved.enabled === true, model: typeof saved.model == 'string' ? saved.model : '', neverSave: Array.isArray(saved.neverSave) ? saved.neverSave.filter(s => typeof s == 'string') : [] };
    }

    /** Draws the extractor's settings: the master switch (off by default), the model, and the never-save list. */
    renderExtraction() {
        let box = document.getElementById('llma-memory-extraction');
        if (!box) {
            return;
        }
        let s = this.extractionSettings();
        let models = ((typeof LLMAState != 'undefined' && LLMAState.availableModels) || []).map(m => m.id).slice(0, 500);
        let options = models.map(n => `<option value="${escapeHtml(n)}"></option>`).join('');
        box.innerHTML = `<h6>${translate('Save memories automatically')}</h6>`
            + `<label class="d-block mb-1"><input type="checkbox" id="llma-memx-enabled" ${s.enabled ? 'checked' : ''}> ${translate('After a reply, look for things worth remembering about me')}</label>`
            + `<div class="small text-muted mb-2">${translate('Off by default. When on, a turn where you say something about yourself costs one extra model call after the reply (at most 20 an hour). It reads only what you and the assistant wrote, never tool results, web pages or attachments, and a turn where the assistant used a tool is read from your messages alone. Every change is shown in the chat with an Undo.')}</div>`
            + `<div class="mb-2"><label class="small" for="llma-memx-model">${translate('Model that does it')}</label>`
            + `<input type="text" class="form-control" id="llma-memx-model" list="llma-memx-models" placeholder="${escapeHtml(translate('Empty: the model that answered the chat'))}" value="${escapeHtml(s.model)}" autocomplete="off"><datalist id="llma-memx-models">${options}</datalist>`
            + `<div class="small text-muted">${translate('Any model from any backend. A small one is enough; a local model that is busy generating will make the extraction wait.')}</div></div>`
            + `<div class="mb-2"><label class="small" for="llma-memx-never">${translate('Never save (one per line)')}</label>`
            + `<textarea class="form-control" rows="3" id="llma-memx-never" placeholder="${escapeHtml(translate('A word or phrase, or /regular expression/'))}">${escapeHtml(s.neverSave.join('\n'))}</textarea>`
            + `<div class="small text-muted">${translate('A turn that mentions one of these is not sent to the model at all. Passwords, keys and card numbers are always left out.')}</div></div>`
            + `<button type="button" class="basic-button btn-primary" id="llma-memx-save">${translate('Save')}</button> <span class="small text-muted" id="llma-memx-out"></span><hr>`;
        document.getElementById('llma-memx-save').addEventListener('click', () => this.saveExtraction());
    }

    /** Saves the extractor's settings in the user's own settings. */
    async saveExtraction() {
        let out = document.getElementById('llma-memx-out');
        let never = document.getElementById('llma-memx-never').value.split('\n').map(s => s.trim()).filter(s => s.length > 0);
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantSetMemorySettings', { enabled: document.getElementById('llma-memx-enabled').checked, model: document.getElementById('llma-memx-model').value.trim(), neverSave: never });
        }
        catch (err) {
            out.textContent = llmaShortError(err) || translate('That did not work.');
            return;
        }
        if (!reply?.success) {
            out.textContent = reply?.error || translate('That did not work.');
            return;
        }
        LLMAState.settings = LLMAState.settings || {};
        LLMAState.settings.memoryExtraction = reply.settings;
        llmaUpdateMemoryToggleBtn();
        // Drawn again from what the server kept (trimmed, de-duplicated), then said.
        this.renderExtraction();
        document.getElementById('llma-memx-out').textContent = translate('Saved.');
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
            html += `<li class="list-group-item bg-transparent llma-memory-change" data-change-id="${escapeHtml(set.id)}"><div class="d-flex justify-content-between gap-2"><div class="small">`
                + `<div class="text-muted">${escapeHtml(set.at ? new Date(set.at).toLocaleString() : '')}</div>${LLMAMemoryNotice.linesHtml(set)}</div>`
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

    /** Loads the notes from the server and draws them. */
    async load() {
        // The extractor's settings come from their own route (LLMAssistantGetMemorySettings) and are saved by their own route; the page's copy in LLMAState.settings is only a cache of what the server last said, and is never sent back with the general settings.
        try {
            let current = await llmaRequest('LLMAssistantGetMemorySettings', {});
            if (current?.success) {
                LLMAState.settings = LLMAState.settings || {};
                LLMAState.settings.memoryExtraction = current.settings;
            }
        }
        catch { /* the copy from the general settings is used */ }
        llmaUpdateMemoryToggleBtn();
        this.renderExtraction();
        this.loadChanges();
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
        this.render();
    }

    /** Draws the totals and the notes. */
    render() {
        let totals = document.getElementById('llma-memory-totals');
        totals.textContent = `${this.notes.length} / ${this.limits.maxNotes ?? 0} ${translate('notes')}`;
        let list = document.getElementById('llma-memory-list');
        let notice = LLMAMemorySettings.stateNotice(this.state);
        let noticeHtml = notice ? `<div class="alert alert-warning py-2 llma-memory-notice" data-state="${escapeHtml(this.state)}">${escapeHtml(translate(notice))}</div>` : '';
        if (this.notes.length == 0) {
            list.innerHTML = noticeHtml + (this.state != 'ok' ? '' : `<div class="text-muted py-2">${translate('Nothing is remembered yet. Ask the assistant to remember something, or tell it about yourself.')}</div>`);
            return;
        }
        let html = noticeHtml + '<ul class="list-group mb-2">';
        for (let note of this.notes) {
            let kind = note.negative ? 'dislike' : (note.slot ? (LLMAMemorySettings.SLOT_LABELS[note.slot] || note.slot) : note.kind);
            let pinned = note.pinned ? ` <span class="badge text-bg-info" title="${escapeHtml(translate('Always in the assistant\'s prompt.'))}">${translate('pinned')}</span>` : '';
            let saved = note.created ? new Date(note.created).toLocaleDateString() : '';
            html += `<li class="list-group-item bg-transparent llma-memory-note" data-note-id="${escapeHtml(note.id)}">`
                + `<span class="badge text-bg-secondary">${escapeHtml(translate(kind))}</span>${pinned} <span class="text-break">${escapeHtml(note.text)}${note.truncated ? '…' : ''}</span>`
                + `<br><small class="text-muted">${escapeHtml(translate(note.origin || ''))}${saved ? ` · ${escapeHtml(saved)}` : ''}</small></li>`;
        }
        list.innerHTML = html + '</ul>';
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
        // Only in the chat it belongs to; in another one it is not shown (it stays listed in Settings > Memory).
        if (set.threadId && typeof LLMAState != 'undefined' && LLMAState.activeThreadId && set.threadId != LLMAState.activeThreadId) {
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
        if (!(reply.undone > 0)) {
            // Every note was changed since (or cannot come back): nothing was undone, and the change stays listed.
            llmaShowToast('Nothing was undone: those notes have been changed since.', 'info');
            return false;
        }
        llmaShowToast(reply.skipped > 0 ? 'Undone, except notes that were changed since.' : 'Undone.', 'success');
        return true;
    }
}

let llmaMemorySettings = new LLMAMemorySettings();

/** Closes the chat socket that stays open after a reply to carry the memory notice (the server stops an extraction when it closes). Called when another chat is opened. */
function llmaCloseLingeringSocket() {
    const socket = LLMAState._lingerSocket;
    LLMAState._lingerSocket = null;
    if (socket && socket !== LLMAState._activeSocket) {
        try { socket.close(); } catch (_) { /* already closed */ }
    }
}

/** Whether the automatic extractor is switched on in the user's own settings (the per-chat button is only shown then). */
function llmaMemoryExtractionOn() {
    return typeof LLMAState != 'undefined' && LLMAState.settings?.memoryExtraction?.enabled === true;
}

/** Keeps the top bar's "don't remember this chat" button in step with the master switch and this chat's own opt-out. */
function llmaUpdateMemoryToggleBtn() {
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


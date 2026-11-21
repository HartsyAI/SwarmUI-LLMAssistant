/**
 * The Memory tab of the settings dialog: a read-only list of the notes the assistant has saved about the user (see the README, "Per-user memory"). Each note shows its kind, where it came
 * from (the user, the assistant, or the memory tool), when it was saved and whether it is pinned. Reviewing, editing, deleting and undoing come with a later phase; for now the list is
 * for seeing what is remembered. Built from Bootstrap's list-group and badge, so it carries no styling of its own.
 */
class LLMAMemorySettings {

    constructor() {
        /** The notes as the server sent them (in the order they appear in the prompt). */
        this.notes = [];
        /** The store's limits: { maxNotes, maxNoteChars, maxPinned, promptBudgetChars }. */
        this.limits = {};
    }

    /** Loads the notes from the server and draws them. */
    async load() {
        let list = document.getElementById('llma-memory-list');
        if (!list) {
            return;
        }
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantGetUserProfile', {});
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
        this.render();
    }

    /** Draws the totals and the notes. */
    render() {
        let totals = document.getElementById('llma-memory-totals');
        totals.textContent = `${this.notes.length} / ${this.limits.maxNotes ?? 0} ${translate('notes')}`;
        let list = document.getElementById('llma-memory-list');
        if (this.notes.length == 0) {
            list.innerHTML = `<div class="text-muted py-2">${translate('Nothing is remembered yet. Ask the assistant to remember something, or tell it about yourself.')}</div>`;
            return;
        }
        let html = '<ul class="list-group mb-2">';
        for (let note of this.notes) {
            let kind = note.negative ? 'dislike' : (note.slot ? note.slot : note.kind);
            let pinned = note.pinned ? ` <span class="badge text-bg-info" title="${escapeHtml(translate('Always in the assistant\'s prompt.'))}">${translate('pinned')}</span>` : '';
            let saved = note.created ? new Date(note.created).toLocaleDateString() : '';
            html += `<li class="list-group-item bg-transparent llma-memory-note" data-note-id="${escapeHtml(note.id)}">`
                + `<span class="badge text-bg-secondary">${escapeHtml(translate(kind))}</span>${pinned} <span class="text-break">${escapeHtml(note.text)}</span>`
                + `<br><small class="text-muted">${escapeHtml(translate(note.origin || ''))}${saved ? ` · ${escapeHtml(saved)}` : ''}</small></li>`;
        }
        list.innerHTML = html + '</ul>';
    }
}

let llmaMemorySettings = new LLMAMemorySettings();

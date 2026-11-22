/**
 * The Memory tab of the settings dialog: a read-only list of the notes the assistant has saved about the user (see the README, "Per-user memory"). Each note shows its kind, where it came
 * from (the user, the assistant, or the memory tool), when it was saved and whether it is pinned. Reviewing, editing, deleting and undoing come with a later phase; for now the list is
 * for seeing what is remembered. Built from Bootstrap's list-group and badge, so it carries no styling of its own.
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

    /** Loads the notes from the server and draws them. */
    async load() {
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

let llmaMemorySettings = new LLMAMemorySettings();

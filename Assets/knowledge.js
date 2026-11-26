/**
 * The Knowledge tab of the settings dialog: the user's own documents (see the README, "Knowledge library"), added from files (text is read in the browser; PDF and Word files are sent as
 * they are and read on the server), listed with how each is
 * searchable, deleted, and tried with a search box that runs the same search the assistant's tool does. Also where the embedding model is chosen: a backend that can embed and a model id,
 * tried when saved. With none chosen, documents are searched by keywords alone and nothing is sent anywhere. Built from Bootstrap's list-group, badge and alert and SwarmUI's make*Input
 * helpers (as the other settings forms are), so it carries no styling of its own.
 */
class LLMAKnowledgeSettings {

    constructor() {
        /** The documents as the server sent them. */
        this.documents = [];
        /** The embedding setting: { providerId, model }. */
        this.settings = { providerId: '', model: '' };
        /** The backends that can embed: [{id, name}]. */
        this.providers = [];
        /** The library's limits and totals. */
        this.limits = {};
        this.totals = {};
    }

    /** Loads the library from the server and draws it. */
    async load() {
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantGetKnowledge', {});
        }
        catch {
            reply = null;
        }
        let list = document.getElementById('llma-knowledge-list');
        if (!reply || !reply.success) {
            if (list) {
                list.innerHTML = `<div class="text-muted py-2">${translate('You do not have permission to use the knowledge library.')}</div>`;
            }
            for (let id of ['llma-knowledge-add', 'llma-knowledge-embedding', 'llma-knowledge-search']) {
                let el = document.getElementById(id);
                if (el) {
                    el.style.display = 'none';
                }
            }
            return;
        }
        this.documents = reply.documents || [];
        this.settings = reply.settings || this.settings;
        this.providers = reply.providers || [];
        this.limits = reply.limits || {};
        this.totals = reply.totals || {};
        for (let id of ['llma-knowledge-add', 'llma-knowledge-embedding', 'llma-knowledge-search']) {
            let el = document.getElementById(id);
            if (el) {
                el.style.display = '';
            }
        }
        this.renderList();
        this.renderEmbedding();
        this.renderSearch();
    }

    /** Draws the totals and the list of documents. */
    renderList() {
        let totals = document.getElementById('llma-knowledge-totals');
        totals.textContent = `${this.totals.documents ?? 0} ${translate('documents')} · ${this.totals.chunks ?? 0} / ${this.limits.chunks ?? 0} ${translate('pieces of text')}`;
        let list = document.getElementById('llma-knowledge-list');
        if (this.documents.length == 0) {
            list.innerHTML = `<div class="text-muted py-2">${translate('No documents yet. Add files (PDF, Word, notes, manuals, Markdown, code) and the assistant can search them when its Search Knowledge tool is switched on.')}</div>`;
            return;
        }
        let html = '<ul class="list-group mb-2">';
        for (let doc of this.documents) {
            let state = doc.embedded
                ? `<span class="badge text-bg-success" title="${escapeHtml(translate('Searched by meaning as well as by words.'))}">${translate('Meaning + words')}</span>`
                : `<span class="badge text-bg-secondary" title="${escapeHtml(translate('Searched by words only.'))}">${translate('Words only')}</span>`;
            let problem = doc.embedError ? `<br><small class="text-warning">${escapeHtml(translate('Not embedded'))}: ${escapeHtml(doc.embedError)}</small>` : '';
            html += `<li class="list-group-item d-flex flex-wrap align-items-center gap-2 bg-transparent" data-doc-id="${escapeHtml(doc.id)}">`
                + `<span class="llma-schedule-text text-break"><b>${escapeHtml(doc.title)}</b> ${state}`
                + `<br><small class="text-muted">${escapeHtml(doc.fileName)} · ${Number(doc.chars).toLocaleString()} ${translate('characters')} · ${doc.chunks} ${translate('pieces')}</small>${problem}</span>`
                + `<button type="button" class="basic-button ms-auto llma-knowledge-delete">${translate('Delete')}</button></li>`;
        }
        html += '</ul>';
        list.innerHTML = html;
        for (let row of list.querySelectorAll('li')) {
            row.querySelector('.llma-knowledge-delete').addEventListener('click', () => this.remove(this.documents.find(d => d.id == row.dataset.docId)));
        }
    }

    /** Opens the file chooser. */
    chooseFiles() {
        document.getElementById('llma-knowledge-files').click();
    }

    /** What the add route is sent for a file: PDF and Word files as they are (the server reads them), anything else as its text. */
    async fileRequest(file) {
        if (!/\.(pdf|docx?)$/i.test(file.name)) {
            return { name: file.name, text: await file.text() };
        }
        if (file.size > llmaChatDocuments.cap('documentMaxMb') * 1024 * 1024) {
            throw new Error(`${translate('The limit in MB is')} ${llmaChatDocuments.cap('documentMaxMb')}.`);
        }
        return { name: file.name, data: await llmaFileToBase64(file) };
    }

    /** Adds the chosen files, one request each. */
    async addFiles(files) {
        let added = 0;
        let warnings = [];
        let list = [...files];
        for (let i = 0; i < list.length; i++) {
            let file = list[i];
            llmaShowToast(`${translate('Adding')} ${i + 1} / ${list.length}: ${file.name}`, 'info');
            let request = null;
            try {
                request = await this.fileRequest(file);
            }
            catch (err) {
                llmaShowToast(`${translate('Could not read')} ${file.name}. ${llmaShortError(err) || ''}`, 'error');
                continue;
            }
            let reply = await llmaUserAction(() => llmaRequest('LLMAssistantAddKnowledge', request), `${translate('Could not add')} ${file.name}`);
            if (reply) {
                added++;
                warnings.push(...(reply.warnings || []).map(w => `${file.name}: ${w}`));
            }
        }
        document.getElementById('llma-knowledge-files').value = '';
        if (added > 0) {
            llmaShowToast([`${translate('Added')} ${added} ${added == 1 ? translate('document') : translate('documents')}.`, ...warnings].join(' '), 'info');
        }
        await this.load();
    }

    /** Deletes a document. */
    async remove(doc) {
        if (!confirm(`${translate('Delete')} "${doc.title}"?`)) {
            return;
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantDeleteKnowledge', { docId: doc.id }), translate('Could not delete'));
        if (reply) {
            await this.load();
        }
    }

    /** Draws the embedding model setting. */
    renderEmbedding() {
        let box = document.getElementById('llma-knowledge-embedding');
        let forms = new LLMASettingsForms();
        let ids = ['', ...this.providers.map(p => p.id)];
        let names = [translate('None (search by words only)'), ...this.providers.map(p => p.name)];
        let current = ids.includes(this.settings.providerId) ? this.settings.providerId : '';
        box.innerHTML = forms.group('llma_know_embed', translate('Search by meaning (optional)'), translate('With an embedding model, documents are also searched by meaning, so a question finds text that does not use the same words. Adding a document then sends its text to that backend. Without one, nothing is sent anywhere and documents are searched by words.'), [
            forms.dropdown('llma-know-provider', 'Backend', 'A backend that can embed text. Claude cannot.', ids, current, names),
            forms.text('llma-know-model', 'Embedding model', 'The model id as the backend knows it, for example text-embedding-3-small, or nomic-embed-text on Ollama.', this.settings.model || '', 'normal'),
        ]) + `<div class="d-flex flex-wrap gap-2 mt-2 align-items-center"><button type="button" class="basic-button btn-primary" id="llma-know-save">${translate('Save and try it')}</button>`
            + `<button type="button" class="basic-button" id="llma-know-reindex">${translate('Embed all documents again')}</button></div>`
            + '<div id="llma-know-embed-out" class="small text-muted mt-2"></div>';
        document.getElementById('llma-know-save').addEventListener('click', () => this.saveEmbedding());
        document.getElementById('llma-know-reindex').addEventListener('click', () => this.reindex());
    }

    /** Saves the embedding setting; the server tries the model first and says why if it does not work. */
    async saveEmbedding() {
        let out = document.getElementById('llma-know-embed-out');
        out.textContent = translate('Trying the model...');
        let providerId = document.getElementById('llma-know-provider').value;
        let model = providerId ? document.getElementById('llma-know-model').value.trim() : '';
        try {
            let reply = await llmaRequest('LLMAssistantSetKnowledgeSettings', { providerId: providerId, model: model });
            out.textContent = reply.dimension ? `${translate('It works: vectors have')} ${reply.dimension} ${translate('numbers. Documents added from now on are embedded; use "Embed all documents again" for the ones already here.')}` : translate('Embedding is off. Documents are searched by words.');
            this.settings = { providerId: providerId, model: model };
        }
        catch (err) {
            out.textContent = llmaShortError(err) || translate('That did not work.');
        }
    }

    /** Embeds every document again with the current setting. */
    async reindex() {
        let out = document.getElementById('llma-know-embed-out');
        out.textContent = translate('Embedding...');
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantReindexKnowledge', {}), translate('Could not embed'));
        if (reply) {
            // The list and the setting are drawn again first (what each document is now), and then the result is shown in the freshly drawn box.
            await this.load();
            document.getElementById('llma-know-embed-out').textContent = `${reply.embedded} ${translate('embedded')}, ${reply.failed} ${translate('failed')}.`;
        }
    }

    /** Draws the search box. */
    renderSearch() {
        let box = document.getElementById('llma-knowledge-search');
        if (box.dataset.built) {
            return;
        }
        box.dataset.built = 'true';
        box.innerHTML = `<h6 class="mt-3">${translate('Try a search')}</h6>`
            + `<div class="d-flex gap-2"><input type="text" class="form-control" id="llma-know-query" placeholder="${escapeHtml(translate('What would the assistant look for?'))}" autocomplete="off">`
            + `<button type="button" class="basic-button" id="llma-know-search">${translate('Search')}</button></div>`
            + '<div id="llma-know-results" class="mt-2"></div>';
        document.getElementById('llma-know-search').addEventListener('click', () => this.search());
        document.getElementById('llma-know-query').addEventListener('keydown', (e) => {
            if (e.key == 'Enter') {
                e.preventDefault();
                this.search();
            }
        });
    }

    /** Runs the search and shows what the assistant's tool would get. */
    async search() {
        let out = document.getElementById('llma-know-results');
        let query = document.getElementById('llma-know-query').value.trim();
        if (!query) {
            return;
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantSearchKnowledge', { query: query, limit: 5 }), translate('Search failed'));
        if (!reply) {
            return;
        }
        if (reply.results.length == 0) {
            out.innerHTML = `<div class="text-muted">${translate('Nothing matched.')}</div>`;
            return;
        }
        let html = `<div class="small text-muted mb-1">${translate(reply.mode == 'hybrid' ? 'Searched by meaning and words.' : 'Searched by words.')}${reply.embedError ? ` ${escapeHtml(translate('Meaning search failed'))}: ${escapeHtml(reply.embedError)}` : ''}</div><ul class="list-group">`;
        for (let r of reply.results) {
            let place = [r.page > 0 ? `${translate('page')} ${r.page}` : '', r.heading || ''].filter(p => p).join(' · ');
            let heading = place ? ` <small class="text-muted">${escapeHtml(place)}</small>` : '';
            let how = `${r.keyword ? `<span class="badge text-bg-secondary">${translate('words')}</span> ` : ''}${r.semantic ? `<span class="badge text-bg-success">${translate('meaning')}</span>` : ''}`;
            let text = r.text.length > 320 ? r.text.slice(0, 320) + '…' : r.text;
            html += `<li class="list-group-item bg-transparent"><b>${escapeHtml(r.title)}</b>${heading} ${how}<br><small class="text-break">${escapeHtml(text)}</small></li>`;
        }
        out.innerHTML = html + '</ul>';
    }
}

let llmaKnowledgeSettings = new LLMAKnowledgeSettings();

/** Wires the Knowledge tab's own buttons. Called once when the settings dialog is set up. */
function llmaSetupKnowledgeTab() {
    document.getElementById('llma-knowledge-add')?.addEventListener('click', () => llmaKnowledgeSettings.chooseFiles());
    document.getElementById('llma-knowledge-files')?.addEventListener('change', (e) => llmaKnowledgeSettings.addFiles(e.target.files));
}

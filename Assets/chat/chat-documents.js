/**
 * Documents attached to chat messages (see the README, "Documents in chats"): the chips above the composer, uploading a file as soon as it is attached so a file that cannot be read says
 * so at once, the chips on a sent message, and the caps on the General settings tab. The server reads the file and keeps its text; the browser only ever holds the reference it returns.
 */
class LLMAChatDocuments {

    constructor() {
        /** Files attached but not yet sent: {key, thread, name, reference}; thread is the chat they were attached in (null for one not started yet), and reference is null while the
         *  server reads the file. */
        this.pending = [];
        /** Counts attached files, to tell them apart. */
        this.nextKey = 1;
        /** The extensions the attach button offers. The server decides what it can read. */
        this.extensions = ['pdf', 'docx', 'txt', 'md', 'markdown', 'csv', 'tsv', 'json', 'jsonl', 'log', 'html', 'htm', 'xml', 'yaml', 'yml', 'toml', 'ini', 'rst', 'tex',
            'js', 'ts', 'py', 'cs', 'java', 'go', 'rs', 'c', 'cpp', 'h', 'sh', 'sql'];
        /** The caps' settings: [setting name, field id, label, help, default, min, max]. */
        this.settingFields = [
            ['documentMaxMb', 'llma-s-doc-max-mb', 'Largest File (MB)', 'The largest document that can be attached to a chat or added to the knowledge library.', 10, 1, 32],
            ['documentMaxPages', 'llma-s-doc-max-pages', 'Most PDF Pages', 'Pages after this many are left out of a PDF, and the result says so.', 200, 1, 1000],
            ['documentMaxChars', 'llma-s-doc-max-chars', 'Most Characters Per Attachment', 'Text kept from one attached file. A longer file is cut, with a note to the model. The knowledge library has its own limit and refuses instead.', 200000, 1000, 1000000],
            ['documentMaxPerMessage', 'llma-s-doc-per-message', 'Most Documents Per Message', 'How many documents one message can carry.', 5, 1, 20],
            ['documentContextChars', 'llma-s-doc-context-chars', 'Document Characters Per Reply', 'The most document text sent with one request, counted as plain English (about four characters a token; digits count double, other scripts four times). Never more of the model\'s context window than half of the Shorten At share. Newer attachments come first.', 60000, 1000, 1000000]
        ];
    }

    /** The accept list for the file chooser. */
    accept() {
        return this.extensions.map(e => `.${e}`).join(',');
    }

    /** Whether a file is one to attach as a document (an old .doc is too, so the server can say how to get it read). */
    isDocument(file) {
        let name = (file?.name || '').toLowerCase();
        let extension = name.includes('.') ? name.split('.').pop() : '';
        if (this.extensions.includes(extension) || extension == 'doc') {
            return true;
        }
        let type = (file?.type || '').toLowerCase();
        return type.startsWith('text/') || type == 'application/pdf';
    }

    /** One of the caps, from the settings. */
    cap(setting) {
        let field = this.settingFields.find(f => f[0] == setting);
        let value = parseInt(LLMAState.settings?.parameters?.[setting], 10);
        return value > 0 ? Math.min(Math.max(value, field[5]), field[6]) : field[4];
    }

    /** The chat open now, or null before one is started. */
    thread() {
        return LLMAState.activeThreadId || null;
    }

    /** The files waiting in a chat's composer. */
    waiting(thread = this.thread()) {
        return this.pending.filter(e => e.thread == thread);
    }

    /** Attaches a file: shows its chip, has the server read it, and keeps the reference, or says why it cannot be attached. */
    async add(file) {
        if (this.waiting().length >= this.cap('documentMaxPerMessage')) {
            llmaShowToast(`${translate('A message can carry at most this many documents')}: ${this.cap('documentMaxPerMessage')}`, 'error');
            return;
        }
        if (file.size == 0) {
            llmaShowToast(`${file.name}: ${translate('That file is empty.')}`, 'error');
            return;
        }
        if (file.size > this.cap('documentMaxMb') * 1024 * 1024) {
            llmaShowToast(`${file.name} ${translate('is too large')} (${this.formatBytes(file.size)}). ${translate('The limit in MB is')} ${this.cap('documentMaxMb')}.`, 'error');
            return;
        }
        let entry = { key: this.nextKey++, thread: this.thread(), name: file.name, reference: null };
        this.pending.push(entry);
        this.renderPending();
        let reply = null;
        let problem = null;
        try {
            reply = await llmaRequest('LLMAssistantUploadChatDocument', { name: file.name, data: await llmaFileToBase64(file) });
        }
        catch (err) {
            problem = llmaShortError(err);
        }
        if (!this.pending.includes(entry)) {
            // Removed while it was being read.
            return;
        }
        if (!reply?.success) {
            this.remove(entry.key);
            llmaShowToast(`${file.name}: ${problem || reply?.error || translate('The file could not be read.')}`, 'error');
            return;
        }
        entry.reference = reply.document;
        this.renderPending();
        if (reply.document.warnings?.length > 0) {
            llmaShowToast(`${file.name}: ${reply.document.warnings.join(' ')}`, 'info');
        }
    }

    /** Removes a file from the composer. */
    remove(key) {
        this.pending = this.pending.filter(e => e.key != key);
        this.renderPending();
    }

    /** Whether anything is attached to the composer of the chat open now. */
    hasPending() {
        return this.waiting().length > 0;
    }

    /** Whether a file of the chat open now is still being read. */
    isReading() {
        return this.waiting().some(e => !e.reference);
    }

    /** Takes a chat's files that are ready off the composer and returns their references, or null when there are none. */
    take(thread = this.thread()) {
        let ready = this.waiting(thread).filter(e => e.reference);
        this.pending = this.pending.filter(e => !ready.includes(e));
        this.renderPending();
        return ready.length > 0 ? ready.map(e => e.reference) : null;
    }

    /** Draws the chips above the composer: those of the chat open now. */
    renderPending() {
        let box = document.getElementById('llma-document-chips');
        if (!box) {
            return;
        }
        let waiting = this.waiting();
        box.innerHTML = '';
        box.style.display = waiting.length > 0 ? '' : 'none';
        for (let entry of waiting) {
            let chip = this.chip(entry.reference || { name: entry.name }, entry.reference ? this.describe(entry.reference) : translate('Reading…'));
            chip.classList.toggle('llma-doc-chip-reading', !entry.reference);
            let remove = llmaCreateActionBtn('×', () => this.remove(entry.key), 'llma-doc-chip-remove');
            remove.type = 'button';
            remove.title = translate('Remove');
            remove.setAttribute('aria-label', `${translate('Remove')} ${entry.name}`);
            chip.appendChild(remove);
            box.appendChild(chip);
        }
    }

    /** A chip showing a document's name and a line about it. */
    chip(doc, about) {
        let chip = createDiv(null, 'llma-doc-chip');
        chip.title = `${doc.name} (${about})`;
        let name = document.createElement('span');
        name.className = 'llma-doc-chip-name';
        name.textContent = doc.name;
        let meta = document.createElement('span');
        meta.className = 'llma-doc-chip-meta';
        meta.textContent = about;
        chip.append(name, meta);
        return chip;
    }

    /** The line about a read document: its kind, pages, length, and whether it was cut. */
    describe(doc) {
        let parts = [(doc.kind || '').toUpperCase()];
        if (doc.pages > 0) {
            parts.push(`${doc.pages} ${translate(doc.pages == 1 ? 'page' : 'pages')}`);
        }
        parts.push(`${Number(doc.chars || 0).toLocaleString()} ${translate('characters')}`);
        if (doc.truncated) {
            parts.push(translate('cut short'));
        }
        return parts.join(' · ');
    }

    /** Puts a sent message's documents at the top of its bubble. */
    showInBubble(bubble, documents) {
        if (!Array.isArray(documents) || documents.length == 0) {
            return;
        }
        let list = createDiv(null, 'llma-msg-documents');
        for (let doc of documents) {
            list.appendChild(this.chip(doc, this.describe(doc)));
        }
        bubble.prepend(list);
    }

    /** About how many tokens the documents on these messages add to a request: never more than a request carries, however many there are (as the server works it out). */
    tokens(messages) {
        let cost = 0;
        for (let message of messages || []) {
            for (let doc of message.documents || []) {
                cost += 400 + Number(doc.cost ?? doc.chars ?? 0);
            }
        }
        if (cost == 0) {
            return 0;
        }
        let budget = this.cap('documentContextChars');
        let window = llmaContext.windowTokens();
        if (window > 0) {
            let shortenAt = Number(LLMAState.settings?.parameters?.compactThreshold);
            shortenAt = shortenAt > 0 && shortenAt <= 1 ? shortenAt : 0.8;
            budget = Math.min(budget, Math.max(1000, Math.floor(window * 2 * shortenAt)));
        }
        return Math.ceil(Math.min(cost, budget) / 4);
    }

    /** "12.3 MB" style size text. */
    formatBytes(n) {
        return n >= 1024 * 1024 ? `${(n / (1024 * 1024)).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`;
    }

    /** The "Documents" section of the General settings form. */
    settingsGroup(forms) {
        return forms.group('llma_general_documents', 'Documents', 'Files attached to a chat (PDF with a text layer, Word .docx, web pages and text) are read on the server, and their text is sent to the model as quoted data. Scanned pages are not read: there is no OCR.',
            this.settingFields.map(f => forms.number(f[1], f[2], f[3], f[4], f[5], f[6], 1)));
    }

    /** Copies the caps from the settings dialog into the settings. */
    readSettings(parameters) {
        for (let field of this.settingFields) {
            let value = llmaReadBoundedInt(field[1]);
            parameters[field[0]] = value > 0 ? value : field[4];
        }
    }

    /** Shows the settings' caps in the dialog. */
    writeSettings(parameters) {
        for (let field of this.settingFields) {
            llmaSetEl(field[1], parameters?.[field[0]] ?? field[4]);
        }
    }
}

let llmaChatDocuments = new LLMAChatDocuments();

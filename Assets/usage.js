/**
 * Tokens and cost in the chat (see the README, "Usage and cost"): a small figure under each reply and one for the whole chat in the context bar, each opening a popover with the
 * details, plus the soft budget warning. The server does the counting and the arithmetic (`LLMAssistantGetThreadUsage`); this only draws what it says. `~` marks a figure estimated
 * from text length because the backend reported nothing, and a model with no price in the user's table shows tokens alone.
 */
class LLMAUsage {

    constructor() {
        /** The server's last reply for a chat: {threadId, currency, replies, totals, budget}. */
        this.data = null;
        /** The pending refresh timer. */
        this.timer = null;
        /** Counts requests, so a slow reply cannot replace a newer one. */
        this.sequence = 0;
        /** Counts popover openings, so totals that arrive late are not drawn into another popover. */
        this.popoverSequence = 0;
        /** Budget warnings already shown as a toast on this page. */
        this.warned = new Set();
        /** When this browser session began (see sessionStart). */
        this.started = null;
        // The session begins when the page does, not when its total is first looked at.
        this.sessionStart();
    }

    /** The browser's IANA time zone, which places "today" and "this week". */
    timeZone() {
        try {
            return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
        }
        catch {
            return 'UTC';
        }
    }

    /** When this browser session began (kept for the tab, so a reload does not restart the session's total). */
    sessionStart() {
        if (!this.started) {
            let key = 'llma_usage_session_start';
            this.started = new Date().toISOString();
            try {
                this.started = sessionStorage.getItem(key) || this.started;
                sessionStorage.setItem(key, this.started);
            }
            catch {
                // Storage can be blocked; the session is then this page load.
            }
        }
        return this.started;
    }

    /** An amount with its currency label. Small amounts keep enough digits to be told apart. */
    money(value, currency) {
        let amount = Number(value);
        if (amount > 0 && amount < 0.0001) {
            return `<0.0001 ${currency}`;
        }
        let digits = amount > 0 && amount < 1 ? 4 : 2;
        return `${amount.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits })} ${currency}`;
    }

    /** A token count for display. */
    count(value) {
        return Number(value || 0).toLocaleString();
    }

    /** Asks for the open chat's figures shortly, folding together the several redraws one change causes. */
    scheduleRefresh(delayMs = 150) {
        clearTimeout(this.timer);
        this.timer = setTimeout(() => this.refresh(), delayMs);
    }

    /** Asks again after a Stop: the server saves a stopped reply a moment after the socket closes. A timer of its own, so an ordinary redraw cannot cancel it. */
    refreshAfterStop() {
        setTimeout(() => this.scheduleRefresh(), 1500);
    }

    /** Fetches the open chat's figures and draws them. A chat that cannot be read simply shows none. */
    async refresh() {
        let threadId = LLMAState.activeThreadId;
        let sequence = ++this.sequence;
        let reply = null;
        if (threadId && (LLMAState.messages || []).length > 0) {
            try {
                reply = await llmaRequest('LLMAssistantGetThreadUsage', { threadId: threadId, timeZone: this.timeZone() });
            }
            catch {
                reply = null;
            }
        }
        if (sequence != this.sequence) {
            return;
        }
        this.data = reply?.success ? reply : null;
        this.render();
        this.warn();
    }

    /** Draws everything from the figures already fetched (call after the messages are redrawn). */
    render() {
        let data = this.data && this.data.threadId == LLMAState.activeThreadId ? this.data : null;
        let shown = data && LLMAState.showTokens;
        let drawn = new Set();
        let added = false;
        let list = document.getElementById('llma-messages');
        let atBottom = !!list && list.scrollHeight - list.scrollTop - list.clientHeight < 8;
        if (shown) {
            for (let reply of data.replies) {
                let meta = document.querySelector(`#llma-messages [data-msg-id="${CSS.escape(reply.messageId)}"] .llma-msg-meta`);
                if (!meta) {
                    continue;
                }
                let chip = meta.parentElement.querySelector('.llma-usage-chip');
                if (!chip) {
                    chip = document.createElement('button');
                    chip.type = 'button';
                    chip.className = 'llma-usage-chip';
                    chip.title = translate('Tokens and cost of this reply');
                    meta.after(chip);
                    added = true;
                }
                chip.textContent = this.replyLabel(reply, data.currency);
                chip.onclick = () => this.openReply(chip, reply, data.currency);
                drawn.add(chip);
            }
        }
        for (let chip of document.querySelectorAll('#llma-messages .llma-usage-chip')) {
            if (!drawn.has(chip)) {
                chip.remove();
            }
        }
        this.renderThread(shown ? data : null);
        this.renderWarning(data);
        // A new line under the last reply must not hide behind the composer. A view anywhere else (a search result, earlier text) stays where it is.
        if (added && atBottom) {
            llmaScrollToBottom();
        }
    }

    /** The one-line figure under a reply, e.g. "1,200 tok · 0.0042 USD". */
    replyLabel(reply, currency) {
        let mark = reply.estimated ? '~' : '';
        let parts = [`${mark}${this.count(reply.promptTokens + reply.completionTokens)} ${translate('tok')}`];
        if (reply.cost !== null) {
            parts.push(`${mark}${this.money(reply.cost, currency)}`);
        }
        if (reply.estimated) {
            parts.push(translate('estimated'));
        }
        if (reply.partial) {
            parts.push(translate('incomplete'));
        }
        return parts.join(' · ');
    }

    /** The chat's own figure in the context bar. */
    renderThread(data) {
        let button = document.getElementById('llma-usage-thread');
        if (!button) {
            return;
        }
        if (!data || data.totals.replies == 0) {
            button.style.display = 'none';
            return;
        }
        let totals = data.totals;
        let mark = totals.estimatedReplies > 0 ? '~' : '';
        let text = `${translate('Chat')}: ${mark}${this.count(totals.totalTokens)} ${translate('tok')}`;
        if (totals.cost !== null) {
            text += ` · ${totals.costEstimated ? '~' : ''}${this.money(totals.cost, data.currency)}`;
            if (totals.unpricedReplies > 0) {
                text += ` (${translate('priced replies only')})`;
            }
        }
        button.textContent = text;
        button.style.display = '';
    }

    /** The budget badge: present while a limit is reached. It informs and blocks nothing. */
    renderWarning(data) {
        let badge = document.getElementById('llma-usage-warning');
        if (!badge) {
            return;
        }
        let warnings = data?.budget?.warnings || [];
        badge.style.display = warnings.length > 0 ? '' : 'none';
        document.querySelector('.llma-context-bar')?.classList.toggle('llma-over-budget', warnings.length > 0);
        if (warnings.length > 0) {
            badge.textContent = warnings.length > 1 ? translate('Over budget') : (warnings[0].period == 'day' ? translate('Over daily budget') : translate('Over weekly budget'));
            badge.title = warnings.map(w => this.warningText(w, data.currency)).join('\n');
        }
    }

    /** One budget warning as a sentence. */
    warningText(warning, currency) {
        let period = warning.period == 'day' ? translate('Today') : translate('This week');
        let mark = warning.estimated ? '~' : '';
        return `${period}: ${mark}${this.money(warning.spent, currency)} ${translate('of a budget of')} ${this.money(warning.limit, currency)}. ${translate('This is only a warning; nothing is blocked.')}`;
    }

    /** Says once per period and day that a limit was reached. */
    warn() {
        for (let warning of this.data?.budget?.warnings || []) {
            let key = `${warning.period}|${warning.limit}|${new Date().toDateString()}`;
            if (!this.warned.has(key)) {
                this.warned.add(key);
                llmaShowToast(this.warningText(warning, this.data.currency), 'info');
            }
        }
    }

    /** One label and value of a popover's table. */
    row(label, value) {
        return `<tr><th scope="row">${escapeHtml(translate(label))}</th><td>${escapeHtml(value)}</td></tr>`;
    }

    /** The popover for one reply. */
    openReply(anchor, reply, currency) {
        let mark = reply.estimated ? '~' : '';
        let rows = this.row('Model', reply.model || translate('(the backend\'s default)'))
            + this.row('Input tokens', `${mark}${this.count(reply.promptTokens)}`)
            + (reply.cachedTokens > 0 ? this.row('of which cached', this.count(reply.cachedTokens)) : '')
            + this.row('Output tokens', `${mark}${this.count(reply.completionTokens)}`);
        if (reply.price) {
            let cached = reply.price.cached === null ? '' : `, ${reply.price.cached} ${translate('cached')}`;
            rows += this.row('Price per million', `${reply.price.input} ${translate('in')}, ${reply.price.output} ${translate('out')}${cached} (${currency})`)
                + this.row('Cost', `${mark}${this.money(reply.cost, currency)}`);
        }
        else {
            rows += this.row('Cost', translate('No price set for this model'));
        }
        let source = reply.estimated
            ? translate('Estimated from the length of the saved text, because the backend reported no usage. The system prompt, tool definitions and attachments are not counted, so it is usually low.')
            : translate('Counted by the backend.');
        if (reply.partial) {
            source += ' ' + translate('Some model rounds of this turn reported nothing, so the real figure is higher.');
        }
        let pop = this.show(anchor, `<b>${escapeHtml(translate('This reply'))}</b><table class="llma-usage-table">${rows}</table><div class="llma-usage-note">${escapeHtml(source)}</div>`
            + `<button type="button" class="basic-button llma-usage-edit">${escapeHtml(translate('Edit prices'))}</button>`);
        pop.querySelector('.llma-usage-edit').addEventListener('click', () => this.openSettings(reply.price ? '' : reply.model));
    }

    /** The popover for the chat: its own figures, then this session's, today's and this week's. */
    async openThread(anchor) {
        let data = this.data;
        if (!data) {
            return;
        }
        let totals = data.totals;
        let mark = totals.estimatedReplies > 0 ? '~' : '';
        let rows = this.row('Replies', this.count(totals.replies))
            + this.row('Input tokens', `${mark}${this.count(totals.promptTokens)}`)
            + (totals.cachedTokens > 0 ? this.row('of which cached', this.count(totals.cachedTokens)) : '')
            + this.row('Output tokens', `${mark}${this.count(totals.completionTokens)}`)
            + this.row('Cost', totals.cost === null ? translate('No price set') : `${totals.costEstimated ? '~' : ''}${this.money(totals.cost, data.currency)}`);
        let warnings = (data.budget?.warnings || []).map(w => `<div class="alert alert-warning py-1 px-2 my-1">${escapeHtml(this.warningText(w, data.currency))}</div>`).join('');
        let pop = this.show(anchor, `<b>${escapeHtml(translate('This chat'))}</b><table class="llma-usage-table">${rows}</table>`
            + `<div class="llma-usage-note">${escapeHtml([translate('Every branch of the chat is counted.'), ...this.notes(totals)].join(' '))}</div>${warnings}`
            + `<div class="llma-usage-periods">${escapeHtml(translate('Loading totals...'))}</div>`
            + `<button type="button" class="basic-button llma-usage-edit">${escapeHtml(translate('Prices and budget'))}</button>`);
        pop.querySelector('.llma-usage-edit').addEventListener('click', () => this.openSettings(''));
        let sequence = this.popoverSequence;
        let html = await this.periodsHtml();
        if (sequence == this.popoverSequence && pop.dataset.visible == 'true') {
            pop.querySelector('.llma-usage-periods').innerHTML = html;
            this.place(pop, anchor);
        }
    }

    /** What a set of totals leaves out or guesses, as sentences. */
    notes(totals) {
        let notes = [];
        if (totals.estimatedReplies > 0) {
            notes.push(`~ ${translate('Replies estimated because the backend reported nothing:')} ${totals.estimatedReplies}.`);
        }
        if (totals.partialReplies > 0) {
            notes.push(`${translate('Replies with incomplete counts, so the real figure is higher:')} ${totals.partialReplies}.`);
        }
        if (totals.cost !== null && totals.unpricedReplies > 0) {
            notes.push(`${translate('Replies of models with no price, left out of the cost:')} ${totals.unpricedReplies} (${this.count(totals.unpricedTokens)} ${translate('tok')}).`);
        }
        return notes;
    }

    /** The session, day and week totals as a table, or why they cannot be shown. */
    async periodsHtml() {
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantGetUsageTotals', { timeZone: this.timeZone(), since: this.sessionStart() });
        }
        catch (err) {
            return `<div class="llma-usage-note">${escapeHtml(llmaShortError(err) || translate('Totals could not be loaded.'))}</div>`;
        }
        let rows = '';
        let notes = [];
        for (let [key, label] of [['session', 'This session'], ['day', 'Today'], ['week', 'This week']]) {
            let totals = reply.periods?.[key];
            if (!totals) {
                continue;
            }
            let cost = totals.cost === null ? '-' : `${totals.costEstimated ? '~' : ''}${this.money(totals.cost, reply.currency)}${totals.unpricedReplies > 0 ? ' *' : ''}`;
            rows += `<tr data-period="${key}"><th scope="row">${escapeHtml(translate(label))}</th><td>${totals.estimatedReplies > 0 ? '~' : ''}${this.count(totals.totalTokens)} ${escapeHtml(translate('tok'))}</td><td>${escapeHtml(cost)}</td></tr>`;
            if (totals.cost !== null && totals.unpricedReplies > 0) {
                notes[0] = `* ${translate('Leaves out replies of models with no price.')}`;
            }
            if (totals.estimatedReplies > 0) {
                notes[1] = `~ ${translate('Includes estimates.')}`;
            }
        }
        notes.push(translate('All your chats, including sub-sessions and scheduled runs. Weeks start on Monday. Deleted chats and background requests such as titles and summaries are not counted.'));
        return `<table class="llma-usage-table">${rows}</table><div class="llma-usage-note">${escapeHtml(notes.filter(n => n).join(' '))}</div>`;
    }

    /** Shows the shared popover (core's) with `html` beside `anchor`, and returns it. */
    show(anchor, html) {
        let pop = document.getElementById('popover_llma_usage');
        if (!pop) {
            pop = createDiv('popover_llma_usage', 'sui-popover sui_popover_model llma-usage-popover');
            pop.setAttribute('role', 'dialog');
            pop.setAttribute('aria-label', translate('Usage details'));
            document.body.appendChild(pop);
        }
        this.popoverSequence++;
        pop.innerHTML = html;
        showPopover('llma_usage', 0, 0);
        this.place(pop, anchor);
        return pop;
    }

    /** Keeps the popover on screen: under its anchor, or above it when there is no room below. */
    place(pop, anchor) {
        let rect = anchor.getBoundingClientRect();
        let left = Math.max(8, Math.min(rect.left, window.innerWidth - pop.offsetWidth - 8));
        let top = rect.bottom + 6;
        if (top + pop.offsetHeight > window.innerHeight - 8) {
            top = Math.max(8, rect.top - pop.offsetHeight - 6);
        }
        pop.style.left = `${left}px`;
        pop.style.top = `${top}px`;
    }

    /** Opens Settings on the Usage tab, with a row ready for `model` when it has no price yet. */
    openSettings(model) {
        hidePopover('llma_usage');
        llmaOpenSettings();
        llmaUsageSettings.wanted = model || '';
        document.querySelector('.llma-modal-tab[data-tab="usage"]')?.click();
    }
}

/**
 * The Usage tab of the settings dialog: today's and this week's totals, the user's price table (per million tokens, nothing bundled), the currency label and the soft budget.
 * Saved by its own route, from its own button or with Save & Close.
 */
class LLMAUsageSettings {

    constructor() {
        /** True once the user has changed something that is not saved. */
        this.dirty = false;
        /** A model to add a row for once the table is drawn. */
        this.wanted = '';
    }

    /** Draws the tab. Unsaved changes are left as they are. */
    async load() {
        let totals = document.getElementById('llma-usage-totals');
        totals.innerHTML = await llmaUsage.periodsHtml();
        if (this.dirty) {
            this.addWanted();
            return;
        }
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantGetUsageSettings', {});
        }
        catch {
            reply = null;
        }
        let form = document.getElementById('llma-usage-form');
        if (!reply?.success) {
            form.innerHTML = `<div class="text-muted py-2">${escapeHtml(translate('You do not have permission to change prices.'))}</div>`;
            return;
        }
        this.render(reply.usage);
        this.addWanted();
    }

    /** Draws the currency, price table and budget from the saved settings. */
    render(usage) {
        let form = document.getElementById('llma-usage-form');
        let models = (LLMAState.availableModels || []).map(m => `<option value="${escapeHtml(m.id)}"></option>`).join('');
        form.innerHTML = `<h6 class="mt-3">${escapeHtml(translate('Prices per million tokens'))}</h6>`
            + `<div class="text-muted small mb-2">${escapeHtml(translate('No prices are built in: copy them from your provider\'s pricing page. A model with no row shows tokens only. The model id must match the model list exactly. Cached input is charged as ordinary input when left empty. Costs are always worked out at the prices here, past replies included.'))}</div>`
            + `<label class="d-flex align-items-center gap-2 mb-2"><span>${escapeHtml(translate('Currency label'))}</span><input type="text" class="form-control form-control-sm llma-usage-currency" id="llma-usage-currency" maxlength="12" value="${escapeHtml(usage.currency)}"></label>`
            + `<datalist id="llma-usage-models">${models}</datalist><div id="llma-usage-rows"></div>`
            + `<button type="button" class="basic-button" id="llma-usage-add">${escapeHtml(translate('+ Add model price'))}</button>`
            + `<h6 class="mt-3">${escapeHtml(translate('Soft budget'))}</h6>`
            + `<div class="form-check"><input type="checkbox" class="form-check-input" id="llma-usage-budget-enabled"${usage.budget.enabled ? ' checked' : ''}><label class="form-check-label" for="llma-usage-budget-enabled">${escapeHtml(translate('Warn me when spending reaches a limit (nothing is ever blocked)'))}</label></div>`
            + `<div class="d-flex flex-wrap gap-2 mt-1">${this.limitInput('llma-usage-budget-daily', 'Daily limit (0 for none)', usage.budget.daily)}${this.limitInput('llma-usage-budget-weekly', 'Weekly limit (0 for none)', usage.budget.weekly)}</div>`
            + `<div class="d-flex flex-wrap align-items-center gap-2 mt-3"><button type="button" class="basic-button btn-primary" id="llma-usage-save">${escapeHtml(translate('Save prices and budget'))}</button><span id="llma-usage-status" class="small" role="status"></span></div>`;
        for (let [model, price] of Object.entries(usage.prices)) {
            this.addRow(model, price);
        }
        form.oninput = () => this.dirty = true;
        document.getElementById('llma-usage-add').addEventListener('click', () => {
            this.addRow('', {}).querySelector('input').focus();
            this.dirty = true;
        });
        document.getElementById('llma-usage-save').addEventListener('click', () => this.save());
    }

    /** One labelled limit box. */
    limitInput(id, label, value) {
        return `<label class="llma-usage-limit"><span class="small">${escapeHtml(translate(label))}</span><input type="number" min="0" step="any" class="form-control form-control-sm" id="${id}" value="${Number(value) || 0}"></label>`;
    }

    /** Adds one row of the price table and returns it. */
    addRow(model, price) {
        let row = createDiv(null, 'llma-usage-row');
        let box = (name, label, value) => `<input type="number" min="0" step="any" class="form-control form-control-sm" data-field="${name}" placeholder="${escapeHtml(translate(label))}" aria-label="${escapeHtml(translate(label))}" value="${value === null || value === undefined ? '' : Number(value)}">`;
        row.innerHTML = `<input type="text" class="form-control form-control-sm" data-field="model" list="llma-usage-models" placeholder="${escapeHtml(translate('Model id'))}" aria-label="${escapeHtml(translate('Model id'))}" value="${escapeHtml(model)}" autocomplete="off">`
            + box('input', 'Input', price.input) + box('output', 'Output', price.output) + box('cached', 'Cached (optional)', price.cached)
            + `<button type="button" class="basic-button llma-usage-remove">${escapeHtml(translate('Remove'))}</button>`;
        row.querySelector('.llma-usage-remove').addEventListener('click', () => {
            row.remove();
            this.dirty = true;
        });
        document.getElementById('llma-usage-rows').appendChild(row);
        return row;
    }

    /** Adds the row asked for by a reply's "Edit prices", when the table has none for that model. */
    addWanted() {
        let model = this.wanted;
        this.wanted = '';
        if (!model || !document.getElementById('llma-usage-rows')) {
            return;
        }
        if (![...document.querySelectorAll('#llma-usage-rows [data-field="model"]')].some(input => input.value == model)) {
            this.addRow(model, {}).querySelector('[data-field="input"]').focus();
        }
    }

    /** Saves the tab. Returns false, with the reason shown, when it was refused. */
    async save() {
        let status = document.getElementById('llma-usage-status');
        let prices = {};
        for (let row of document.querySelectorAll('#llma-usage-rows .llma-usage-row')) {
            let value = (name) => row.querySelector(`[data-field="${name}"]`).value.trim();
            let model = value('model');
            if (!model && !value('input') && !value('output') && !value('cached')) {
                continue;
            }
            if (Object.hasOwn(prices, model)) {
                return this.fail(status, `${translate('There are two rows for')} ${model}.`);
            }
            prices[model] = { input: value('input'), output: value('output'), cached: value('cached') };
        }
        let budget = {
            enabled: document.getElementById('llma-usage-budget-enabled').checked,
            daily: document.getElementById('llma-usage-budget-daily').value,
            weekly: document.getElementById('llma-usage-budget-weekly').value
        };
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantSaveUsageSettings', { currency: document.getElementById('llma-usage-currency').value, prices: prices, budget: budget });
        }
        catch (err) {
            return this.fail(status, llmaShortError(err) || translate('Could not save.'));
        }
        this.dirty = false;
        this.render(reply.usage);
        document.getElementById('llma-usage-status').textContent = translate('Saved.');
        llmaUsage.warned.clear();
        llmaUsage.scheduleRefresh();
        document.getElementById('llma-usage-totals').innerHTML = await llmaUsage.periodsHtml();
        return true;
    }

    /** Shows why a save was refused and brings the tab forward. */
    fail(status, message) {
        status.textContent = message;
        document.querySelector('.llma-modal-tab[data-tab="usage"]')?.click();
        llmaShowToast(message, 'error');
        return false;
    }

    /** Saves with the dialog's Save & Close when there is something unsaved. */
    async saveIfDirty() {
        return this.dirty ? await this.save() : true;
    }

    /** Forgets unsaved changes (the dialog was closed or the settings reset). */
    discard() {
        this.dirty = false;
    }
}

let llmaUsage = new LLMAUsage();
let llmaUsageSettings = new LLMAUsageSettings();

document.addEventListener('click', (e) => {
    let button = e.target.closest?.('#llma-usage-thread, #llma-usage-warning');
    if (button) {
        llmaUsage.openThread(button);
    }
});

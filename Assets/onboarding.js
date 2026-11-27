/**
 * The first-run guide: what is ready (a backend, a chat model, tools, embeddings), where to set up a backend when there is none, and the starter set to add in one click. It sits over an empty
 * chat and never blocks anything: the message box, the chat list and the settings stay usable, the assistant gallery and chats with messages hide it, sending a message or running a tool closes it, and it can be
 * skipped. The server keeps whether the user has seen it and says what to show; Settings > General can run it again. Built from Bootstrap's list-group, badge and alert.
 */
class LLMAOnboarding {

    constructor() {
        /** Whether the guide is wanted now (it still only shows over an empty chat). */
        this.open = false;
        /** The server's last report, or null before one arrived. */
        this.report = null;
        /** What the last starter set install said, as safe HTML. */
        this.result = '';
        /** Whether a request made by a button is running. */
        this.busy = false;
        /** The id of the button that had the keyboard when the running request started, or null. */
        this.pressed = null;
    }

    /** The guide's element. */
    box() {
        return document.getElementById('llma-onboarding');
    }

    /** Wires the settings button, watches the view, and opens the guide when the server says to. Called once, after the tab has drawn. */
    async init() {
        if (!this.box()) {
            return;
        }
        this.setupSettings();
        let observer = new MutationObserver(() => this.sync());
        observer.observe(document.getElementById('llma-messages'), { childList: true });
        observer.observe(document.getElementById('llma-welcome'), { attributes: true, attributeFilter: ['style'] });
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantGetOnboarding', { checks: false });
        }
        catch (err) {
            // The guide is optional: without the answer the chat works as it always does.
            return;
        }
        if (reply?.success && reply.show) {
            await this.show();
        }
    }

    /** Draws the settings button (once). */
    setupSettings() {
        let box = document.getElementById('llma-onboarding-settings');
        if (!box || box.dataset.built) {
            return;
        }
        box.dataset.built = 'true';
        box.innerHTML = `<h6>${translate('Getting started')}</h6>`
            + `<p class="small text-muted mb-2">${translate('The first-run guide: what is ready, how to set up a backend, and the starter assistants, prompt snippets and example chats.')}</p>`
            + `<button type="button" class="basic-button" id="llma-onboarding-rerun">${translate('Show the guide again')}</button>`;
        document.getElementById('llma-onboarding-rerun').addEventListener('click', () => this.rerun());
    }

    /** Opens the guide and loads what it shows. */
    async show() {
        this.open = true;
        this.result = '';
        this.render();
        this.sync();
        await this.load(false);
    }

    /** Shows the guide when it is open and an empty chat is on screen. */
    sync() {
        let box = this.box();
        let welcome = document.getElementById('llma-welcome');
        let messages = document.getElementById('llma-messages');
        if (!box || !welcome || !messages) {
            return;
        }
        let visible = this.open && welcome.style.display == 'none' && messages.childElementCount == 0;
        box.style.display = visible ? '' : 'none';
        box.parentElement.classList.toggle('llma-onboarding-open', visible);
    }

    /** Asks the server for the report. With refresh, the backends are asked for their models again. */
    async load(refresh) {
        let reply = null;
        try {
            reply = await llmaRequest('LLMAssistantGetOnboarding', { checks: true, refresh: refresh });
        }
        catch (err) {
            reply = { error: llmaShortError(err) };
        }
        if (!reply?.success) {
            let error = reply?.error || translate('The check could not run.');
            this.result = `<div class="alert alert-danger py-2">${escapeHtml(error)}</div>`;
            llmaA11y.announce(error);
        }
        else {
            this.report = reply;
            // The server's recent model list can be older than the chat's (a backend that was still starting): when they disagree, ask the backends again.
            if (!refresh && !reply.canChat && LLMAState.availableModels?.length > 0) {
                await this.load(true);
                return;
            }
        }
        this.render();
    }

    /** The user sent a message or ran a tool in a chat: the guide is done with. The server records that itself when it saves it. */
    onSend() {
        this.open = false;
        this.sync();
    }

    /** The chat's model list has models: if the guide still says that nothing can answer, checks again. */
    async onModels() {
        if (this.open && !this.busy && this.report && !this.report.canChat) {
            await this.load(true);
        }
    }

    /** Draws the whole guide from the last report. */
    render() {
        let box = this.box();
        if (!box) {
            return;
        }
        let report = this.report;
        // Every button is replaced: the one with the keyboard gets it back. A button disabled for its own request may have lost it to the page already.
        let held = box.contains(document.activeElement) ? document.activeElement.id : (document.activeElement == document.body ? this.pressed : null);
        let scrolled = box.querySelector('.llma-onboarding-body')?.scrollTop || 0;
        let html = `<div class="llma-onboarding-body"><h2 class="llma-onboarding-title" id="llma-onboarding-title">${translate('Getting started')}</h2>`
            + `<p class="text-muted">${translate('A quick look at what is ready. You can skip this and look around at any time; Settings > General brings it back.')}</p>`;
        if (!report) {
            html += this.result
                ? `${this.result}<button type="button" class="basic-button mb-3" id="llma-onboarding-recheck">${translate('Check again')}</button>`
                : `<div class="small text-muted mb-3">${translate('Checking...')}</div>`;
        }
        else {
            html += this.checksHtml(report) + this.starterHtml(report) + `<div id="llma-onboarding-result">${this.result}</div>`;
        }
        html += '</div><div class="llma-onboarding-actions">'
            + `<button type="button" class="basic-button" id="llma-onboarding-skip">${translate('Skip for now')}</button>`
            + (report?.canChat ? `<button type="button" class="basic-button btn-primary" id="llma-onboarding-done">${translate('Start chatting')}</button>` : '')
            + '</div>';
        box.innerHTML = html;
        // The guide is redrawn whole after each step: a reader part way down stays there.
        box.querySelector('.llma-onboarding-body').scrollTop = scrolled;
        this.wire('llma-onboarding-skip', () => this.close('skip'));
        this.wire('llma-onboarding-done', () => this.close('complete'));
        this.wire('llma-onboarding-recheck', () => this.recheck());
        this.wire('llma-onboarding-install', () => this.install());
        this.wire('llma-onboarding-backends', () => llmaCoreBridge.openServerBackends());
        if (held) {
            this.focusButton(held);
        }
    }

    /** Gives a button the keyboard, or the way on when that button is gone or can no longer be pressed. */
    focusButton(id) {
        let target = document.getElementById(id);
        if (!target || target.disabled) {
            target = document.getElementById('llma-onboarding-done') || document.getElementById('llma-onboarding-skip');
        }
        target?.focus({ preventScroll: true });
    }

    /** Disables the button a request started from, remembering whether it had the keyboard. */
    hold(id) {
        let button = document.getElementById(id);
        this.pressed = document.activeElement == button ? id : null;
        button.disabled = true;
    }

    /** Wires one button, when it was drawn. */
    wire(id, action) {
        document.getElementById(id)?.addEventListener('click', action);
    }

    /** The checklist, with the way to the backend settings when nothing can answer yet. */
    checksHtml(report) {
        let html = '<ul class="list-group mb-2">';
        for (let check of report.checks) {
            html += `<li class="list-group-item bg-transparent" data-check="${escapeHtml(check.id)}" data-status="${escapeHtml(check.status)}">`
                + `<span class="badge text-bg-${llmaReadiness.colors[check.status] || 'secondary'} me-2">${translate(llmaReadiness.words[check.status] || check.status)}</span>`
                + `<b>${escapeHtml(check.title)}</b><br><small class="text-break">${escapeHtml(check.detail)}</small>`
                + (check.fix ? `<br><small class="text-muted">${escapeHtml(translate('To fix'))}: ${escapeHtml(check.fix)}</small>` : '')
                + '</li>';
        }
        html += '</ul>';
        if (!report.canChat) {
            html += `<div class="alert alert-warning py-2" id="llma-onboarding-nobackend">${translate('Chatting needs an LLM backend with a model. Any backend Swarm offers will do.')} `
                + (report.setup.canAdd ? '' : translate('Ask an administrator to set one up.'))
                + (report.setup.canView ? `<div class="mt-2"><button type="button" class="basic-button" id="llma-onboarding-backends">${translate('Open Server > Backends')}</button></div>` : '')
                + '</div>';
        }
        return `${html}<button type="button" class="basic-button mb-3" id="llma-onboarding-recheck">${translate('Check again')}</button>`;
    }

    /** The starter set: what it holds, which of it is there, and the button that adds the rest. */
    starterHtml(report) {
        let starter = report.starter;
        let kinds = { assistant: 'Assistant', snippet: 'Prompt snippet', chat: 'Example chat' };
        let html = `<h6>${translate('Starter set')}</h6>`
            + `<p class="small text-muted mb-2">${translate('A few assistants, prompt snippets (slash commands) and example chats, added as your own items to edit or delete. Nothing you already have is changed.')}</p>`
            + '<ul class="list-group mb-2">';
        for (let item of starter.items) {
            html += `<li class="list-group-item bg-transparent llma-onboarding-item" data-item="${escapeHtml(item.id)}" data-installed="${item.installed ? 'true' : 'false'}">`
                + `<span class="text-break">${escapeHtml(item.title)} <small class="text-muted">${translate(kinds[item.kind] || item.kind)}</small></span>`
                + (item.installed ? `<span class="badge text-bg-success">${translate('Added')}</span>` : '')
                + (item.inWorkspace ? `<span class="badge text-bg-secondary" title="${escapeHtml(translate('Only chats in that workspace have it'))}">${translate('In a workspace')}</span>` : '')
                + '</li>';
        }
        let done = starter.inWorkspace > 0 ? 'Nothing left to add' : 'Starter set added';
        let label = starter.missing == 0 ? done : (starter.installed == 0 ? 'Add starter set' : 'Add what is missing');
        return `${html}</ul><button type="button" class="basic-button mb-2" id="llma-onboarding-install"${starter.missing == 0 ? ' disabled' : ''}>${translate(label)}</button>`;
    }

    /** Skips or finishes the guide. It closes at once; if the server did not record it, it will show again next time. */
    async close(action) {
        this.open = false;
        this.sync();
        try {
            await llmaRequest('LLMAssistantSetOnboarding', { action: action });
        }
        catch (err) {
            llmaShowToast(llmaShortError(err) || translate('That could not be saved.'), 'error');
        }
        document.getElementById('llma-input')?.focus();
    }

    /** Checks again after the user changed something, and reloads the chat's model list to match. */
    async recheck() {
        if (this.busy) {
            return;
        }
        this.busy = true;
        try {
            this.result = '';
            this.hold('llma-onboarding-recheck');
            await llmaLoadModels();
            await this.load(true);
            // A failure was already said.
            if (!this.result) {
                llmaA11y.announce(translate('Check finished'));
            }
        }
        finally {
            this.busy = false;
            this.pressed = null;
        }
    }

    /** Adds the starter set and redraws the lists it changed. */
    async install() {
        if (this.busy) {
            return;
        }
        this.busy = true;
        try {
            this.hold('llma-onboarding-install');
            let reply = null;
            try {
                reply = await llmaRequest('LLMAssistantInstallStarterSet', {});
            }
            catch (err) {
                reply = { error: llmaShortError(err) };
            }
            if (!reply?.success) {
                let error = reply?.error || translate('The starter set could not be added.');
                this.result = `<div class="alert alert-danger py-2">${escapeHtml(error)}</div>`;
                llmaA11y.announce(error);
            }
            else {
                await this.reloadLists();
                // What is there already, or in a workspace (its line says so), is no problem.
                let problems = reply.skipped.filter(s => s.code != 'exists' && s.code != 'workspace');
                this.result = `<div class="alert alert-${problems.length ? 'warning' : 'success'} py-2">${escapeHtml(translate('Added'))}: ${reply.added.length}`
                    + problems.map(s => `<br><small>${escapeHtml(s.title)}: ${escapeHtml(s.reason)}</small>`).join('')
                    + '</div>';
                llmaA11y.announce([`${translate('Added')}: ${reply.added.length}`, ...problems.map(s => `${s.title}: ${s.reason}`)].join('. '));
                // The guide may have been run again meanwhile, and have no report yet.
                if (this.report) {
                    this.report.starter = reply.starter;
                }
                else {
                    await this.load(false);
                }
            }
        }
        finally {
            this.busy = false;
            this.render();
            this.pressed = null;
        }
    }

    /** Loads what an install changes: assistants, chats and slash commands. The assistant on screen stays the one in use: loading the list gives back the saved one, which can differ. */
    async reloadLists() {
        let active = LLMAState.activeAssistantId;
        await llmaLoadAssistants();
        if (LLMAState.assistants.some(a => a.id == active)) {
            LLMAState.activeAssistantId = active;
        }
        await llmaLoadThreads();
        llmaPromptAssets.loadedFor = null;
        llmaRenderWelcomeAssistants();
    }

    /** From Settings: closes the dialog, records that the guide is wanted, and shows it over a new chat. */
    async rerun() {
        if (LLMAState.isGenerating) {
            llmaShowToast(translate('Stop generation first'), 'info');
            return;
        }
        if (!llmaCloseSettings()) {
            return;
        }
        try {
            await llmaRequest('LLMAssistantSetOnboarding', { action: 'run' });
        }
        catch (err) {
            llmaShowToast(llmaShortError(err) || translate('That could not be saved.'), 'error');
            return;
        }
        let assistant = LLMAState.assistants.find(a => a.id == LLMAState.activeAssistantId) || LLMAState.assistants[0];
        if (assistant) {
            llmaStartChatWithAssistant(assistant.id, { persist: false });
        }
        else {
            llmaShowWelcome();
        }
        this.report = null;
        await this.show();
    }
}

let llmaOnboarding = new LLMAOnboarding();

/**
 * The composer's prompt rewrite controls: whether the images this chat makes have their prompts rewritten for the image model (this chat's own switch, and the one for all chats), and a preview of what a rewrite
 * would give. The server decides which switch wins (LLMAssistantGetRewritePolicy), so this only shows what it is told and sends changes back.
 */
class LLMARewriteControls {

    constructor() {
        /** The server's last answer about the chat showing, or null when it is not known. */
        this.policy = null;
        /** Counts policy requests, so a late answer for an earlier chat is dropped. */
        this.policyRequest = 0;
        /** Counts previews, so a late answer for a closed or repeated preview is dropped. */
        this.previewRequest = 0;
        /** Stops watching for a click outside the open popover. */
        this.stopClickAway = null;
    }

    /** Wires the composer button, its popover and the preview dialog. */
    setup() {
        let button = document.getElementById('llma-enhance-btn');
        if (!button) {
            return;
        }
        button.addEventListener('click', e => {
            e.stopPropagation();
            this.togglePopover();
        });
        for (let radio of document.querySelectorAll('input[name="llma-enhance-chat"]')) {
            radio.addEventListener('change', () => this.setChat(radio.value));
        }
        document.getElementById('llma-enhance-all').addEventListener('change', e => this.setAllChats(e.target.value));
        document.getElementById('llma-enhance-preview-btn').addEventListener('click', () => this.openPreview());
        document.getElementById('llma-rewrite-preview-run').addEventListener('click', () => this.runPreview());
        document.getElementById('llma-rewrite-preview-close').addEventListener('click', () => this.closePreview());
        document.getElementById('llma-rewrite-preview-done').addEventListener('click', () => this.closePreview());
        let overlay = document.getElementById('llma-rewrite-overlay');
        overlay.addEventListener('click', e => {
            if (e.target == overlay) {
                this.closePreview();
            }
        });
        this.refresh();
    }

    /** A chat switch (true, false or null) as the API's text. */
    toSwitch(value) {
        return value === true ? 'on' : value === false ? 'off' : 'default';
    }

    /** Takes the chat switch of a chat that was just loaded, then asks the server again. */
    onThreadLoaded(thread) {
        LLMAState.activeThreadAutoEnhance = typeof thread.autoEnhance == 'boolean' ? thread.autoEnhance : null;
        this.refresh();
    }

    /** Asks the server what applies to the chat showing, for an image model (empty for the one the image tool would default to). */
    async askPolicy(imageModel = '') {
        try {
            return await llmaRequest('LLMAssistantGetRewritePolicy', {
                threadId: LLMAState.activeThreadId || '',
                assistantId: LLMAState.activeAssistantId || '',
                chatOverride: this.toSwitch(LLMAState.activeThreadAutoEnhance),
                imageModel: imageModel
            });
        }
        catch {
            return null;
        }
    }

    /** Reads what applies to the chat showing and draws it. */
    async refresh() {
        let request = ++this.policyRequest;
        let policy = await this.askPolicy();
        if (request != this.policyRequest) {
            return;
        }
        this.policy = policy;
        this.render();
    }

    /** One sentence saying whether prompts are rewritten and which switch decides. */
    describe(policy) {
        let levels = {
            call: 'this generation', chat: 'this chat', assistant: 'the assistant', model: 'your rule for the image model',
            entryPoint: 'the All chats switch', master: 'the main switch in Settings'
        };
        let by = `${translate('Decided by')} ${translate(levels[policy.decidedBy] || policy.decidedBy)}.`;
        if (!policy.run) {
            return `${translate('Off: image prompts are sent as written.')} ${by}`;
        }
        if (policy.layers.model === false) {
            return `${translate('On, but your rule gives this image model no style, so its prompts are sent as written:')} ${policy.imageModel}. ${by}`;
        }
        return `${translate('On: image prompts are rewritten for their model.')} ${by}`;
    }

    /** Draws the button and the popover from the chat switch and the server's answer. */
    render() {
        let button = document.getElementById('llma-enhance-btn');
        if (!button) {
            return;
        }
        let own = LLMAState.activeThreadAutoEnhance;
        let run = this.policy ? this.policy.run : null;
        document.getElementById('llma-enhance-label').textContent = run === null ? '?' : run ? translate('On') : translate('Off');
        button.classList.toggle('on', run === true);
        button.classList.toggle('off', own === false);
        let status = this.policy ? this.describe(this.policy) : translate('Could not read the setting.');
        // title is reassigned every time this runs, so translate() directly, never class="translate".
        button.title = `${translate('Auto-enhance prompt')}. ${status}`;
        document.getElementById(`llma-enhance-chat-${this.toSwitch(own)}`).checked = true;
        if (this.policy) {
            document.getElementById('llma-enhance-all').value = this.policy.settings.chat;
        }
        document.getElementById('llma-enhance-status').textContent = status;
    }

    /** Opens or closes the popover; opening reads the state again, since Settings or the assistant may have changed it. */
    togglePopover() {
        let popover = document.getElementById('llma-enhance-popover');
        let open = popover.style.display == 'none';
        llmaCloseAllPopovers(popover);
        if (!open) {
            this.closePopover();
            return;
        }
        popover.style.display = '';
        let button = document.getElementById('llma-enhance-btn');
        button.setAttribute('aria-expanded', 'true');
        this.stopClickAway = llmaPopoverClickAway(popover, button, () => this.closePopover());
        this.refresh();
    }

    /** Closes the popover. */
    closePopover() {
        let popover = document.getElementById('llma-enhance-popover');
        if (!popover) {
            return;
        }
        popover.style.display = 'none';
        document.getElementById('llma-enhance-btn').setAttribute('aria-expanded', 'false');
        if (this.stopClickAway) {
            this.stopClickAway();
            this.stopClickAway = null;
        }
    }

    /** Sets this chat's own switch ('default', 'on' or 'off'). A chat not created yet keeps it until the first message creates it. */
    async setChat(value) {
        let enabled = value == 'on' ? true : value == 'off' ? false : null;
        let threadId = LLMAState.activeThreadId;
        if (threadId) {
            let reply = await llmaUserAction(() => llmaRequest('LLMAssistantSetThreadAutoEnhance', { threadId: threadId, enabled: enabled }), translate('Could not save the setting for this chat'));
            if (!reply || LLMAState.activeThreadId != threadId) {
                this.refresh();
                return;
            }
        }
        LLMAState.activeThreadAutoEnhance = enabled;
        await this.refresh();
    }

    /** Sets the switch for all chats in the user's own settings, sending only that key. */
    async setAllChats(value) {
        await this.saveEntryPoint('chat', value);
        await this.refresh();
    }

    /** Saves one entry point's switch ('default', 'on' or 'off') and keeps this page's copy of the settings in step, so a later full save does not undo it. */
    async saveEntryPoint(entryPoint, value) {
        let patch = { promptRewrite: { entryPoints: { [entryPoint]: value } } };
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantSaveSettings', { settings: JSON.stringify(patch) }), translate('Could not save the setting'));
        if (reply && LLMAState.settings) {
            let section = LLMAState.settings.promptRewrite || {};
            LLMAState.settings.promptRewrite = { ...section, entryPoints: { ...(section.entryPoints || {}), [entryPoint]: value } };
        }
        return !!reply;
    }

    /** Opens the preview on what is in the composer, for the image model the chat would use. */
    async openPreview() {
        this.closePopover();
        let overlay = document.getElementById('llma-rewrite-overlay');
        document.getElementById('llma-rewrite-preview-prompt').value = document.getElementById('llma-input')?.value || '';
        document.getElementById('llma-rewrite-preview-model').value = this.policy?.imageModel || document.getElementById('current_model')?.value || '';
        document.getElementById('llma-rewrite-preview-result').innerHTML = '';
        document.getElementById('llma-rewrite-preview-policy').textContent = '';
        document.getElementById('llma-rewrite-preview-run').disabled = false;
        overlay.style.display = '';
        llmaInstallFocusTrap(overlay);
        document.getElementById('llma-rewrite-preview-prompt').focus();
        try {
            let reply = await llmaRequest('LLMAssistantGetPromptStyles', {});
            document.getElementById('llma-rewrite-preview-models').innerHTML = (reply.imageModels || []).slice(0, 500).map(m => `<option value="${escapeHtml(m.name)}"></option>`).join('');
        }
        catch {
            // The names are only suggestions: one can be typed.
        }
    }

    /** Whether the preview dialog is showing. */
    previewOpen() {
        let overlay = document.getElementById('llma-rewrite-overlay');
        return !!overlay && overlay.style.display != 'none';
    }

    /** Closes the preview, dropping any answer still on its way. */
    closePreview() {
        this.previewRequest++;
        let overlay = document.getElementById('llma-rewrite-overlay');
        overlay.style.display = 'none';
        llmaRemoveFocusTrap(overlay);
        document.getElementById('llma-enhance-btn')?.focus();
    }

    /** Rewrites the previewed prompt and shows the original beside the result, the style used and any warnings. Nothing is generated. */
    async runPreview() {
        let prompt = document.getElementById('llma-rewrite-preview-prompt').value.trim();
        let out = document.getElementById('llma-rewrite-preview-result');
        if (!prompt) {
            out.textContent = translate('Write a prompt to preview.');
            return;
        }
        let model = document.getElementById('llma-rewrite-preview-model').value.trim();
        let button = document.getElementById('llma-rewrite-preview-run');
        let request = ++this.previewRequest;
        button.disabled = true;
        out.textContent = translate('Rewriting...');
        let policy = await this.askPolicy(model);
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantRewritePrompt', { prompt: prompt, imageModel: model }), translate('Could not rewrite'));
        if (request != this.previewRequest) {
            return;
        }
        button.disabled = false;
        // Disabling the focused button drops the focus to the page; take it back into the dialog.
        if (document.activeElement == document.body) {
            button.focus();
        }
        document.getElementById('llma-rewrite-preview-policy').textContent = policy ? `${translate('When the assistant generates:')} ${this.describe(policy)}` : '';
        if (!reply) {
            out.textContent = '';
            return;
        }
        let column = (title, text, name) => `<div class="col-12 col-md-6"><div class="small text-muted">${translate(title)}</div><pre class="llma-rewrite-text ${name}">${escapeHtmlNoBr(text)}</pre></div>`;
        out.innerHTML = llmaPromptStyles.rewriteSummaryLines(reply).join('<br>')
            + `<div class="row g-2 my-1">${column('Original', reply.original, 'llma-preview-original')}${column('Rewritten', reply.text, 'llma-preview-rewritten')}</div>`
            + llmaPromptStyles.rewriteDetailLines(reply, '').join('<br>');
    }
}

let llmaRewriteControls = new LLMARewriteControls();

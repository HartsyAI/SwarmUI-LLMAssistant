/**
 * Organizes the three LLM backend cards that core renders under Server > Backends, without replacing core code:
 * - Groups each card's fields into Basics, Memory and Advanced sections, each with a link to its guide in the README.
 * - Shows plain labels, with the saved name and a guide link in each tooltip. Saved names never change.
 * - Relists GPU when the local card's Device changes, from LLMAssistantListGpus, and greys out what the device ignores.
 * - Offers the server's own models for Default Model, from LLMAssistantListBackendModels.
 * - Keeps a saved value a dropdown does not list, so the next Save does not overwrite it.
 * Core rebuilds a card when it is saved or its settings change, so a MutationObserver re-runs this. An organized card gets no writes.
 */
class LLMBackendCards {

    /** Sets up the card layouts. */
    constructor() {
        this.guideRoot = 'https://github.com/HartsyAI/SwarmUI-LLMAssistant#';
        this.localType = 'llmassistant-hartsy-local';
        this.openaiType = 'llmassistant-openai';
        this.anthropicType = 'llmassistant-anthropic';
        /**
         * Per backend type: sections in card order (Basics starts open, the others open by themselves when a field in them is not
         * at its default), then the label, default and number limits per saved field name.
         */
        this.layouts = {
            'llmassistant-hartsy-local': {
                sections: [
                    { key: 'basics', title: 'Basics', guide: 'local-engine-basics', fields: ['Device', 'GPUDeviceId', 'VramMode'] },
                    { key: 'memory', title: 'Memory', guide: 'local-engine-memory', fields: ['Placement', 'LowVramQuant', 'AlwaysFreeMemory', 'EvictBelowGb', 'ReuseConversationPrefix'] },
                    { key: 'advanced', title: 'Advanced', guide: 'local-engine-advanced', fields: ['RepetitionPenalty', 'TopK', 'MinP', 'GraphDecode', 'SpeculativeDecode', 'RecurrentLoops', 'StructuredToolCalling'] }
                ],
                fields: {
                    Device: { label: 'Device', default: 'auto' },
                    GPUDeviceId: { label: 'GPU', default: '0' },
                    VramMode: { label: 'VRAM Mode', default: 'Auto' },
                    Placement: { label: 'Placement', default: 'auto' },
                    LowVramQuant: { label: 'Keep Weights Compressed', default: 'false' },
                    AlwaysFreeMemory: { label: 'Unload After Each Reply', default: 'false' },
                    EvictBelowGb: { label: 'Free RAM Floor (GB)', default: '0', min: 0, max: 1024, step: 1 },
                    ReuseConversationPrefix: { label: 'Reuse Chat Memory', default: 'true' },
                    RepetitionPenalty: { label: 'Repetition Penalty', default: '1.1', min: 0, max: 2, step: 0.05 },
                    TopK: { label: 'Top K', default: '40', min: 0, max: 200, step: 1 },
                    MinP: { label: 'Min P', default: '0', min: 0, max: 1, step: 0.01 },
                    GraphDecode: { label: 'Graph Decode', default: 'false' },
                    SpeculativeDecode: { label: 'Speculative Decode', default: 'false' },
                    RecurrentLoops: { label: 'Default Loops', default: '0' },
                    StructuredToolCalling: { label: 'Structured Tool Calling', default: 'false' }
                }
            },
            'llmassistant-openai': {
                sections: [
                    { key: 'basics', title: 'Basics', guide: 'remote-openai-compatible-settings', fields: ['Address', 'AuthorizationHeader', 'DefaultModel'] },
                    { key: 'advanced', title: 'Advanced', guide: 'remote-openai-compatible-settings', fields: ['AllowIdle', 'ConnectionAttemptTimeoutSeconds', 'OtherHeaders', 'TokenLimitParameter', 'NativeToolCalling'] }
                ],
                fields: {
                    Address: { label: 'Address', default: '' },
                    AuthorizationHeader: { label: 'Authorization Header', default: '' },
                    DefaultModel: { label: 'Default Model', default: '' },
                    AllowIdle: { label: 'Allow Idle', default: 'false' },
                    ConnectionAttemptTimeoutSeconds: { label: 'Connect Timeout (seconds)', default: '30', min: 1, max: 600, step: 1 },
                    OtherHeaders: { label: 'Other Headers', default: '' },
                    TokenLimitParameter: { label: 'Token Limit Field', default: 'auto' },
                    NativeToolCalling: { label: 'Native Tool Calling', default: 'auto' }
                }
            },
            'llmassistant-anthropic': {
                sections: [
                    { key: 'basics', title: 'Basics', guide: 'anthropic-settings', fields: ['DefaultModel'] },
                    { key: 'advanced', title: 'Advanced', guide: 'anthropic-settings', fields: ['TimeoutSeconds', 'WorkspaceId', 'BaseUrl'] }
                ],
                fields: {
                    DefaultModel: { label: 'Default Model', default: 'claude-opus-4-8' },
                    TimeoutSeconds: { label: 'Reply Timeout (seconds)', default: '120', min: 10, max: 3600, step: 10 },
                    WorkspaceId: { label: 'Workspace ID', default: '' },
                    BaseUrl: { label: 'Base URL', default: 'https://api.anthropic.com' }
                }
            }
        };
        /** LLMAssistantListGpus answers by Device value. Null marks a failed request, so it is not retried on every poll. */
        this.gpuLists = {};
        /** Device values with a GPU list request in flight. */
        this.gpuPending = {};
        /** LLMAssistantListBackendModels answers by "backendId:modcount", so a saved change of address asks again. */
        this.modelLists = {};
        /** Model list keys with a request in flight. */
        this.modelPending = {};
        /** Sections the user opened or closed, by backend id and section key, so a rebuilt card keeps them. */
        this.openState = {};
        /** Reentrancy guard so our own DOM writes don't recurse through the MutationObserver. */
        this.enhancing = false;
    }

    /** Hooks core's re-render points. Called once the session is ready. */
    init() {
        if (typeof backendsRevisedCallbacks == 'undefined') {
            return;
        }
        backendsRevisedCallbacks.push(() => this.enhanceAllCards());
        let list = document.getElementById('backends_list');
        if (list) {
            new MutationObserver(() => this.onListMutated()).observe(list, { childList: true, subtree: true });
        }
        document.addEventListener('change', e => this.onFieldChanged(e));
        let tabButton = document.getElementById('serverbackendstabbutton');
        if (tabButton) {
            // Each visit asks again, so a key added in the User tab since then shows up without a page reload.
            tabButton.addEventListener('click', () => this.refreshModelLists());
        }
        this.enhanceAllCards();
    }

    /** Drops the cached model lists and asks again for every organized remote card. */
    refreshModelLists() {
        this.modelLists = {};
        if (typeof backends_loaded == 'undefined') {
            return;
        }
        for (let cardBody of document.querySelectorAll('.llma-card-organized')) {
            let backend = backends_loaded[cardBody.dataset.llmaBackendId];
            if (backend && cardBody.dataset.llmaType != this.localType) {
                this.applyModelList(cardBody, backend, true);
            }
        }
    }

    /** Re-runs enhancement after core changes the backends list, guarding against our own writes. */
    onListMutated() {
        if (this.enhancing) {
            return;
        }
        this.enhancing = true;
        try {
            this.enhanceAllCards();
        }
        finally {
            this.enhancing = false;
        }
    }

    /** Organizes each newly rendered LLM card. */
    enhanceAllCards() {
        if (typeof backends_loaded == 'undefined') {
            return;
        }
        for (let backend of Object.values(backends_loaded)) {
            let layout = this.layouts[backend.type];
            if (!layout) {
                continue;
            }
            let cardBody = document.getElementById(`backend-card-${backend.id}`)?.querySelector('.card-body');
            if (!cardBody || cardBody.classList.contains('llma-card-organized')) {
                continue;
            }
            cardBody.classList.add('llma-card-organized');
            cardBody.dataset.llmaType = backend.type;
            cardBody.dataset.llmaBackendId = backend.id;
            this.keepSavedValues(cardBody, backend);
            this.organize(cardBody, backend, layout);
            if (backend.type == this.localType) {
                this.applyGpuList(cardBody);
            }
            else {
                this.prepareModelField(cardBody, backend);
                // Core renders the cards hidden at page load; ask the providers only once a card can be seen or the tab opens.
                this.applyModelList(cardBody, backend, cardBody.offsetParent != null);
            }
            this.applyRules(cardBody);
        }
    }

    /** Relists GPU when the local card's Device changes, and rechecks which fields apply after any edit on an LLM card. */
    onFieldChanged(e) {
        let name = e.target?.dataset?.name;
        let cardBody = e.target?.closest ? e.target.closest('.llma-card-organized') : null;
        if (!name || !cardBody) {
            return;
        }
        if (name == 'Device' && cardBody.dataset.llmaType == this.localType) {
            // A GPU number means a different device under another Device, so a value the new list lacks is not kept.
            cardBody.dataset.llmaGpuReset = 'true';
            this.applyGpuList(cardBody);
        }
        this.applyRules(cardBody);
    }

    /**
     * Core shows a dropdown whose saved value is not one of its options with the first option selected, and the next Save writes
     * that back. Adds the saved value as its own option instead, so nothing changes unless the user picks.
     */
    keepSavedValues(cardBody, backend) {
        for (let select of cardBody.querySelectorAll('select[data-name]')) {
            let saved = backend.settings?.[select.dataset.name];
            if (saved == null || `${saved}` == '') {
                continue;
            }
            this.selectOrAdd(select, `${saved}`);
        }
    }

    /** Selects a value, adding it as "(saved value)" when the dropdown does not list it. */
    selectOrAdd(select, value) {
        let listed = false;
        for (let option of select.options) {
            if (option.value == value) {
                listed = true;
            }
        }
        if (!listed) {
            let option = document.createElement('option');
            option.value = value;
            option.innerText = `${value} (${translate('saved value')})`;
            select.appendChild(option);
        }
        select.value = value;
    }

    /** Moves the fields into sections with plain labels. Each input keeps its id and data-name, so core's Edit and Save work unchanged. */
    organize(cardBody, backend, layout) {
        let rows = {};
        for (let child of [...cardBody.children]) {
            let input = child.querySelector('[data-name]');
            if (input) {
                rows[input.dataset.name] = child;
            }
        }
        for (let section of layout.sections) {
            let members = [];
            for (let name of section.fields) {
                if (rows[name]) {
                    members.push(rows[name]);
                }
            }
            if (members.length == 0) {
                continue;
            }
            let box = createDiv(null, `llma-card-section llma-card-section-${section.key}`);
            let header = createDiv(null, 'llma-card-section-header');
            let caret = document.createElement('span');
            caret.className = 'llma-card-section-caret';
            let title = document.createElement('span');
            title.className = 'llma-card-section-title';
            title.innerText = translate(section.title);
            header.appendChild(caret);
            header.appendChild(title);
            header.appendChild(this.makeGuideLink(section.guide, 'Guide'));
            let body = createDiv(null, 'llma-card-section-body');
            box.appendChild(header);
            box.appendChild(body);
            // Anchor before moving anything: a row moved into the body is no longer in the card to anchor against.
            members[0].before(box);
            for (let row of members) {
                this.relabel(row, layout, section.guide);
                body.appendChild(row);
            }
            let stateKey = `${backend.id}:${section.key}`;
            let open = this.openState[stateKey];
            if (open == null) {
                open = section.key == 'basics' || this.hasChangedField(section, layout, backend);
            }
            this.setOpen(box, open);
            header.addEventListener('click', () => {
                let opening = body.style.display == 'none';
                this.openState[stateKey] = opening;
                this.setOpen(box, opening);
            });
        }
    }

    /** Whether any field in a section is saved at something other than its default. */
    hasChangedField(section, layout, backend) {
        for (let name of section.fields) {
            let saved = backend.settings?.[name];
            let info = layout.fields[name];
            if (saved != null && info && `${saved}` != info.default) {
                return true;
            }
        }
        return false;
    }

    /** Opens or closes a section. */
    setOpen(box, open) {
        box.querySelector('.llma-card-section-body').style.display = open ? 'block' : 'none';
        box.querySelector('.llma-card-section-caret').innerText = open ? '▼' : '▶';
    }

    /** Shows a field's plain label, puts its saved name in the tooltip header, adds a guide link under the tooltip, and sets number limits. */
    relabel(row, layout, guide) {
        let input = row.querySelector('[data-name]');
        let name = input.dataset.name;
        let info = layout.fields[name];
        if (!info) {
            return;
        }
        for (let span of row.querySelectorAll('.auto-input-name .translate')) {
            if (span.dataset.pretranslated == name) {
                span.dataset.pretranslated = info.label;
                span.innerText = translate(info.label);
            }
        }
        if (info.step != null && input.type == 'number') {
            input.min = info.min;
            input.max = info.max;
            input.step = info.step;
        }
        let pop = row.querySelector('.sui-popover');
        if (!pop) {
            return;
        }
        // textContent, not innerText: innerText reads empty while the popover is hidden.
        let bold = pop.querySelector('b');
        if (bold && bold.textContent == name) {
            bold.textContent = info.label;
            // Core follows the name with its input type; the saved name is more use there, since the logs and guides use it.
            let after = bold.nextSibling;
            if (after && after.nodeType == Node.TEXT_NODE) {
                after.textContent = ` (${name}):`;
            }
        }
        pop.appendChild(document.createElement('br'));
        pop.appendChild(this.makeGuideLink(guide, 'Read more in the guide'));
    }

    /** A link to a README section that opens in a new tab, without toggling the section header it sits in. */
    makeGuideLink(anchor, text) {
        let link = document.createElement('a');
        link.className = 'llma-card-guide-link';
        link.href = `${this.guideRoot}${anchor}`;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.innerText = translate(text);
        link.addEventListener('click', e => e.stopPropagation());
        return link;
    }

    /** Lists the GPU choices for the local card's Device, from a cached LLMAssistantListGpus answer. */
    applyGpuList(cardBody) {
        let device = cardBody.querySelector('[data-name="Device"]');
        let gpu = cardBody.querySelector('[data-name="GPUDeviceId"]');
        if (!device || !gpu || gpu.tagName != 'SELECT') {
            return;
        }
        if (!(device.value in this.gpuLists)) {
            this.requestGpuList(device.value);
            return;
        }
        let list = this.gpuLists[device.value];
        if (!list) {
            // The request failed, so the list core rendered stays.
            return;
        }
        let reset = cardBody.dataset.llmaGpuReset == 'true';
        delete cardBody.dataset.llmaGpuReset;
        let signature = JSON.stringify([list.values, list.names]);
        if (gpu.dataset.llmaSig != signature) {
            let current = gpu.value;
            this.fillSelect(gpu, list.values, list.names);
            gpu.dataset.llmaSig = signature;
            if (reset && !list.values.includes(current)) {
                gpu.value = list.values[0];
            }
            else {
                this.selectOrAdd(gpu, current);
            }
        }
        if (cardBody.dataset.llmaKind != list.kind) {
            cardBody.dataset.llmaKind = list.kind;
            this.applyRules(cardBody);
        }
    }

    /** Fetches the GPU choices for one Device value once, then updates every organized local card. */
    requestGpuList(deviceValue) {
        if (this.gpuPending[deviceValue]) {
            return;
        }
        this.gpuPending[deviceValue] = true;
        genericRequest('LLMAssistantListGpus', { 'device': deviceValue }, data => {
            this.gpuPending[deviceValue] = false;
            this.gpuLists[deviceValue] = data;
            for (let cardBody of document.querySelectorAll(`.llma-card-organized[data-llma-type="${this.localType}"]`)) {
                this.applyGpuList(cardBody);
            }
        }, 0, e => {
            this.gpuPending[deviceValue] = false;
            this.gpuLists[deviceValue] = null;
            console.warn(`[LLMAssistant] Could not list GPUs for Device '${deviceValue}', so the card keeps its own list: ${e}`);
        });
    }

    /** Turns the OpenAI card's Default Model box into a one-line input that can suggest the server's models while still taking any name. */
    prepareModelField(cardBody, backend) {
        if (backend.type != this.openaiType) {
            return;
        }
        let field = cardBody.querySelector('[data-name="DefaultModel"]');
        if (!field || field.tagName != 'TEXTAREA') {
            return;
        }
        // A textarea cannot take a suggestion list; a one-line input with the same id, data-name and value saves the same way.
        let input = document.createElement('input');
        input.type = 'text';
        input.id = field.id;
        input.dataset.name = field.dataset.name;
        input.className = 'auto-text llma-card-suggest-input';
        input.placeholder = field.placeholder;
        input.value = field.value;
        input.disabled = field.disabled;
        input.autocomplete = 'off';
        let list = document.createElement('datalist');
        list.id = `${input.id}_suggestions`;
        input.setAttribute('list', list.id);
        field.replaceWith(input);
        input.after(list);
    }

    /**
     * Fills Default Model from a cached LLMAssistantListBackendModels answer: suggestions on the OpenAI card, the dropdown on the
     * Anthropic one. With nothing cached, asks the server only when mayAsk is set.
     */
    applyModelList(cardBody, backend, mayAsk) {
        let key = `${backend.id}:${backend.modcount}`;
        if (!(key in this.modelLists)) {
            if (mayAsk) {
                this.requestModelList(backend.id, key);
            }
            return;
        }
        let list = this.modelLists[key];
        let field = cardBody.querySelector('[data-name="DefaultModel"]');
        // An empty list (no key, server offline) leaves the field as core rendered it.
        if (!list || !field || list.values.length == 0) {
            return;
        }
        if (field.tagName == 'SELECT') {
            let current = field.value;
            this.fillSelect(field, list.values, list.names);
            this.selectOrAdd(field, current);
            return;
        }
        let datalist = document.getElementById(field.getAttribute('list'));
        let signature = JSON.stringify([list.values, list.names]);
        if (datalist && datalist.dataset.llmaSig != signature) {
            datalist.innerHTML = '';
            datalist.dataset.llmaSig = signature;
            for (let i = 0; i < list.values.length; i++) {
                let option = document.createElement('option');
                option.value = list.values[i];
                if (list.names[i] != list.values[i]) {
                    option.label = list.names[i];
                }
                datalist.appendChild(option);
            }
        }
    }

    /** Fetches one backend's model choices once per saved state of its card, then fills its Default Model. */
    requestModelList(backendId, key) {
        if (this.modelPending[key]) {
            return;
        }
        this.modelPending[key] = true;
        genericRequest('LLMAssistantListBackendModels', { 'backend_id': backendId }, data => {
            this.modelPending[key] = false;
            this.modelLists[key] = data;
            if (data.reason) {
                console.log(`[LLMAssistant] Default Model list for backend ${backendId} is unavailable: ${data.reason}`);
            }
            let backend = backends_loaded[backendId];
            let cardBody = document.getElementById(`backend-card-${backendId}`)?.querySelector('.llma-card-organized');
            if (backend && cardBody) {
                this.applyModelList(cardBody, backend, false);
            }
        }, 0, e => {
            this.modelPending[key] = false;
            this.modelLists[key] = null;
            console.warn(`[LLMAssistant] Could not list models for backend ${backendId}, so Default Model stays as it is: ${e}`);
        });
    }

    /** Replaces a dropdown's options. */
    fillSelect(select, values, names) {
        select.innerHTML = '';
        for (let i = 0; i < values.length; i++) {
            let option = document.createElement('option');
            option.value = values[i];
            option.innerText = names[i];
            select.appendChild(option);
        }
    }

    /** Greys out the local card's fields that the chosen Device or other settings make unused, with the reason on hover. */
    applyRules(cardBody) {
        if (cardBody.dataset.llmaType != this.localType) {
            return;
        }
        let kind = cardBody.dataset.llmaKind || '';
        let vramMode = this.valueOf(cardBody, 'VramMode');
        let alwaysFree = cardBody.querySelector('[data-name="AlwaysFreeMemory"]')?.checked == true;
        this.setInactive(cardBody, 'GPUDeviceId', kind == 'cpu', 'Not used on CPU.');
        for (let name of ['Placement', 'LowVramQuant', 'GraphDecode']) {
            this.setInactive(cardBody, name, kind != '' && kind != 'cuda', 'Only used with CUDA (NVIDIA).');
        }
        this.setInactive(cardBody, 'ReuseConversationPrefix', alwaysFree || vramMode == 'Aggressive' || vramMode == 'Maximum',
            'Not used while Unload After Each Reply is on, or with VRAM Mode Aggressive or Maximum.');
    }

    /** Greys out one field's row, explaining why in its hover text. The field stays editable, so a saved value can still change. */
    setInactive(cardBody, name, inactive, reason) {
        let row = cardBody.querySelector(`[data-name="${name}"]`)?.closest('.auto-input');
        if (!row) {
            return;
        }
        row.classList.toggle('llma-card-inactive', inactive);
        row.title = inactive ? translate(reason) : '';
    }

    /** The current value of one field on a card, or an empty string. */
    valueOf(cardBody, name) {
        return cardBody.querySelector(`[data-name="${name}"]`)?.value || '';
    }
}

let llmBackendCards = new LLMBackendCards();

if (typeof sessionReadyCallbacks != 'undefined') {
    sessionReadyCallbacks.push(() => llmBackendCards.init());
}

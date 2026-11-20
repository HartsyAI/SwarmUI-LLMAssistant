/**
 * The Prompt styles tab of the settings dialog. A style is written instructions for one model: how to write a prompt for an image or video model (its format, layout rules and examples), or how to
 * instruct a particular chat model. The tab has two sections, "By image model" and "By chat model", each with its styles, the user's own rules that pin a model to a style (or to none), and a
 * preview that says which style a model gets and why. The server decides everything (which style wins, who may change what), so this only shows what it is told and sends changes back.
 * Built from Bootstrap's list-group and badge and SwarmUI's make*Input helpers (as the other settings forms are), so it carries no styling of its own.
 */
class LLMAPromptStyles {

    constructor() {
        /** The styles the server last sent, each { id, kind, name, description, scope, scopes, matches, format, formatter, negative, supports, tasks, guide }. */
        this.styles = [];
        /** The user's rules, each { axis, match, style }. */
        this.overrides = [];
        /** The image and video models core lists, each { name, classId, className, compatId, isVideo }, for the model pickers. */
        this.imageModels = [];
        /** Whether the user may save shared styles. */
        this.canWriteShared = false;
        /** The style being edited as the server sent it, or null for a new one (or when the editor is closed). */
        this.editing = null;
    }

    /** The two sections: which axis each is, its heading, what it is for, and which style kinds it lists. */
    static get SECTIONS() {
        return [
            { axis: 'image', title: 'By image model', kinds: ['image', 'video'],
                help: 'How a prompt is written for the image or video model it is for. A style is picked by the model\'s type, so a model with no style written for it gets the default.' },
            { axis: 'llm', title: 'By chat model', kinds: ['llm'],
                help: 'A short addition on how to instruct the chat model that is doing the writing. A model with no style written for it gets none.' },
        ];
    }

    /** Loads the styles from the server and draws the tab. */
    async load() {
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantGetPromptStyles', {}), translate('Could not load the prompt styles'));
        if (!reply) {
            return;
        }
        this.styles = reply.styles || [];
        this.overrides = reply.overrides || [];
        this.imageModels = reply.imageModels || [];
        this.canWriteShared = !!reply.canWriteShared;
        this.render();
    }

    /** Whether the style in force can be changed from here: a personal one, or a shared one when the user may write the shared layer. */
    canChange(style) {
        return style.scope == 'personal' || (style.scope == 'shared' && this.canWriteShared);
    }

    /** The model names offered when typing a model, for an axis. */
    modelNames(axis) {
        if (axis == 'image') {
            return this.imageModels.map(m => m.name);
        }
        return (LLMAState.availableModels || []).map(m => m.id);
    }

    /** Draws both sections. */
    render() {
        for (let section of LLMAPromptStyles.SECTIONS) {
            let box = document.getElementById(`llma-styles-${section.axis}`);
            if (!box) {
                continue;
            }
            let styles = this.styles.filter(s => section.kinds.includes(s.kind));
            box.innerHTML = this.sectionHtml(section, styles);
            this.wireSection(box, section, styles);
        }
    }

    /** The markup of one section. */
    sectionHtml(section, styles) {
        let axis = section.axis;
        let scopeNames = { bundled: 'Built in', shared: 'Shared', personal: 'Personal' };
        let html = `<h6 class="mt-3">${translate(section.title)}</h6><p class="text-muted small">${translate(section.help)}</p>`;
        html += '<ul class="list-group mb-2">';
        for (let style of styles) {
            let badges = style.scopes.map(s => `<span class="badge text-bg-light">${translate(scopeNames[s] || s)}</span>`).join(' ');
            let conditions = style.matches.length == 0 ? translate('Default') : style.matches.map(m => `${m.kind}:${m.pattern}`).join(', ');
            let editable = this.canChange(style);
            let customized = style.scopes.includes('bundled') && style.scopes.length > 1;
            html += `<li class="list-group-item d-flex align-items-center gap-2 bg-transparent" data-style-id="${escapeHtml(style.id)}">`
                + `<span class="flex-grow-1"><b>${escapeHtml(style.name)}</b> <span class="badge text-bg-secondary">${escapeHtml(style.kind)}</span> ${badges}`
                + `<br><small class="text-muted">${escapeHtml(style.description)}</small>`
                + `<br><small class="text-muted">${translate('Matches')}: ${escapeHtml(conditions)}</small></span>`
                + `<button type="button" class="basic-button llma-style-edit">${editable ? translate('Edit') : translate('Customize')}</button>`
                + (customized && editable ? `<button type="button" class="basic-button llma-style-reset" title="${translate('Remove your copy and use the built-in one again')}">${translate('Reset')}</button>` : '')
                + (editable && !style.scopes.includes('bundled') ? `<button type="button" class="basic-button llma-style-delete">${translate('Delete')}</button>` : '')
                + '</li>';
        }
        if (styles.length == 0) {
            html += `<li class="list-group-item bg-transparent text-muted">${translate('No styles yet.')}</li>`;
        }
        html += '</ul>';
        html += `<button type="button" class="basic-button llma-style-add">${translate('+ Add Style')}</button>`;
        html += this.rulesHtml(axis, styles);
        html += this.previewHtml(axis);
        return html;
    }

    /** The user's rules for an axis, and the form to add one. */
    rulesHtml(axis, styles) {
        let rules = this.overrides.filter(r => r.axis == axis);
        let html = `<h6 class="mt-3">${translate('Your rules')}</h6><p class="text-muted small">${translate('Pin one model, or a group of models by a pattern such as *pony*, to a style, or to none. A rule beats a style\'s own conditions.')}</p>`;
        html += '<ul class="list-group mb-2">';
        for (let rule of rules) {
            let target = rule.style == 'off' ? translate('no style') : escapeHtml(rule.style);
            html += `<li class="list-group-item d-flex align-items-center gap-2 bg-transparent" data-rule-match="${escapeHtml(rule.match)}">`
                + `<span class="flex-grow-1"><code>${escapeHtml(rule.match)}</code> &rarr; ${target}</span>`
                + `<button type="button" class="basic-button llma-rule-delete">${translate('Delete')}</button></li>`;
        }
        if (rules.length == 0) {
            html += `<li class="list-group-item bg-transparent text-muted">${translate('No rules.')}</li>`;
        }
        html += '</ul>';
        let options = styles.map(s => `<option value="${escapeHtml(s.id)}">${escapeHtml(s.name)}</option>`).join('') + `<option value="off">${translate('No style')}</option>`;
        html += `<div class="d-flex gap-2 align-items-center mb-2"><input type="text" class="auto-text-block llma-rule-match" list="llma-styles-models-${axis}" placeholder="${translate('Model name or pattern')}" autocomplete="off">`
            + `<select class="form-select llma-rule-style" style="width:auto;">${options}</select>`
            + `<button type="button" class="basic-button llma-rule-add">${translate('Add rule')}</button></div>`;
        return html;
    }

    /** The preview that says which style a model gets, with the model names as suggestions. */
    previewHtml(axis) {
        let names = this.modelNames(axis).slice(0, 500).map(n => `<option value="${escapeHtml(n)}"></option>`).join('');
        return `<datalist id="llma-styles-models-${axis}">${names}</datalist>`
            + `<h6 class="mt-3">${translate('Which style does a model get?')}</h6>`
            + `<div class="d-flex gap-2 align-items-center mb-2"><input type="text" class="auto-text-block llma-resolve-model" list="llma-styles-models-${axis}" placeholder="${translate('Model name')}" autocomplete="off">`
            + `<button type="button" class="basic-button llma-resolve-run">${translate('Check')}</button></div>`
            + '<div class="llma-resolve-result small mb-3"></div>';
    }

    /** Wires the buttons of a drawn section. */
    wireSection(box, section, styles) {
        let axis = section.axis;
        for (let row of box.querySelectorAll('li[data-style-id]')) {
            let style = styles.find(s => s.id == row.dataset.styleId);
            row.querySelector('.llma-style-edit')?.addEventListener('click', () => this.openEditor(style, axis));
            row.querySelector('.llma-style-reset')?.addEventListener('click', () => this.reset(style));
            row.querySelector('.llma-style-delete')?.addEventListener('click', () => this.remove(style));
        }
        box.querySelector('.llma-style-add')?.addEventListener('click', () => this.openEditor(null, axis));
        for (let row of box.querySelectorAll('li[data-rule-match]')) {
            row.querySelector('.llma-rule-delete').addEventListener('click', () => this.removeRule(axis, row.dataset.ruleMatch));
        }
        box.querySelector('.llma-rule-add')?.addEventListener('click', () => this.addRule(box, axis));
        box.querySelector('.llma-resolve-run')?.addEventListener('click', () => this.resolve(box, axis));
        box.querySelector('.llma-resolve-model')?.addEventListener('keydown', event => {
            if (event.key == 'Enter') {
                this.resolve(box, axis);
            }
        });
    }

    /** Asks the server which style a model gets and shows the answer and the reason. */
    async resolve(box, axis) {
        let model = box.querySelector('.llma-resolve-model').value.trim();
        let out = box.querySelector('.llma-resolve-result');
        if (!model) {
            out.textContent = '';
            return;
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantResolvePromptStyle', { axis: axis, model: model }), translate('Could not check'));
        if (!reply) {
            return;
        }
        let lines = [];
        if (!reply.found) {
            lines.push(`<span class="text-muted">${translate('That model is not listed, so only its name was used.')}</span>`);
        }
        else if (axis == 'image') {
            lines.push(`${translate('Model type')}: <b>${escapeHtml(reply.model.className || translate('unknown'))}</b> <code>${escapeHtml(reply.model.classId || '')}</code>`);
        }
        if (reply.off) {
            lines.push(`<b>${translate('No style')}</b>`);
        }
        else if (reply.style) {
            lines.push(`${translate('Style')}: <b>${escapeHtml(reply.style.name)}</b> (${escapeHtml(reply.style.id)}, ${escapeHtml(reply.style.scope)})`);
        }
        else {
            lines.push(`<b>${translate('No style')}</b>`);
        }
        lines.push(escapeHtml(reply.reason));
        out.innerHTML = lines.join('<br>');
    }

    /** Saves the rules, then draws again. Returns whether the server took them. */
    async saveRules(rules) {
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantSetStyleOverrides', { overrides: rules }), translate('Could not save the rule'));
        if (reply) {
            this.overrides = rules;
        }
        this.render();
        return !!reply;
    }

    /** Adds a rule from a section's form. */
    async addRule(box, axis) {
        let match = box.querySelector('.llma-rule-match').value.trim();
        if (!match) {
            return;
        }
        let style = box.querySelector('.llma-rule-style').value;
        let rules = this.overrides.filter(r => !(r.axis == axis && r.match.toLowerCase() == match.toLowerCase()));
        rules.push({ axis: axis, match: match, style: style });
        await this.saveRules(rules);
    }

    /** Removes a rule. */
    async removeRule(axis, match) {
        await this.saveRules(this.overrides.filter(r => !(r.axis == axis && r.match == match)));
    }

    /** Removes the user's copy of a built-in style after they confirm, so the built-in one is used again. */
    async reset(style) {
        if (!confirm(`${translate('Reset')} ${style.name}?`)) {
            return;
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantDeletePromptStyle', { id: style.id, scope: style.scope }), translate('Could not reset'));
        if (reply) {
            await this.load();
        }
    }

    /** Deletes a style after the user confirms. */
    async remove(style) {
        if (!confirm(`${translate('Delete')} ${style.name}?`)) {
            return;
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantDeletePromptStyle', { id: style.id, scope: style.scope }), translate('Could not delete'));
        if (reply) {
            if (this.editing && this.editing.id == style.id) {
                this.closeEditor();
            }
            await this.load();
        }
    }

    /** Opens the editor on a style, or on a new one when none is given. A built-in style opens as a copy the user will own once they save. */
    async openEditor(style, axis) {
        let value = { id: '', kind: axis == 'llm' ? 'llm' : 'image', name: '', description: '', matches: [], format: 'text', formatter: '', negative: 'none', supports: [], tasks: [], guide: '', body: '', scope: 'personal' };
        if (style) {
            let reply = await llmaUserAction(() => llmaRequest('LLMAssistantGetPromptStyle', { id: style.id, scope: style.scope }), translate('Could not load'));
            if (!reply) {
                return;
            }
            value = { ...value, ...reply.style };
        }
        this.editing = style ? { ...style } : null;
        let editor = document.getElementById('llma-styles-editor');
        let forms = new LLMASettingsForms();
        let kinds = axis == 'llm' ? ['llm'] : ['image', 'video'];
        let kindNames = axis == 'llm' ? ['Chat model'] : ['Image model', 'Video model'];
        let title = style ? (this.canChange(style) ? translate('Edit') : translate('Customize (you will get your own copy)')) : translate('New style');
        let html = forms.group('llma_style_main', title, translate('A style is instructions for the model that writes the prompt. Its conditions say which models it is for; with none it is the default for its kind.'), [
            forms.text('llma-style-id', 'Id', 'Lowercase letters, digits, dashes and underscores. Saving a built-in style\'s id as your own customizes it.', value.id, 'normal'),
            forms.dropdown('llma-style-kind', 'For', 'The kind of model this style is for.', kinds, kinds.includes(value.kind) ? value.kind : kinds[0], kindNames),
            forms.text('llma-style-name', 'Name', 'What it is called in the list.', value.name, 'normal'),
            forms.text('llma-style-description', 'Description', 'One line saying what it is for.', value.description, 'normal'),
            forms.text('llma-style-matches', 'Conditions', 'The models it is for, as kind:pattern separated by commas. An image or video style can use class, compat, exact and glob (for example class:ideogram-4, glob:*pony*); a chat-model style can use exact, glob, family, provider and tag. Leave empty to make it the default.', value.matches.map(m => `${m.kind}:${m.pattern}`).join(', '), 'normal'),
            forms.dropdown('llma-style-format', 'Prompt format', 'What the finished prompt looks like.', ['text', 'tags', 'json'], value.format, ['Plain text', 'Tags', 'JSON']),
            forms.text('llma-style-formatter', 'Formatter', 'The name of a built-in normalizer that fixes the output into the exact shape the model needs. Leave empty for none.', value.formatter, 'normal'),
            forms.dropdown('llma-style-negative', 'Negative prompt', 'Whether the model takes a negative prompt, and where it goes.', ['none', 'separate', 'inline'], value.negative, ['None', 'Separate field', 'In the prompt']),
            forms.text('llma-style-supports', 'Honours', 'What the model honours, so the writer uses only that: weights, break, regions, boxes, text-rendering (comma separated).', value.supports.join(', '), 'normal'),
            forms.text('llma-style-tasks', 'Tasks', 'For a chat-model style: the tasks it applies to (chat, tools, vision, caption, rewrite), comma separated. Empty means all.', value.tasks.join(', '), 'normal'),
            forms.text('llma-style-guide', 'Guide', 'The path of the prompting guide this was written from, for reference.', value.guide, 'normal'),
            forms.text('llma-style-body', 'Instructions', 'The text given to the model that writes the prompt.', value.body, 'big'),
        ]) + (this.canWriteShared ? forms.group('llma_style_scope', translate('Who it applies to'), translate('A shared style is available to every user of this server. A personal one only to you.'), [
            forms.checkbox('llma-style-shared', 'Shared (everyone)', 'Save this in the shared layer.', value.scope == 'shared'),
        ]) : '')
            + `<div class="d-flex gap-2 mt-2"><button type="button" class="basic-button btn-primary" id="llma-style-save">${translate('Save')}</button>`
            + `<button type="button" class="basic-button" id="llma-style-cancel">${translate('Cancel')}</button></div>`;
        editor.innerHTML = html;
        editor.style.display = '';
        // An existing style keeps its id (a new id would be a second style) and its kind.
        document.getElementById('llma-style-id').disabled = !!style;
        document.getElementById('llma-style-kind').disabled = !!style;
        document.getElementById('llma-style-save').addEventListener('click', () => this.saveEditor());
        document.getElementById('llma-style-cancel').addEventListener('click', () => this.closeEditor());
        findParentOfClass(document.getElementById('llma-style-tasks'), 'auto-input').style.display = document.getElementById('llma-style-kind').value == 'llm' ? '' : 'none';
        editor.scrollIntoView({ block: 'nearest' });
    }

    /** Saves the style in the editor. A built-in style the user opened is saved as their own copy (same id, personal). */
    async saveEditor() {
        let get = id => document.getElementById(id);
        let shared = get('llma-style-shared');
        let scope = shared && shared.checked ? 'shared' : 'personal';
        let request = {
            id: get('llma-style-id').value.trim().toLowerCase(),
            kind: get('llma-style-kind').value,
            name: get('llma-style-name').value.trim(),
            description: get('llma-style-description').value.trim(),
            matches: get('llma-style-matches').value.trim(),
            format: get('llma-style-format').value,
            formatter: get('llma-style-formatter').value.trim(),
            negative: get('llma-style-negative').value,
            supports: get('llma-style-supports').value.trim(),
            tasks: get('llma-style-tasks').value.trim(),
            guide: get('llma-style-guide').value.trim(),
            body: get('llma-style-body').value,
            scope: scope,
        };
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantSavePromptStyle', request), translate('Could not save'));
        if (!reply) {
            return;
        }
        // Moving a style between the personal and shared layers saves the new copy first, then removes the old one.
        if (this.editing && (this.editing.scope == 'personal' || this.editing.scope == 'shared') && this.editing.scope != scope) {
            await llmaUserAction(() => llmaRequest('LLMAssistantDeletePromptStyle', { id: this.editing.id, scope: this.editing.scope }), translate('Could not remove the old copy'));
        }
        this.closeEditor();
        await this.load();
    }

    /** Closes the editor. */
    closeEditor() {
        this.editing = null;
        let editor = document.getElementById('llma-styles-editor');
        editor.style.display = 'none';
        editor.innerHTML = '';
    }
}

let llmaPromptStyles = new LLMAPromptStyles();

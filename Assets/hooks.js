/**
 * The Hooks tab of the settings dialog: a list of the hooks that apply to the user, an editor, and a tester that shows what a hook would do
 * to a call without running any tool. The list uses Bootstrap's list-group and badge, and the editor is built from SwarmUI's own make*Input
 * helpers and input groups (as the other settings forms are), so it carries no styling of its own.
 */
class LLMAHooks {

    constructor() {
        /** The hooks last loaded, as the server sent them (each tagged with its _scope). */
        this.hooks = [];
        /** Whether the user may save hooks to the shared layer. */
        this.canWriteShared = false;
        /** Whether the user may create command hooks. */
        this.canCommand = false;
        /** The hook being edited, or null when the editor is closed. */
        this.editing = null;
    }

    /** Loads the hooks from the server and draws the list. */
    async load() {
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantGetHooks', {}), translate('Could not load hooks'));
        if (!reply) {
            return;
        }
        this.hooks = reply.hooks || [];
        this.canWriteShared = !!reply.canWriteShared;
        this.canCommand = !!reply.canCommand;
        this.renderList();
    }

    /** Draws the list of hooks. */
    renderList() {
        let list = document.getElementById('llma-hooks-list');
        if (!list) {
            return;
        }
        if (this.hooks.length == 0) {
            list.innerHTML = `<div class="text-muted py-2">${translate('No hooks yet. A hook is a rule that runs around a tool call: it can refuse it, ask you first, change its arguments, or add a note for the model.')}</div>`;
            return;
        }
        let html = '<ul class="list-group mb-2">';
        for (let hook of this.hooks) {
            let id = escapeHtml(hook.id);
            let scope = hook._scope == 'shared' ? 'text-bg-secondary' : 'text-bg-light';
            // A shared hook can only be changed by someone who may write the shared layer; for everyone else it is read-only.
            let locked = hook._scope == 'shared' && !this.canWriteShared ? ` disabled title="${translate('Only an admin can change a shared hook.')}"` : '';
            html += `<li class="list-group-item d-flex align-items-center gap-2 bg-transparent" data-hook-id="${id}" data-hook-scope="${escapeHtml(hook._scope)}">`
                + `<span class="form-check form-switch toggle-switch m-0"><input class="form-check-input llma-hook-enabled" type="checkbox" ${hook.enabled ? 'checked' : ''}${locked || ` title="${translate('Enabled')}"`} autocomplete="off"></span>`
                + `<span class="flex-grow-1"><b>${escapeHtml(hook.name)}</b> <span class="badge ${scope}">${translate(hook._scope == 'shared' ? 'Shared' : 'Personal')}</span>`
                + `<br><small class="text-muted">${escapeHtml(this.describe(hook))}</small></span>`
                + `<button type="button" class="basic-button llma-hook-edit"${locked}>${translate('Edit')}</button>`
                + `<button type="button" class="basic-button llma-hook-delete"${locked}>${translate('Delete')}</button></li>`;
        }
        html += '</ul>';
        list.innerHTML = html;
        for (let row of list.querySelectorAll('li')) {
            let hook = this.hooks.find(h => h.id == row.dataset.hookId && h._scope == row.dataset.hookScope);
            row.querySelector('.llma-hook-enabled').addEventListener('change', event => this.setEnabled(hook, event.target.checked));
            row.querySelector('.llma-hook-edit').addEventListener('click', () => this.openEditor(hook));
            row.querySelector('.llma-hook-delete').addEventListener('click', () => this.remove(hook));
        }
    }

    /** One line saying what a hook does. */
    describe(hook) {
        let when = hook.phase == 'post' ? translate('After') : translate('Before');
        if (hook.kind == 'command') {
            return `${when} ${hook.tool}: ${translate('runs')} ${hook.command}`;
        }
        let on = hook.argument ? ` ${translate('when')} ${hook.argument}${hook.matchType == 'any' ? ` ${translate('is present')}` : ` ${hook.matchType} ${hook.value}`}` : '';
        let actions = { deny: translate('refuse'), ask: translate('ask first'), rewrite: translate('rewrite the arguments'), note: translate('add a note') };
        return `${when} ${hook.tool}${on}: ${actions[hook.action] || hook.action}`;
    }

    /** Turns a hook on or off from the list. */
    async setEnabled(hook, enabled) {
        let saved = await this.save({ ...hook, enabled: enabled }, hook._scope);
        if (!saved) {
            await this.load();
        }
    }

    /** Deletes a hook after the user confirms. */
    async remove(hook) {
        if (!confirm(`${translate('Delete the hook')} "${hook.name}"?`)) {
            return;
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantDeleteHook', { hookId: hook.id, scope: hook._scope }), translate('Could not delete the hook'));
        if (reply) {
            if (this.editing && this.editing.id == hook.id) {
                this.closeEditor();
            }
            await this.load();
        }
    }

    /** Opens the editor on a hook, or on a new one when none is given. */
    openEditor(hook = null) {
        this.editing = hook ? { ...hook } : null;
        let value = hook || { name: '', enabled: true, order: 0, phase: 'pre', tool: '*', kind: 'rule', argument: '', matchType: 'any', value: '', action: 'deny', message: '', set: {}, command: '', args: [], timeoutSeconds: 10, onError: 'deny', _scope: 'personal' };
        let editor = document.getElementById('llma-hooks-editor');
        let kinds = this.canCommand || value.kind == 'command' ? ['rule', 'command'] : ['rule'];
        let forms = new LLMASettingsForms();
        let html = forms.group('llma_hook_main', hook ? translate('Edit hook') : translate('New hook'), translate('A hook runs around a tool call the model makes. Before a call it can refuse it, ask you first, change its arguments or add a note. After a call it can add a note to the result.'), [
            forms.text('llma-hook-name', 'Name', 'Shown in the list and in what the model is told when this hook blocks a call.', value.name, 'normal'),
            forms.checkbox('llma-hook-enabled', 'Enabled', 'A disabled hook never runs.', value.enabled),
            forms.dropdown('llma-hook-phase', 'When', 'Before the tool runs, or after it has run.', ['pre', 'post'], value.phase, ['Before the tool', 'After the tool']),
            forms.text('llma-hook-tool', 'Tool', 'Which tool it applies to: a tool id such as file_write, or a pattern such as file_* (* is any text, ? is any one character). * means every tool.', value.tool, 'normal'),
            forms.dropdown('llma-hook-kind', 'Kind', 'A rule is plain data. A command runs a program on the server, which needs the LLM Hook Command permission.', kinds, value.kind, kinds.map(k => k == 'rule' ? 'Rule' : 'Command (runs a program)')),
            forms.number('llma-hook-order', 'Order', 'Hooks run lowest first, shared hooks before personal ones.', value.order, -1000, 1000, 1),
        ]) + forms.group('llma_hook_rule', translate('Rule'), translate('When the rule applies and what it does.'), [
            forms.text('llma-hook-argument', 'Argument', 'Optional. The argument to look at, for example path or command. * looks at all of them. Leave empty to apply to every call of the tool.', value.argument, 'normal'),
            forms.dropdown('llma-hook-matchtype', 'Compare as', 'How the argument is compared with the value: it is present, matches a glob (* and ?), matches a regular expression, or contains the text.', ['any', 'glob', 'regex', 'contains'], value.matchType, ['Is present', 'Glob', 'Regular expression', 'Contains']),
            forms.text('llma-hook-value', 'Value', 'What the argument is compared with.', value.value, 'normal'),
            forms.dropdown('llma-hook-action', 'Then', 'Refuse the call, ask you first whatever the approval mode is, rewrite its arguments, or add a note for the model.', ['deny', 'ask', 'rewrite', 'note'], value.action, ['Refuse the call', 'Ask me first', 'Rewrite the arguments', 'Add a note']),
            forms.text('llma-hook-message', 'Message', 'The reason the model is given when the call is refused, or the text of the note.', value.message, 'big'),
            forms.text('llma-hook-set', 'Arguments to set', 'A JSON object of arguments to set on the call, for example {"overwrite": false}.', JSON.stringify(value.set || {}), 'big'),
        ]) + forms.group('llma_hook_command', translate('Command'), translate('The program receives the call as JSON on standard input and answers with JSON on standard output: {"decision": "allow|deny|ask", "message": "...", "arguments": {...}, "note": "..."}. Exit code 2 refuses the call with what it wrote to standard error. Anything else counts as a failure.'), [
            forms.text('llma-hook-command', 'Program', 'The program to run. No shell is used.', value.command, 'normal'),
            forms.text('llma-hook-args', 'Arguments', 'One argument per line.', (value.args || []).join('\n'), 'big'),
            forms.number('llma-hook-timeout', 'Time limit (seconds)', 'The program is killed if it runs longer.', value.timeoutSeconds, 1, 60, 1),
            forms.dropdown('llma-hook-onerror', 'If it fails', 'What to do when the program cannot run or fails: refuse the call (safe) or let it through.', ['deny', 'allow'], value.onError, ['Refuse the call', 'Let the call through']),
        ]) + (this.canWriteShared ? forms.group('llma_hook_scope', translate('Who it applies to'), translate('A shared hook applies to every user of this server. A personal hook applies only to you.'), [
            forms.checkbox('llma-hook-shared', 'Shared (everyone)', 'Save this hook in the shared layer.', value._scope == 'shared'),
        ]) : '')
            + this.testerHtml(forms)
            + `<div class="d-flex gap-2 mt-2"><button type="button" class="basic-button btn-primary" id="llma-hook-save">${translate('Save hook')}</button>`
            + `<button type="button" class="basic-button" id="llma-hook-cancel">${translate('Cancel')}</button></div>`;
        editor.innerHTML = html;
        editor.style.display = '';
        for (let id of ['llma-hook-phase', 'llma-hook-kind', 'llma-hook-matchtype', 'llma-hook-action', 'llma-hook-argument']) {
            document.getElementById(id).addEventListener('change', () => this.updateVisibility());
            document.getElementById(id).addEventListener('input', () => this.updateVisibility());
        }
        document.getElementById('llma-hook-save').addEventListener('click', () => this.saveEditor());
        document.getElementById('llma-hook-cancel').addEventListener('click', () => this.closeEditor());
        document.getElementById('llma-hook-test').addEventListener('click', () => this.test());
        document.getElementById('llma-hooks-add').style.display = 'none';
        this.updateVisibility();
        editor.scrollIntoView({ block: 'nearest' });
    }

    /** The tester: a sample call and a button that shows what the hook being edited would do to it. */
    testerHtml(forms) {
        return forms.group('llma_hook_test', translate('Try it'), translate('Shows what this hook would do to a sample call, without running any tool.'), [
            forms.text('llma-hook-test-tool', 'Tool', 'The tool the sample call is for.', 'file_write', 'normal'),
            forms.text('llma-hook-test-args', 'Arguments', 'The sample call\'s arguments, as a JSON object.', '{"path": "notes.txt", "content": "hello"}', 'big'),
            forms.text('llma-hook-test-result', 'Result', 'For an "after" hook: the sample result, as a JSON object.', '{"success": true}', 'big'),
        ]) + `<div class="my-2"><button type="button" class="basic-button" id="llma-hook-test">${translate('Test')}</button><pre class="mt-2 mb-0" id="llma-hook-test-out"></pre></div>`;
    }

    /** Shows only the fields that matter for the hook's kind, phase and action. */
    updateVisibility() {
        let get = id => document.getElementById(id);
        let show = (id, visible) => {
            let box = findParentOfClass(get(id), 'auto-input');
            if (box) {
                box.style.display = visible ? '' : 'none';
            }
        };
        let showGroup = (id, visible) => {
            let group = get(`auto-group-${id}`);
            if (group) {
                group.style.display = visible ? '' : 'none';
            }
        };
        let isCommand = get('llma-hook-kind').value == 'command';
        let isPost = get('llma-hook-phase').value == 'post';
        // A post hook can only add a note, so its action list is just that.
        let action = get('llma-hook-action');
        let wanted = isPost ? ['note'] : ['deny', 'ask', 'rewrite', 'note'];
        for (let option of action.options) {
            option.hidden = !wanted.includes(option.value);
            option.disabled = option.hidden;
        }
        if (!wanted.includes(action.value)) {
            action.value = wanted[0];
        }
        showGroup('llma_hook_rule', !isCommand);
        showGroup('llma_hook_command', isCommand);
        let hasArgument = get('llma-hook-argument').value.trim() != '';
        show('llma-hook-matchtype', hasArgument);
        show('llma-hook-value', hasArgument && get('llma-hook-matchtype').value != 'any');
        show('llma-hook-message', ['deny', 'note'].includes(action.value));
        show('llma-hook-set', action.value == 'rewrite');
        show('llma-hook-test-result', isPost);
    }

    /** Collects the editor's fields into the hook JSON the server stores. Returns null (with a toast) when a field is not valid. */
    collect() {
        let get = id => document.getElementById(id);
        let set = {};
        let setText = get('llma-hook-set').value.trim();
        if (setText) {
            try {
                set = JSON.parse(setText);
            }
            catch {
                llmaShowToast(translate('"Arguments to set" must be a JSON object, for example {"overwrite": false}.'), 'error');
                return null;
            }
        }
        return {
            id: this.editing ? this.editing.id : '',
            name: get('llma-hook-name').value.trim(),
            enabled: get('llma-hook-enabled').checked,
            order: parseInt(get('llma-hook-order').value, 10) || 0,
            phase: get('llma-hook-phase').value,
            tool: get('llma-hook-tool').value.trim() || '*',
            kind: get('llma-hook-kind').value,
            argument: get('llma-hook-argument').value.trim(),
            matchType: get('llma-hook-matchtype').value,
            value: get('llma-hook-value').value,
            action: get('llma-hook-action').value,
            message: get('llma-hook-message').value,
            set: set,
            command: get('llma-hook-command').value.trim(),
            args: get('llma-hook-args').value.split('\n').map(line => line.trim()).filter(line => line != ''),
            timeoutSeconds: parseInt(get('llma-hook-timeout').value, 10) || 10,
            onError: get('llma-hook-onerror').value,
        };
    }

    /** Saves the hook in the editor. */
    async saveEditor() {
        let hook = this.collect();
        if (!hook) {
            return;
        }
        let shared = document.getElementById('llma-hook-shared');
        let scope = shared && shared.checked ? 'shared' : 'personal';
        if (this.editing && this.editing._scope != scope) {
            // Moving a hook between layers saves the new copy first, then removes the old one.
            let moved = await this.save({ ...hook, id: '' }, scope);
            if (moved) {
                await llmaUserAction(() => llmaRequest('LLMAssistantDeleteHook', { hookId: this.editing.id, scope: this.editing._scope }), translate('Could not remove the old copy'));
            }
        }
        else {
            await this.save(hook, scope);
        }
        this.closeEditor();
        await this.load();
    }

    /** Sends one hook to the server. Returns whether it was saved. */
    async save(hook, scope) {
        let clean = { ...hook };
        delete clean._scope;
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantSaveHook', { hook: JSON.stringify(clean), scope: scope }), translate('Could not save the hook'));
        return !!reply;
    }

    /** Closes the editor. */
    closeEditor() {
        this.editing = null;
        let editor = document.getElementById('llma-hooks-editor');
        editor.style.display = 'none';
        editor.innerHTML = '';
        document.getElementById('llma-hooks-add').style.display = '';
    }

    /** Shows what the hook being edited would do to the sample call. */
    async test() {
        let hook = this.collect();
        if (!hook) {
            return;
        }
        let output = document.getElementById('llma-hook-test-out');
        output.textContent = translate('Testing…');
        let reply;
        try {
            reply = await llmaRequest('LLMAssistantTestHook', {
                hook: JSON.stringify(hook),
                toolId: document.getElementById('llma-hook-test-tool').value.trim(),
                arguments: document.getElementById('llma-hook-test-args').value,
                result: document.getElementById('llma-hook-test-result').value,
            });
        }
        catch (e) {
            output.textContent = `${translate('Test failed')}: ${llmaShortError(e)}`;
            return;
        }
        if (!reply.success) {
            output.textContent = reply.error || translate('Test failed');
            return;
        }
        let lines = [];
        if (reply.phase == 'post') {
            lines.push(reply.notes.length > 0 ? `${translate('Notes added to the result')}: ${reply.notes.join(' | ')}` : translate('Nothing added to the result.'));
        }
        else if (reply.denied) {
            lines.push(`${translate('Refused')}: ${reply.message}`);
        }
        else {
            lines.push(reply.forceAsk ? translate('Would ask you first.') : translate('Would let the call through.'));
            lines.push(`${translate('Arguments')}: ${JSON.stringify(reply.arguments)}`);
            if (reply.notes.length > 0) {
                lines.push(`${translate('Notes')}: ${reply.notes.join(' | ')}`);
            }
        }
        output.textContent = lines.join('\n');
    }
}

let llmaHooks = new LLMAHooks();

/** Wires the Hooks tab's own buttons. Called once when the settings dialog is set up. */
function llmaSetupHooksTab() {
    let add = document.getElementById('llma-hooks-add');
    if (add) {
        add.addEventListener('click', () => llmaHooks.openEditor(null));
    }
}

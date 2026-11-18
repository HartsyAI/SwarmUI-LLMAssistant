/**
 * The Skills tab of the settings dialog: the skills and commands the user can use, an editor for their own (and the shared ones, for an admin), and a switch per
 * workspace for whether its own skills and commands are used. Built from Bootstrap's list-group, badge and form-switch and SwarmUI's make*Input helpers (as the other
 * settings forms are), so it carries no styling of its own.
 */
class LLMASkillsSettings {

    constructor() {
        /** The asset being edited as the server sent it, or null for a new one (or when the editor is closed). */
        this.editing = null;
    }

    /** Loads the list from the server and draws it. */
    async load() {
        await llmaPromptAssets.refresh();
        this.renderList();
        this.renderTrust();
    }

    /** Whether the asset can be changed from here: a personal one, or a shared one when the user may write the shared layer. */
    canChange(asset) {
        return asset.scope == 'personal' || (asset.scope == 'shared' && llmaPromptAssets.canWriteShared);
    }

    /** Draws the list of skills and commands. */
    renderList() {
        let list = document.getElementById('llma-skills-list');
        if (!list) {
            return;
        }
        let assets = llmaPromptAssets.assets;
        if (assets.length == 0) {
            list.innerHTML = `<div class="text-muted py-2">${translate('No skills or commands yet. A command is a prompt you run by typing / and its name; a skill is instructions the assistant can load when a task matches.')}</div>`;
            return;
        }
        let scopeNames = { shared: 'Shared', personal: 'Personal', bundled: 'Built in', workspace: 'Workspace' };
        let html = '<ul class="list-group mb-2">';
        for (let asset of assets) {
            let id = escapeHtml(asset.name);
            let kind = asset.kind == 'skill' ? translate('Skill') : translate('Command');
            let editable = this.canChange(asset);
            html += `<li class="list-group-item d-flex align-items-center gap-2 bg-transparent" data-asset-kind="${escapeHtml(asset.kind)}" data-asset-name="${id}" data-asset-scope="${escapeHtml(asset.scope)}">`
                + `<span class="flex-grow-1"><b>/${id}</b> <span class="badge text-bg-secondary">${kind}</span> <span class="badge text-bg-light">${translate(scopeNames[asset.scope] || asset.scope)}</span>`
                + (asset.userOnly ? ` <span class="badge text-bg-light" title="${translate('The assistant cannot load this on its own.')}">${translate('You only')}</span>` : '')
                + `<br><small class="text-muted">${escapeHtml(asset.argumentHint ? `${asset.argumentHint} ${asset.description}` : asset.description)}</small></span>`
                + (editable ? `<button type="button" class="basic-button llma-skill-edit">${translate('Edit')}</button><button type="button" class="basic-button llma-skill-delete">${translate('Delete')}</button>` : '')
                + '</li>';
        }
        html += '</ul>';
        list.innerHTML = html;
        for (let row of list.querySelectorAll('li')) {
            let asset = assets.find(a => a.kind == row.dataset.assetKind && a.name == row.dataset.assetName && a.scope == row.dataset.assetScope);
            row.querySelector('.llma-skill-edit')?.addEventListener('click', () => this.openEditor(asset));
            row.querySelector('.llma-skill-delete')?.addEventListener('click', () => this.remove(asset));
        }
    }

    /** Draws the switches for trusting a workspace's own skills and commands (only for a user who can use code mode, so who has workspaces). */
    renderTrust() {
        let box = document.getElementById('llma-skills-trust');
        if (!box) {
            return;
        }
        let workspaces = llmaPromptAssets.workspaces;
        if (workspaces.length == 0) {
            box.innerHTML = '';
            return;
        }
        let html = `<h6 class="mt-3">${translate('Workspace skills and commands')}</h6>`
            + `<p class="text-muted small">${translate('A workspace can hold its own skills and commands in .llma/ or .claude/. They are used in a code chat only when you trust that workspace, because the assistant can write files there and what is in them goes into its instructions.')}</p>`
            + '<ul class="list-group mb-2">';
        for (let workspace of workspaces) {
            html += `<li class="list-group-item d-flex align-items-center gap-2 bg-transparent" data-workspace-id="${escapeHtml(workspace.id)}">`
                + `<span class="form-check form-switch toggle-switch m-0"><input class="form-check-input llma-workspace-trust" type="checkbox" ${workspace.trusted ? 'checked' : ''} title="${translate('Trust this workspace')}" autocomplete="off"></span>`
                + `<span class="flex-grow-1">${escapeHtml(workspace.label)}</span></li>`;
        }
        html += '</ul>';
        box.innerHTML = html;
        for (let row of box.querySelectorAll('li')) {
            row.querySelector('.llma-workspace-trust').addEventListener('change', event => this.setTrust(row.dataset.workspaceId, event.target.checked));
        }
    }

    /** Trusts or stops trusting a workspace. */
    async setTrust(workspaceId, trusted) {
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantSetWorkspaceTrust', { workspaceId: workspaceId, trusted: trusted }), translate('Could not change the workspace'));
        if (!reply) {
            await this.load();
            return;
        }
        let workspace = llmaPromptAssets.workspaces.find(w => w.id == workspaceId);
        if (workspace) {
            workspace.trusted = trusted;
        }
        llmaPromptAssets.loadedAt = 0;
    }

    /** Deletes an asset after the user confirms. */
    async remove(asset) {
        if (!confirm(`${translate('Delete')} /${asset.name}?`)) {
            return;
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantDeletePromptAsset', { kind: asset.kind, name: asset.name, scope: asset.scope }), translate('Could not delete'));
        if (reply) {
            if (this.editing && this.editing.name == asset.name && this.editing.kind == asset.kind) {
                this.closeEditor();
            }
            await this.load();
        }
    }

    /** Opens the editor on an asset, or on a new one when none is given. */
    async openEditor(asset = null) {
        let value = { kind: 'command', name: '', description: '', argumentHint: '', body: '', userOnly: false, scope: 'personal' };
        if (asset) {
            let reply = await llmaUserAction(() => llmaRequest('LLMAssistantGetPromptAsset', { kind: asset.kind, name: asset.name, scope: asset.scope }), translate('Could not load'));
            if (!reply) {
                return;
            }
            value = { ...value, ...reply.asset };
        }
        this.editing = asset ? { ...asset } : null;
        let editor = document.getElementById('llma-skills-editor');
        let forms = new LLMASettingsForms();
        let html = forms.group('llma_skill_main', asset ? translate('Edit') : translate('New skill or command'), translate('A command is a prompt template you run by typing / and its name; $ARGUMENTS in it is replaced by what you type after the name. A skill is instructions the assistant can load when a task matches its description.'), [
            forms.dropdown('llma-skill-kind', 'Kind', 'A command runs when you type its name. A skill is also offered to the assistant, which loads it when the description fits.', ['command', 'skill'], value.kind, ['Command (you run it)', 'Skill (you or the assistant)']),
            forms.text('llma-skill-name', 'Name', 'Lowercase letters, digits, dashes and underscores. You type it after the slash.', value.name, 'normal'),
            forms.text('llma-skill-description', 'Description', 'One line. For a skill this is what the assistant reads to decide whether to load it, so say when it applies.', value.description, 'normal'),
            forms.text('llma-skill-hint', 'Argument hint', 'Optional. What to type after the name, shown in the picker, for example [file] [what to check].', value.argumentHint, 'normal'),
            forms.text('llma-skill-body', 'Text', 'The prompt (a command) or the instructions (a skill). Use $ARGUMENTS where what you type after the name should go.', value.body, 'big'),
            forms.checkbox('llma-skill-useronly', 'Only I can run it', 'For a skill: the assistant is not told about it and cannot load it, only you can run it by name.', value.userOnly),
        ]) + (llmaPromptAssets.canWriteShared ? forms.group('llma_skill_scope', translate('Who it applies to'), translate('A shared one is available to every user of this server. A personal one only to you.'), [
            forms.checkbox('llma-skill-shared', 'Shared (everyone)', 'Save this in the shared layer.', value.scope == 'shared'),
        ]) : '')
            + `<div class="d-flex gap-2 mt-2"><button type="button" class="basic-button btn-primary" id="llma-skill-save">${translate('Save')}</button>`
            + `<button type="button" class="basic-button" id="llma-skill-cancel">${translate('Cancel')}</button></div>`;
        editor.innerHTML = html;
        editor.style.display = '';
        // An existing one keeps its kind and name: renaming would be a new one, and the old would stay.
        document.getElementById('llma-skill-kind').disabled = !!asset;
        document.getElementById('llma-skill-name').disabled = !!asset;
        document.getElementById('llma-skill-kind').addEventListener('change', () => this.updateVisibility());
        document.getElementById('llma-skill-save').addEventListener('click', () => this.saveEditor());
        document.getElementById('llma-skill-cancel').addEventListener('click', () => this.closeEditor());
        document.getElementById('llma-skills-add').style.display = 'none';
        this.updateVisibility();
        editor.scrollIntoView({ block: 'nearest' });
    }

    /** Shows the fields that apply to the chosen kind. */
    updateVisibility() {
        let isSkill = document.getElementById('llma-skill-kind').value == 'skill';
        let show = (id, visible) => {
            let box = findParentOfClass(document.getElementById(id), 'auto-input');
            if (box) {
                box.style.display = visible ? '' : 'none';
            }
        };
        show('llma-skill-hint', !isSkill);
        show('llma-skill-useronly', isSkill);
    }

    /** Saves the asset in the editor. */
    async saveEditor() {
        let get = id => document.getElementById(id);
        let shared = get('llma-skill-shared');
        let scope = shared && shared.checked ? 'shared' : 'personal';
        let kind = get('llma-skill-kind').value;
        let request = {
            kind: kind,
            name: get('llma-skill-name').value.trim().toLowerCase(),
            description: get('llma-skill-description').value.trim(),
            argumentHint: kind == 'command' ? get('llma-skill-hint').value.trim() : '',
            body: get('llma-skill-body').value,
            userOnly: kind == 'skill' && get('llma-skill-useronly').checked,
            scope: scope,
        };
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantSavePromptAsset', request), translate('Could not save'));
        if (!reply) {
            return;
        }
        // Moving one between the personal and shared layers saves the new copy first, then removes the old one.
        if (this.editing && this.editing.scope != scope) {
            await llmaUserAction(() => llmaRequest('LLMAssistantDeletePromptAsset', { kind: this.editing.kind, name: this.editing.name, scope: this.editing.scope }), translate('Could not remove the old copy'));
        }
        this.closeEditor();
        await this.load();
    }

    /** Closes the editor. */
    closeEditor() {
        this.editing = null;
        let editor = document.getElementById('llma-skills-editor');
        editor.style.display = 'none';
        editor.innerHTML = '';
        document.getElementById('llma-skills-add').style.display = '';
    }
}

let llmaSkillsSettings = new LLMASkillsSettings();

/** Wires the Skills tab's own button. Called once when the settings dialog is set up. */
function llmaSetupSkillsTab() {
    let add = document.getElementById('llma-skills-add');
    if (add) {
        add.addEventListener('click', () => llmaSkillsSettings.openEditor(null));
    }
}

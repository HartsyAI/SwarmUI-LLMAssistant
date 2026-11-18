/**
 * The MCP Servers tab of the settings dialog: the servers an admin has set up, where each connection stands, the tools each offers (and a switch
 * for each), and an editor with a test button. Like the Hooks tab it is Bootstrap's list-group, badge and form-switch plus SwarmUI's own make*Input
 * helpers, so it carries no styling of its own. Anyone who can open settings sees the list; only someone who may manage servers can change it.
 */
class LLMAMcp {

    constructor() {
        /** The servers last loaded, as the server sent them. */
        this.servers = [];
        /** Whether the user may add, change and remove servers. */
        this.canManage = false;
        /** Whether the user may call the tools. */
        this.canCall = false;
        /** The server being edited, or null for a new one (or when the editor is closed). */
        this.editing = null;
        /** The poll that keeps the status current while a server is starting. */
        this.pollTimer = null;
        /** The text a stored secret is shown as; sending it back keeps the stored value. */
        this.hidden = '••••••';
    }

    /** Loads the servers from the server and draws the list. */
    async load() {
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantGetMcpServers', {}), translate('Could not load MCP servers'));
        if (!reply) {
            return;
        }
        this.servers = reply.servers || [];
        this.canManage = !!reply.canManage;
        this.canCall = !!reply.canCall;
        document.getElementById('llma-mcp-add').style.display = this.canManage && !this.editing ? '' : 'none';
        this.renderList();
        this.schedulePoll();
    }

    /** While any server is still starting, looks again in a couple of seconds (and stops once the tab is closed or all are settled). */
    schedulePoll() {
        clearTimeout(this.pollTimer);
        let panel = document.getElementById('llma-settings-mcp');
        let starting = this.servers.some(server => server.state == 'connecting');
        if (!starting || !panel || panel.style.display == 'none') {
            return;
        }
        this.pollTimer = setTimeout(() => this.load(), 2000);
    }

    /** Draws the list of servers, each with its status and a fold-out of its tools. */
    renderList() {
        let list = document.getElementById('llma-mcp-list');
        if (!list) {
            return;
        }
        if (this.servers.length == 0) {
            let hint = this.canManage
                ? translate('No MCP servers yet. An MCP server gives the assistant extra tools: a program on this machine (stdio) or a URL (http). Add one to get started.')
                : translate('No MCP servers have been set up. An admin can add them here.');
            list.innerHTML = `<div class="text-muted py-2">${hint}</div>`;
            return;
        }
        let states = {
            ready: ['text-bg-success', translate('Ready')],
            connecting: ['text-bg-warning', translate('Connecting')],
            failed: ['text-bg-danger', translate('Failed')],
            disabled: ['text-bg-secondary', translate('Off')],
        };
        let html = '<ul class="list-group mb-2">';
        for (let server of this.servers) {
            let id = escapeHtml(server.id);
            let [badge, label] = states[server.state] || states.disabled;
            let locked = this.canManage ? '' : ' disabled';
            let detail = server.transport == 'http' ? server.url : server.command;
            let error = server.state == 'failed' && server.error ? `<br><small class="text-danger llma-mcp-error">${escapeHtml(server.error)}</small>` : '';
            let toolRows = (server.tools || []).map(tool => {
                let kind = tool.readOnly ? translate('read-only') : (tool.openWorld ? translate('network') : translate('changes things'));
                return `<li class="list-group-item d-flex align-items-center gap-2 bg-transparent py-1" data-tool-name="${escapeHtml(tool.name)}">`
                    + `<span class="form-check form-switch toggle-switch m-0"><input class="form-check-input llma-mcp-tool-enabled" type="checkbox" ${tool.enabled ? 'checked' : ''}${locked} autocomplete="off"></span>`
                    + `<span class="flex-grow-1"><code>${escapeHtml(tool.id)}</code> <span class="badge text-bg-light">${kind}</span><br><small class="text-muted">${escapeHtml(tool.description || '')}</small></span></li>`;
            }).join('');
            let tools = (server.tools || []).length > 0
                ? `<details class="mt-1"><summary class="small text-muted llma-mcp-tools-summary">${(server.tools || []).length} ${translate('tools')}</summary><ul class="list-group mt-1">${toolRows}</ul></details>`
                : '';
            html += `<li class="list-group-item bg-transparent" data-server-id="${id}">`
                + '<div class="d-flex align-items-center gap-2">'
                + `<span class="form-check form-switch toggle-switch m-0"><input class="form-check-input llma-mcp-enabled" type="checkbox" ${server.enabled ? 'checked' : ''}${locked} title="${translate('Enabled')}" autocomplete="off"></span>`
                + `<span class="flex-grow-1"><b>${escapeHtml(server.name)}</b> <span class="badge ${badge} llma-mcp-state">${label}</span> <span class="badge text-bg-light">${escapeHtml(server.transport)}</span>`
                + `<br><small class="text-muted">${escapeHtml(detail || '')}</small>${error}</span>`
                + (this.canManage
                    ? `<button type="button" class="basic-button llma-mcp-reconnect">${translate('Reconnect')}</button>`
                    + `<button type="button" class="basic-button llma-mcp-edit">${translate('Edit')}</button>`
                    + `<button type="button" class="basic-button llma-mcp-delete">${translate('Delete')}</button>`
                    : '')
                + `</div>${tools}</li>`;
        }
        html += '</ul>';
        if (!this.canCall) {
            html += `<div class="text-muted small">${translate('You do not have permission to call MCP tools, so they are not offered to you.')}</div>`;
        }
        list.innerHTML = html;
        for (let row of list.querySelectorAll(':scope > ul > li')) {
            let server = this.servers.find(s => s.id == row.dataset.serverId);
            row.querySelector('.llma-mcp-enabled').addEventListener('change', event => this.setEnabled(server, event.target.checked));
            row.querySelector('.llma-mcp-reconnect')?.addEventListener('click', () => this.reconnect(server));
            row.querySelector('.llma-mcp-edit')?.addEventListener('click', () => this.openEditor(server));
            row.querySelector('.llma-mcp-delete')?.addEventListener('click', () => this.remove(server));
            for (let toolRow of row.querySelectorAll('[data-tool-name]')) {
                toolRow.querySelector('.llma-mcp-tool-enabled').addEventListener('change', event => this.setToolEnabled(server, toolRow.dataset.toolName, event.target.checked));
            }
        }
    }

    /** Turns a whole server on or off. */
    async setEnabled(server, enabled) {
        await this.save({ ...server, enabled: enabled });
        await this.load();
    }

    /** Turns one of a server's tools on or off. */
    async setToolEnabled(server, toolName, enabled) {
        let off = new Set(server.disabledTools || []);
        if (enabled) {
            off.delete(toolName);
        }
        else {
            off.add(toolName);
        }
        await this.save({ ...server, disabledTools: [...off] });
        await this.load();
    }

    /** Closes the server's connection and connects it again now. */
    async reconnect(server) {
        llmaShowToast(`${translate('Reconnecting')} ${server.name}…`, 'info');
        await llmaUserAction(() => llmaRequest('LLMAssistantReconnectMcpServer', { serverId: server.id }), translate('Could not reconnect'));
        await this.load();
    }

    /** Deletes a server after the user confirms. */
    async remove(server) {
        if (!confirm(`${translate('Delete the MCP server')} "${server.name}"?`)) {
            return;
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantDeleteMcpServer', { serverId: server.id }), translate('Could not delete the server'));
        if (reply) {
            if (this.editing && this.editing.id == server.id) {
                this.closeEditor();
            }
            await this.load();
        }
    }

    /** Opens the editor on a server, or on a new one when none is given. */
    openEditor(server = null) {
        this.editing = server ? { ...server } : null;
        let value = server || { id: '', name: '', enabled: true, transport: 'stdio', command: '', args: [], env: {}, cwd: '', url: '', headers: {}, timeoutSeconds: 60, disabledTools: [] };
        let editor = document.getElementById('llma-mcp-editor');
        let forms = new LLMASettingsForms();
        let envText = Object.entries(value.env || {}).map(([name, secret]) => `${name}=${secret}`).join('\n');
        let headerText = Object.entries(value.headers || {}).map(([name, secret]) => `${name}: ${secret}`).join('\n');
        editor.innerHTML = forms.group('llma_mcp_main', server ? translate('Edit MCP server') : translate('New MCP server'), translate('An MCP server gives the assistant extra tools. Setting one up needs the LLM MCP Manage permission, because a stdio server is a program started on this machine.'), [
            forms.text('llma-mcp-id', 'Id', 'Short name, used in the tool ids the model sees (mcp__id__tool). Lowercase letters, digits, dashes and underscores. Cannot be changed later.', value.id, 'normal'),
            forms.text('llma-mcp-name', 'Name', 'Shown in the list.', value.name, 'normal'),
            forms.checkbox('llma-mcp-enabled', 'Enabled', 'A switched-off server is not connected and offers no tools.', value.enabled),
            forms.dropdown('llma-mcp-transport', 'Transport', 'stdio starts a program on this machine and talks to it through its standard input and output. http connects to a server that speaks the Streamable HTTP transport.', ['stdio', 'http'], value.transport, ['stdio (a program on this machine)', 'http (a URL)']),
            forms.number('llma-mcp-timeout', 'Time limit (seconds)', 'How long one tool call may take before it is given up on.', value.timeoutSeconds, 1, 600, 1),
            forms.checkbox('llma-mcp-ask', 'Always ask first', 'Ask before every call to this server\'s tools in Ask mode, and refuse them in Plan mode, even the ones the server says are read-only. Use it for a server you do not fully trust to describe its own tools.', value.alwaysAsk),
        ]) + forms.group('llma_mcp_stdio', translate('Program'), translate('The program is started with a small safe environment (not this server\'s own), plus the variables below. No shell is used.'), [
            forms.text('llma-mcp-command', 'Program', 'What to run, for example npx or node. Found through the PATH.', value.command, 'normal'),
            forms.text('llma-mcp-args', 'Arguments', 'One argument per line.', (value.args || []).join('\n'), 'big'),
            forms.text('llma-mcp-env', 'Environment', 'One NAME=value per line. Values are secrets: they are never shown again, and a line left as it is keeps its stored value.', envText, 'big'),
            forms.text('llma-mcp-cwd', 'Folder', 'The folder to run in. Empty for this server\'s own.', value.cwd, 'normal'),
        ]) + forms.group('llma_mcp_http', translate('URL'), translate('Where the server listens.'), [
            forms.text('llma-mcp-url', 'URL', 'The full address, for example https://example.com/mcp.', value.url, 'normal'),
            forms.text('llma-mcp-headers', 'Headers', 'One Name: value per line, for example Authorization: Bearer … Values are secrets, as with environment variables.', headerText, 'big'),
        ]) + `<div class="my-2"><div class="d-flex gap-2"><button type="button" class="basic-button" id="llma-mcp-test">${translate('Test')}</button>`
            + `<button type="button" class="basic-button btn-primary" id="llma-mcp-save">${translate('Save server')}</button>`
            + `<button type="button" class="basic-button" id="llma-mcp-cancel">${translate('Cancel')}</button></div>`
            + '<pre class="mt-2 mb-0" id="llma-mcp-test-out"></pre></div>';
        editor.style.display = '';
        document.getElementById('llma-mcp-id').disabled = !!server;
        document.getElementById('llma-mcp-transport').addEventListener('change', () => this.updateVisibility());
        document.getElementById('llma-mcp-save').addEventListener('click', () => this.saveEditor());
        document.getElementById('llma-mcp-cancel').addEventListener('click', () => this.closeEditor());
        document.getElementById('llma-mcp-test').addEventListener('click', () => this.test());
        document.getElementById('llma-mcp-add').style.display = 'none';
        this.updateVisibility();
        editor.scrollIntoView({ block: 'nearest' });
    }

    /** Shows the group that matches the transport. */
    updateVisibility() {
        let http = document.getElementById('llma-mcp-transport').value == 'http';
        document.getElementById('auto-group-llma_mcp_stdio').style.display = http ? 'none' : '';
        document.getElementById('auto-group-llma_mcp_http').style.display = http ? '' : 'none';
    }

    /** Collects the editor's fields into the server JSON. */
    collect() {
        let get = id => document.getElementById(id);
        let lines = text => text.split('\n').map(line => line.trim()).filter(line => line != '');
        let env = {};
        for (let line of lines(get('llma-mcp-env').value)) {
            let at = line.indexOf('=');
            if (at > 0) {
                env[line.substring(0, at).trim()] = line.substring(at + 1);
            }
        }
        let headers = {};
        for (let line of lines(get('llma-mcp-headers').value)) {
            let at = line.indexOf(':');
            if (at > 0) {
                headers[line.substring(0, at).trim()] = line.substring(at + 1).trim();
            }
        }
        return {
            id: this.editing ? this.editing.id : get('llma-mcp-id').value.trim().toLowerCase(),
            name: get('llma-mcp-name').value.trim(),
            enabled: get('llma-mcp-enabled').checked,
            transport: get('llma-mcp-transport').value,
            command: get('llma-mcp-command').value.trim(),
            args: lines(get('llma-mcp-args').value),
            env: env,
            cwd: get('llma-mcp-cwd').value.trim(),
            url: get('llma-mcp-url').value.trim(),
            headers: headers,
            timeoutSeconds: parseInt(get('llma-mcp-timeout').value, 10) || 60,
            alwaysAsk: get('llma-mcp-ask').checked,
            disabledTools: this.editing ? (this.editing.disabledTools || []) : [],
        };
    }

    /** Saves the server in the editor. A server that saved but could not connect says why. */
    async saveEditor() {
        let reply = await this.save(this.collect());
        if (!reply) {
            return;
        }
        this.closeEditor();
        await this.load();
        if (reply.server && reply.server.state == 'failed') {
            llmaShowToast(`${translate('Saved, but it could not connect')}: ${reply.server.error}`, 'error');
        }
    }

    /** Sends one server to the server. Returns the reply, or null when it was refused. */
    async save(server) {
        let clean = { ...server };
        for (let key of ['state', 'error', 'serverName', 'tools']) {
            delete clean[key];
        }
        return await llmaUserAction(() => llmaRequest('LLMAssistantSaveMcpServer', { server: JSON.stringify(clean) }), translate('Could not save the server'));
    }

    /** Closes the editor. */
    closeEditor() {
        this.editing = null;
        let editor = document.getElementById('llma-mcp-editor');
        editor.style.display = 'none';
        editor.innerHTML = '';
        document.getElementById('llma-mcp-add').style.display = this.canManage ? '' : 'none';
    }

    /** Connects to the server as described in the editor, without saving it, and shows the tools it offers or why it could not. */
    async test() {
        let output = document.getElementById('llma-mcp-test-out');
        output.textContent = translate('Connecting…');
        let reply;
        try {
            reply = await llmaRequest('LLMAssistantTestMcpServer', { server: JSON.stringify(this.collect()) });
        }
        catch (e) {
            output.textContent = `${translate('Test failed')}: ${llmaShortError(e)}`;
            return;
        }
        if (!reply.success) {
            output.textContent = reply.error || translate('Test failed');
            return;
        }
        let names = (reply.tools || []).map(tool => tool.name);
        output.textContent = `${translate('Connected')}${reply.serverName ? ` (${reply.serverName})` : ''}: ${names.length} ${translate('tools')}${names.length ? `\n${names.join(', ')}` : ''}`;
    }
}

let llmaMcp = new LLMAMcp();

/** Wires the MCP tab's own buttons. Called once when the settings dialog is set up. */
function llmaSetupMcpTab() {
    let add = document.getElementById('llma-mcp-add');
    if (add) {
        add.addEventListener('click', () => llmaMcp.openEditor(null));
    }
}

/* ================================================================
   LLM Assistant - tools.js
   Tool management UI: list, edit, save, delete, test tools.
   ================================================================ */

'use strict';

// -- State --
let llmaEditingToolId = null;
LLMAState.tools = LLMAState.tools || [];

// -- Load Tools --
async function llmaLoadTools() {
    try {
        const result = await llmaRequest('LLMAssistantGetTools', {});
        LLMAState.tools = Array.isArray(result?.tools) ? result.tools : [];
        if (typeof result?.canWriteShared === 'boolean') {
            LLMAState.canWriteShared = !!result.canWriteShared;
        }
    } catch {
        LLMAState.tools = [];
    }
    llmaRenderToolList();
}

// -- Render Tool List (Settings Tab) --
function llmaRenderToolList() {
    const container = document.getElementById('llma-tool-list');
    if (!container) return;

    if (!LLMAState.tools.length) {
        container.innerHTML = '<div class="llma-empty-state">No tools available.</div>';
        return;
    }

    let html = '';
    for (const tool of LLMAState.tools) {
        const disabled = tool.enabled === false;
        const builtInBadge = tool.isBuiltIn ? ' <span class="llma-builtin-badge">(built-in)</span>' : '';
        const scopeBadge = tool._scope === 'shared'
            ? ' <span class="llma-scope-badge llma-scope-shared" title="Shared — visible to all users on this instance">shared</span>'
            : (tool._scope === 'personal' ? ' <span class="llma-scope-badge llma-scope-personal" title="Personal — only visible to you">personal</span>' : '');
        html += `
            <div class="llma-tool-list-item${disabled ? ' disabled' : ''}" data-tool-id="${llmaEscapeHtml(tool.id)}">
                <div class="llma-tool-list-info">
                    <div class="llma-tool-list-name">${llmaEscapeHtml(tool.name || tool.id)}${builtInBadge}${scopeBadge}</div>
                    <div class="llma-tool-list-desc">${llmaEscapeHtml(tool.description || '')}</div>
                    <div class="llma-tool-list-meta">
                        <span class="llma-tool-badge">${llmaEscapeHtml(tool.handlerType || 'builtin')}</span>
                        <span class="llma-tool-badge">${llmaEscapeHtml(tool.id)}</span>
                    </div>
                </div>
                <div class="llma-tool-list-actions">
                    <label class="llma-tool-toggle" title="${disabled ? 'Disabled' : 'Enabled'}">
                        <input type="checkbox" class="llma-tool-enable" data-tool-id="${llmaEscapeHtml(tool.id)}" ${disabled ? '' : 'checked'}>
                        <span>${disabled ? 'Off' : 'On'}</span>
                    </label>
                    <button class="basic-button llma-tool-edit" data-tool-id="${llmaEscapeHtml(tool.id)}">Edit</button>
                    ${tool.isBuiltIn ? '' : `<button class="basic-button llma-tool-delete" data-tool-id="${llmaEscapeHtml(tool.id)}">Delete</button>`}
                </div>
            </div>`;
    }
    container.innerHTML = html;

    container.querySelectorAll('.llma-tool-enable').forEach(el => {
        el.addEventListener('change', async () => {
            const id = el.dataset.toolId;
            const tool = LLMAState.tools.find(t => t.id === id);
            if (!tool) return;
            tool.enabled = el.checked;
            const scope = tool._scope === 'shared' ? 'shared' : 'personal';
            await llmaRequest('LLMAssistantSaveTool', { tool: JSON.stringify(tool), scope }).catch(() => {});
            llmaRenderToolList();
        });
    });
    container.querySelectorAll('.llma-tool-edit').forEach(btn => {
        btn.addEventListener('click', () => {
            const id = btn.dataset.toolId;
            const tool = LLMAState.tools.find(t => t.id === id);
            if (tool) llmaShowToolEditor(tool);
        });
    });
    container.querySelectorAll('.llma-tool-delete').forEach(btn => {
        btn.addEventListener('click', () => llmaDeleteTool(btn.dataset.toolId));
    });
}

// -- Tool Editor --
function llmaOpenCreateToolEditor() {
    llmaShowToolEditor(null);
}

function llmaShowToolEditor(tool) {
    const editor = document.getElementById('llma-tool-editor');
    if (!editor) return;
    editor.style.display = '';
    llmaEditingToolId = tool ? tool.id : null;

    const title = document.getElementById('llma-tool-editor-title');
    if (title) title.textContent = tool ? `Edit: ${tool.name || tool.id}` : 'New Tool';

    const deleteBtn = document.getElementById('llma-tool-delete');
    if (deleteBtn) deleteBtn.style.display = (tool && !tool.isBuiltIn) ? '' : 'none';

    llmaSetEl('llma-tool-id', tool?.id || '');
    llmaSetEl('llma-tool-name', tool?.name || '');
    llmaSetEl('llma-tool-description', tool?.description || '');

    const idInput = document.getElementById('llma-tool-id');
    if (idInput) idInput.disabled = !!tool;

    const nameInput = document.getElementById('llma-tool-name');
    if (nameInput) nameInput.disabled = !!tool?.isBuiltIn;

    const handlerSel = document.getElementById('llma-tool-handler-type');
    if (handlerSel) {
        handlerSel.value = tool?.handlerType || 'builtin';
        handlerSel.disabled = !!tool?.isBuiltIn;
    }

    const handlerIdInput = document.getElementById('llma-tool-handler-id');
    if (handlerIdInput) {
        handlerIdInput.value = tool?.handlerId || '';
        handlerIdInput.disabled = !!tool?.isBuiltIn;
    }

    const schemaArea = document.getElementById('llma-tool-schema');
    if (schemaArea) {
        const params = tool?.parameters || { type: 'object', properties: {}, required: [] };
        schemaArea.value = JSON.stringify(params, null, 2);
        schemaArea.disabled = !!tool?.isBuiltIn;
    }

    llmaSetElChecked('llma-tool-enabled', tool?.enabled !== false);

    const scopeWrap = document.getElementById('llma-tool-scope-toggle');
    const scopeCheckbox = document.getElementById('llma-tool-scope-shared');
    if (scopeWrap) scopeWrap.style.display = LLMAState.canWriteShared ? '' : 'none';
    if (scopeCheckbox) {
        scopeCheckbox.checked = tool?._scope === 'shared';
        // Built-in tools always live in the shared layer; don't let admins accidentally demote them.
        scopeCheckbox.disabled = !!tool?.isBuiltIn;
    }

    const noteEl = document.getElementById('llma-tool-editor-note');
    if (noteEl) {
        noteEl.textContent = tool?.isBuiltIn
            ? 'This is a built-in tool. Only description and enabled state can be edited.'
            : '';
        noteEl.style.display = tool?.isBuiltIn ? '' : 'none';
    }

    // Clear test UI
    const testArgs = document.getElementById('llma-tool-test-args');
    if (testArgs) testArgs.value = '{}';
    const testResult = document.getElementById('llma-tool-test-result');
    if (testResult) testResult.textContent = '';
}

async function llmaSaveToolFromEditor() {
    const id = document.getElementById('llma-tool-id')?.value?.trim();
    const name = document.getElementById('llma-tool-name')?.value?.trim();
    const description = document.getElementById('llma-tool-description')?.value?.trim();
    const handlerType = document.getElementById('llma-tool-handler-type')?.value?.trim() || 'builtin';
    const handlerId = document.getElementById('llma-tool-handler-id')?.value?.trim() || id;
    const enabled = document.getElementById('llma-tool-enabled')?.checked !== false;
    const schemaText = document.getElementById('llma-tool-schema')?.value?.trim() || '{}';

    if (!id) { llmaShowToast('Tool id is required', 'error'); return; }
    if (!name) { llmaShowToast('Tool name is required', 'error'); return; }

    let parameters;
    try {
        parameters = JSON.parse(schemaText);
    } catch (ex) {
        llmaShowToast('Invalid JSON in parameters schema', 'error');
        return;
    }

    const existing = LLMAState.tools.find(t => t.id === id);
    const tool = {
        ...(existing || {}),
        id,
        name,
        description,
        parameters,
        handlerType,
        handlerId,
        enabled,
    };

    const scopeCheckbox = document.getElementById('llma-tool-scope-shared');
    const wantsShared = !!(scopeCheckbox?.checked && LLMAState.canWriteShared);
    // Built-in tools stay in the shared layer regardless of UI state.
    const scope = (wantsShared || tool.isBuiltIn) ? 'shared' : 'personal';

    try {
        const result = await llmaRequest('LLMAssistantSaveTool', { tool: JSON.stringify(tool), scope });
        if (result && result.success === false) {
            llmaShowToast(result.error || 'Failed to save tool', 'error');
            return;
        }
        document.getElementById('llma-tool-editor').style.display = 'none';
        await llmaLoadTools();
        llmaShowToast('Tool saved', 'success');
    } catch {
        llmaShowToast('Failed to save tool', 'error');
    }
}

async function llmaDeleteTool(id) {
    if (!id) return;
    const tool = LLMAState.tools.find(t => t.id === id);
    if (tool?.isBuiltIn) { llmaShowToast('Cannot delete built-in tool', 'info'); return; }
    if (!confirm(`Delete tool "${tool?.name || id}"?`)) return;
    try {
        const payload = { toolId: id };
        if (tool?._scope) payload.scope = tool._scope;
        const result = await llmaRequest('LLMAssistantDeleteTool', payload);
        if (result && result.success === false) {
            llmaShowToast(result.error || 'Failed to delete tool', 'error');
            return;
        }
        document.getElementById('llma-tool-editor').style.display = 'none';
        await llmaLoadTools();
        llmaShowToast('Tool deleted', 'info');
    } catch {
        llmaShowToast('Failed to delete tool', 'error');
    }
}

async function llmaTestTool() {
    const id = document.getElementById('llma-tool-id')?.value?.trim();
    if (!id) { llmaShowToast('No tool to test', 'info'); return; }
    const argsText = document.getElementById('llma-tool-test-args')?.value?.trim() || '{}';
    let args;
    try {
        args = JSON.parse(argsText);
    } catch {
        llmaShowToast('Invalid JSON arguments', 'error');
        return;
    }
    const resultEl = document.getElementById('llma-tool-test-result');
    if (resultEl) resultEl.textContent = 'Running...';
    try {
        const result = await llmaRequest('LLMAssistantExecuteTool', { toolId: id, arguments: JSON.stringify(args) });
        if (resultEl) resultEl.textContent = JSON.stringify(result?.result || result, null, 2);
    } catch (ex) {
        if (resultEl) resultEl.textContent = 'Error: ' + (ex?.message || String(ex));
    }
}

// -- Setup Tools Tab --
function llmaSetupToolsTab() {
    document.getElementById('llma-create-tool-btn')?.addEventListener('click', llmaOpenCreateToolEditor);
    document.getElementById('llma-tool-save')?.addEventListener('click', llmaSaveToolFromEditor);
    document.getElementById('llma-tool-delete')?.addEventListener('click', () => llmaDeleteTool(llmaEditingToolId));
    document.getElementById('llma-tool-cancel')?.addEventListener('click', () => {
        document.getElementById('llma-tool-editor').style.display = 'none';
    });
    document.getElementById('llma-tool-editor-close')?.addEventListener('click', () => {
        document.getElementById('llma-tool-editor').style.display = 'none';
    });
    document.getElementById('llma-tool-test-run')?.addEventListener('click', llmaTestTool);
}

// -- Render Enabled Tools Checklist (Assistant Editor) --
function llmaRenderAssistantToolsChecklist(enabledToolIds) {
    const container = document.getElementById('llma-assist-tools');
    if (!container) return;

    const ids = Array.isArray(enabledToolIds) ? enabledToolIds : [];
    if (!LLMAState.tools.length) {
        container.innerHTML = '<div class="llma-tool-checklist-empty">No tools available. Create tools in the Tools tab first.</div>';
        return;
    }

    let html = '<div class="llma-tool-checklist">';
    for (const tool of LLMAState.tools) {
        const checked = ids.includes(tool.id);
        const disabled = tool.enabled === false;
        html += `
            <label class="llma-tool-check${disabled ? ' global-disabled' : ''}" title="${disabled ? 'Tool is globally disabled' : llmaEscapeHtml(tool.description || '')}">
                <input type="checkbox" class="llma-assist-tool-check" data-tool-id="${llmaEscapeHtml(tool.id)}" ${checked ? 'checked' : ''} ${disabled ? 'disabled' : ''}>
                <span class="llma-tool-check-name">${llmaEscapeHtml(tool.name || tool.id)}</span>
                ${tool.isBuiltIn ? '<span class="llma-builtin-badge">built-in</span>' : ''}
            </label>`;
    }
    html += '</div>';
    container.innerHTML = html;
}

function llmaReadAssistantEnabledToolIds() {
    const boxes = document.querySelectorAll('.llma-assist-tool-check:checked');
    return Array.from(boxes).map(b => b.dataset.toolId);
}

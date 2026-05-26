/* ================================================================
   LLM Assistant - tools.js
   Tool management UI: list, edit, save, delete, test tools.
   ================================================================ */

'use strict';

// -- State --
let llmaEditingToolId = null;
LLMAState.tools = LLMAState.tools || [];

// IDs of built-in tools whose editor shows a per-user "Tool Config" block.
// Add new IDs here when wiring config support for additional tools.
const LLMA_TOOLS_WITH_CONFIG = new Set(['generate_image', 'file_write']);

// -- Load Tools --
async function llmaLoadTools() {
    try {
        const result = await llmaRequest('LLMAssistantGetTools', {});
        LLMAState.tools = Array.isArray(result?.tools) ? result.tools : [];
        if (typeof result?.canWriteShared === 'boolean') {
            LLMAState.canWriteShared = !!result.canWriteShared;
        }
        LLMAState.loadErrors.tools = null;
    } catch (e) {
        LLMAState.tools = [];
        LLMAState.loadErrors.tools = llmaShortError(e) || 'Failed to load tools';
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

    // Per-user tool config (eg generate_image's default preset, file_write's extra extensions).
    // Only meaningful for built-in tools that opt in via LLMA_TOOLS_WITH_CONFIG.
    llmaSetupToolConfigPanel(tool);
}

// -- Per-tool config panel --
// Shows the right config block for the tool being edited and loads its current values.
function llmaSetupToolConfigPanel(tool) {
    const section = document.getElementById('llma-tool-config-section');
    if (!section) return;
    // Hide every config block first.
    section.querySelectorAll('.llma-tool-config-block').forEach(el => el.style.display = 'none');
    if (!tool || !LLMA_TOOLS_WITH_CONFIG.has(tool.id)) {
        section.style.display = 'none';
        return;
    }
    const block = document.getElementById(`llma-tool-config-${tool.id}`);
    if (!block) {
        section.style.display = 'none';
        return;
    }
    section.style.display = '';
    block.style.display = '';
    if (tool.id === 'generate_image') {
        llmaPopulateImagePresetConfig();
    } else if (tool.id === 'file_write') {
        llmaPopulateFileWriteConfig();
    }
}

async function llmaPopulateImagePresetConfig() {
    const sel = document.getElementById('llma-config-default-preset');
    if (!sel) return;
    sel.innerHTML = '<option value="">Loading…</option>';
    try {
        const [presetsRes, configRes] = await Promise.all([
            llmaRequest('LLMAssistantGetImagePresets', {}),
            llmaRequest('LLMAssistantGetToolConfig', { toolId: 'generate_image' }),
        ]);
        const presets = Array.isArray(presetsRes?.presets) ? presetsRes.presets : [];
        const current = configRes?.config?.defaultPreset || '';
        sel.innerHTML = '';
        const noneOpt = document.createElement('option');
        noneOpt.value = '';
        noneOpt.textContent = presets.length ? '(none — let the LLM pick or error if it doesn\'t)' : '(no presets saved — create one on the Generate tab first)';
        sel.appendChild(noneOpt);
        for (const p of presets) {
            const opt = document.createElement('option');
            opt.value = p.title;
            opt.textContent = p.description ? `${p.title} — ${p.description}` : p.title;
            if (p.title === current) opt.selected = true;
            sel.appendChild(opt);
        }
    } catch {
        sel.innerHTML = '<option value="">(failed to load presets)</option>';
    }
}

async function llmaPopulateFileWriteConfig() {
    const input = document.getElementById('llma-config-extra-extensions');
    if (!input) return;
    input.value = '';
    try {
        const res = await llmaRequest('LLMAssistantGetToolConfig', { toolId: 'file_write' });
        const extras = res?.config?.extraExtensions;
        if (Array.isArray(extras)) {
            input.value = extras.join(', ');
        }
    } catch { /* leave empty */ }
}

// Reads the current values out of the visible config block and sends them to the server.
// Returns true on success, false on any error (caller decides whether to surface a toast).
async function llmaSaveCurrentToolConfig(toolId) {
    if (!toolId || !LLMA_TOOLS_WITH_CONFIG.has(toolId)) return true;
    let config = {};
    if (toolId === 'generate_image') {
        const sel = document.getElementById('llma-config-default-preset');
        const val = sel?.value?.trim() || '';
        if (val) config.defaultPreset = val;
    } else if (toolId === 'file_write') {
        const input = document.getElementById('llma-config-extra-extensions');
        const raw = input?.value || '';
        const exts = raw.split(',').map(s => s.trim().replace(/^\./, '')).filter(Boolean);
        if (exts.length) config.extraExtensions = exts;
    }
    try {
        const res = await llmaRequest('LLMAssistantSetToolConfig', {
            toolId,
            config: JSON.stringify(config),
        });
        return res?.success !== false;
    } catch {
        return false;
    }
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
        // Persist the per-user tool config block alongside the tool def.
        // Failure here doesn't roll back the tool save — surface a softer warning instead.
        const cfgOk = await llmaSaveCurrentToolConfig(id);
        document.getElementById('llma-tool-editor').style.display = 'none';
        await llmaLoadTools();
        llmaShowToast(cfgOk ? 'Tool saved' : 'Tool saved, but config failed to save', cfgOk ? 'success' : 'warning');
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
    // When the user toggles a tool on/off, the per-assistant config section needs to add/remove
    // that tool's config block. Re-render whatever the config section was last given.
    container.querySelectorAll('.llma-assist-tool-check').forEach(box => {
        box.addEventListener('change', () => {
            // We pass null as assistant so the renderer rebuilds from current checklist state +
            // whatever values are currently in the form (preserved by reading from DOM first).
            llmaRenderAssistantToolConfig(null);
        });
    });
}

function llmaReadAssistantEnabledToolIds() {
    const boxes = document.querySelectorAll('.llma-assist-tool-check:checked');
    return Array.from(boxes).map(b => b.dataset.toolId);
}

// -- Per-assistant tool config (assistant editor) --
//
// Renders one config sub-form per enabled tool that opts into config (today: generate_image,
// file_write — the same set the global Tools tab supports, see LLMA_TOOLS_WITH_CONFIG above).
//
// Saved into `assistant.toolConfig[toolId]` on the assistant blob. Precedence at runtime
// (handled server-side by ToolConfigService): assistant config > user config > tool default.
//
// Calling with `assistant=null` re-renders from the live checklist + currently-typed values
// (so toggling a tool on/off doesn't blow away unsaved config from the other tools).
function llmaRenderAssistantToolConfig(assistant) {
    const container = document.getElementById('llma-asst-tool-config');
    if (!container) return;
    // Snapshot current form values BEFORE wiping, so toggling a tool checkbox doesn't lose
    // config the user has been typing into another tool's block.
    const liveValues = llmaSnapshotAssistantToolConfigForms();
    const baseValues = (assistant && assistant.toolConfig) || liveValues || {};
    const enabledIds = new Set(llmaReadAssistantEnabledToolIds());
    container.innerHTML = '';
    let renderedCount = 0;
    for (const toolId of LLMA_TOOLS_WITH_CONFIG) {
        if (!enabledIds.has(toolId)) continue;
        const tool = LLMAState.tools.find(t => t.id === toolId);
        if (!tool) continue;
        const block = document.createElement('div');
        block.className = 'llma-asst-tool-config-block';
        block.dataset.toolId = toolId;
        const heading = document.createElement('div');
        heading.className = 'llma-asst-tool-config-heading';
        heading.textContent = tool.name || toolId;
        block.appendChild(heading);
        const cfg = baseValues[toolId] || {};
        const inner = llmaBuildAssistantToolConfigInner(toolId, cfg);
        if (inner) block.appendChild(inner);
        container.appendChild(block);
        renderedCount++;
    }
    if (renderedCount === 0) {
        container.innerHTML = '<div class="llma-asst-tool-config-empty">No configurable tools enabled. Enable Generate Image or Write File above to set this assistant\'s defaults.</div>';
    }
}

function llmaBuildAssistantToolConfigInner(toolId, config) {
    if (toolId === 'generate_image') {
        const wrap = document.createElement('div');
        wrap.innerHTML = `
            <label>Default image preset</label>
            <select class="llma-asst-cfg-input" data-key="defaultPreset"></select>
            <div class="llma-asst-tool-config-help">When this assistant generates an image without picking a preset, it uses this one. Leave blank to fall back to your account default.</div>
        `;
        const sel = wrap.querySelector('select');
        llmaPopulateAssistantPresetSelect(sel, config.defaultPreset || '');
        return wrap;
    }
    if (toolId === 'file_write') {
        const wrap = document.createElement('div');
        wrap.innerHTML = `
            <label>Extra allowed extensions</label>
            <input type="text" class="llma-asst-cfg-input" data-key="extraExtensions" placeholder="py, html, css">
            <div class="llma-asst-tool-config-help">Comma-separated. Always allowed: <code>md, json, txt, yaml, yml, csv, log</code>. Anything you add here is in addition (only for this assistant).</div>
        `;
        const input = wrap.querySelector('input');
        if (Array.isArray(config.extraExtensions)) {
            input.value = config.extraExtensions.join(', ');
        }
        return wrap;
    }
    return null;
}

async function llmaPopulateAssistantPresetSelect(sel, currentValue) {
    sel.innerHTML = '<option value="">(use account default)</option>';
    try {
        const res = await llmaRequest('LLMAssistantGetImagePresets', {});
        const presets = Array.isArray(res?.presets) ? res.presets : [];
        for (const p of presets) {
            const opt = document.createElement('option');
            opt.value = p.title;
            opt.textContent = p.description ? `${p.title} — ${p.description}` : p.title;
            if (p.title === currentValue) opt.selected = true;
            sel.appendChild(opt);
        }
    } catch { /* leave with the empty option */ }
}

// Snapshots whatever's currently typed into the per-assistant config forms, so re-renders
// (eg from toggling a tool checkbox) don't wipe in-progress edits.
function llmaSnapshotAssistantToolConfigForms() {
    const out = {};
    document.querySelectorAll('#llma-asst-tool-config .llma-asst-tool-config-block').forEach(block => {
        const toolId = block.dataset.toolId;
        if (!toolId) return;
        const cfg = {};
        block.querySelectorAll('.llma-asst-cfg-input').forEach(input => {
            const key = input.dataset.key;
            if (!key) return;
            if (toolId === 'file_write' && key === 'extraExtensions') {
                const exts = (input.value || '').split(',').map(s => s.trim().replace(/^\./, '')).filter(Boolean);
                if (exts.length) cfg[key] = exts;
            } else {
                const val = (input.value || '').trim();
                if (val) cfg[key] = val;
            }
        });
        if (Object.keys(cfg).length > 0) out[toolId] = cfg;
    });
    return out;
}

// Returns the per-assistant tool config object to ship in the assistant save payload.
// Empty/all-defaults yields undefined so we don't pollute the assistant blob.
function llmaSerializeAssistantToolConfig() {
    const snap = llmaSnapshotAssistantToolConfigForms();
    return Object.keys(snap).length > 0 ? snap : undefined;
}

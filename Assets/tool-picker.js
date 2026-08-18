/* ================================================================
   LLM Assistant - tool-picker.js
   In-chat "/" tool picker (Claude-style).

   Type "/" (at the start or after a space) to autocomplete the active
   assistant's enabled tools. Two ways to invoke:
     - Ctrl+Enter / mouse-click a tool  -> Option A: run it directly via a
       schema-driven form (deterministic — no model involved).
     - Plain Enter on "/toolId your request" (type-through) -> Option B: the
       message is sent and the backend nudges the model to call that tool.

   Loaded after chat.js; wired from llmaSetupInput().
   ================================================================ */

'use strict';

const LLMAToolPicker = {
    open: false,
    items: [],
    active: 0,
    tokenStart: -1,
    el: null,
};

// Tools available to the active assistant, intersected with globally-enabled tools. Uses the
// server-resolved `_effectiveToolIds` (inheritance applied) so the picker never disagrees with what
// the backend actually offers the model; raw `enabledToolIds` is only a fallback for stale payloads.
// Empty whenever tool calling itself is off (see llmaEffectiveToolsEnabled) — the picker has nothing to
// offer in that state, same as the backend injecting no tool prompt at all.
function llmaPickerTools() {
    if (!llmaEffectiveToolsEnabled()) return [];
    const asst = (LLMAState.assistants || []).find(a => a.id === LLMAState.activeAssistantId);
    const enabledIds = asst?._effectiveToolIds || asst?.enabledToolIds || [];
    return (LLMAState.tools || []).filter(t => enabledIds.includes(t.id) && t.enabled !== false);
}

// -- Per-chat "Tools" master toggle (composer button, next to the "/" picker) --------------------

// Effective state for the ACTIVE thread: an explicit per-thread override wins; otherwise falls back
// to the active assistant's resolved master switch (missing/undefined = on, matching the server default).
function llmaEffectiveToolsEnabled() {
    if (typeof LLMAState.activeThreadToolsEnabled === 'boolean') return LLMAState.activeThreadToolsEnabled;
    const asst = (LLMAState.assistants || []).find(a => a.id === LLMAState.activeAssistantId);
    if (typeof asst?._effectiveToolsEnabled === 'boolean') return asst._effectiveToolsEnabled;
    return asst?.toolsEnabled !== false;
}

// Reflects the current effective state onto the composer button (icon slash + title + aria-pressed).
function llmaUpdateToolsToggleBtn() {
    const btn = document.getElementById('llma-tools-toggle-btn');
    if (!btn) return;
    const enabled = llmaEffectiveToolsEnabled();
    btn.classList.toggle('off', !enabled);
    btn.setAttribute('aria-pressed', String(enabled));
    btn.title = enabled
        ? 'Tool calling is ON for this chat — click to turn off'
        : 'Tool calling is OFF for this chat — no tool prompt is sent. Click to turn on.';
}

// Flips the per-thread override and persists it server-side. A brand-new (not-yet-created) thread has
// no id yet — in that case just flip the assistant-inherited default locally; llmaCreateThread will
// carry it over as the thread's initial override once the first message actually creates the thread.
async function llmaToggleThreadTools() {
    const next = !llmaEffectiveToolsEnabled();
    if (!LLMAState.activeThreadId) {
        LLMAState.activeThreadToolsEnabled = next;
        llmaUpdateToolsToggleBtn();
        llmaPickerClose();
        return;
    }
    try {
        const result = await llmaRequest('LLMAssistantSetThreadToolsEnabled', { threadId: LLMAState.activeThreadId, enabled: next });
        if (!result?.success) {
            llmaShowToast(result?.error || 'Failed to update the tools setting.', 'error');
            return;
        }
        LLMAState.activeThreadToolsEnabled = next;
        llmaUpdateToolsToggleBtn();
        llmaPickerClose();
        llmaShowToast(next ? 'Tool calling enabled for this chat.' : 'Tool calling disabled for this chat — no tool prompt will be sent.', 'info');
    } catch (e) {
        llmaShowToast(llmaShortError(e) || 'Failed to update the tools setting.', 'error');
    }
}

// A "/word" token immediately before the cursor (slash at start or after whitespace — so URLs/paths
// like http://x don't trigger it). Returns { query, start, end } or null.
function llmaPickerToken(input) {
    const pos = input.selectionStart ?? input.value.length;
    const before = input.value.slice(0, pos);
    const m = before.match(/(?:^|\s)\/([\w-]*)$/);
    if (!m) return null;
    return { query: m[1].toLowerCase(), start: pos - m[1].length - 1, end: pos };
}

// Leading "/toolId rest..." used by Option B on send. Returns { toolId, message } or null
// (only when toolId is a real enabled tool).
function llmaPickerExtractForceTool(message) {
    const m = (message || '').match(/^\/([\w-]+)\b\s*([\s\S]*)$/);
    if (!m) return null;
    if (!llmaPickerTools().some(t => t.id === m[1])) return null;
    return { toolId: m[1], message: m[2].trim() };
}

function llmaPickerEnsureEl() {
    if (LLMAToolPicker.el && LLMAToolPicker.el.isConnected) return LLMAToolPicker.el;
    const wrap = document.querySelector('.llma-input-wrap');
    if (!wrap) return null;
    const el = document.createElement('div');
    el.className = 'llma-tool-popup';
    el.id = 'llma-tool-popup';
    el.style.display = 'none';
    el.setAttribute('role', 'listbox');
    el.setAttribute('aria-label', 'Tools');
    wrap.appendChild(el);
    LLMAToolPicker.el = el;
    return el;
}

function llmaPickerClose() {
    LLMAToolPicker.open = false;
    if (LLMAToolPicker.el) LLMAToolPicker.el.style.display = 'none';
    document.getElementById('llma-tools-btn')?.classList.remove('active');
    if (LLMAToolPicker._awayHandler) {
        document.removeEventListener('mousedown', LLMAToolPicker._awayHandler, true);
        LLMAToolPicker._awayHandler = null;
    }
    const input = document.getElementById('llma-input');
    if (input) { input.removeAttribute('aria-activedescendant'); input.setAttribute('aria-expanded', 'false'); }
}

// Open the picker as a full menu of the assistant's tools, triggered by the "/" button instead of
// by typing. tokenStart = -1 so selecting a tool won't try to strip a typed "/token" from the input.
// Clicking the button again (or anywhere outside) closes it.
function llmaPickerOpenAll() {
    const input = document.getElementById('llma-input');
    if (LLMAToolPicker.open && LLMAToolPicker.tokenStart === -1) { llmaPickerClose(); return; }
    const tools = llmaPickerTools();
    if (tools.length === 0) {
        if (typeof llmaShowToast === 'function') {
            llmaShowToast(llmaEffectiveToolsEnabled()
                ? 'No tools are enabled for this assistant.'
                : 'Tool calling is off for this chat — click the tools toggle to turn it back on.', 'info');
        }
        input?.focus();
        return;
    }
    LLMAToolPicker.items = tools;
    LLMAToolPicker.tokenStart = -1;
    LLMAToolPicker.active = 0;
    LLMAToolPicker.open = true;
    llmaPickerRender();
    document.getElementById('llma-tools-btn')?.classList.add('active');
    input?.focus();
    // One-shot outside-click closer (capture phase so it runs before the popup item's mousedown,
    // which lives inside the popup and is therefore exempt). Deferred so this opening click doesn't trip it.
    setTimeout(() => {
        const onAway = (e) => {
            const pop = LLMAToolPicker.el;
            const btn = document.getElementById('llma-tools-btn');
            if (pop?.contains(e.target) || btn?.contains(e.target)) return;
            llmaPickerClose();
        };
        document.addEventListener('mousedown', onAway, true);
        LLMAToolPicker._awayHandler = onAway;
    }, 0);
}

// Recompute on each input change.
function llmaPickerOnInput(input) {
    const tok = llmaPickerToken(input);
    if (!tok) { llmaPickerClose(); return; }
    const tools = llmaPickerTools();
    const items = tok.query
        ? tools.filter(t => `${t.id} ${t.name || ''}`.toLowerCase().includes(tok.query))
        : tools;
    if (items.length === 0) { llmaPickerClose(); return; }
    LLMAToolPicker.items = items;
    LLMAToolPicker.tokenStart = tok.start;
    LLMAToolPicker.active = 0;
    LLMAToolPicker.open = true;
    llmaPickerRender();
}

function llmaPickerRender() {
    const el = llmaPickerEnsureEl();
    if (!el) return;
    el.innerHTML =
        LLMAToolPicker.items.map((t, i) => `
            <div class="llma-tool-popup-item${i === LLMAToolPicker.active ? ' active' : ''}" id="llma-tool-opt-${i}" data-idx="${i}" role="option" aria-selected="${i === LLMAToolPicker.active}">
                <div class="llma-tool-popup-name">/${llmaEscapeHtml(t.id)}</div>
                <div class="llma-tool-popup-desc">${llmaEscapeHtml(t.description || t.name || '')}</div>
            </div>`).join('') +
        `<div class="llma-tool-popup-foot">↑↓ / Tab navigate · Ctrl+Enter or click to run · Enter to send</div>`;
    el.style.display = '';
    const input = document.getElementById('llma-input');
    if (input) {
        input.setAttribute('aria-expanded', 'true');
        input.setAttribute('aria-controls', 'llma-tool-popup');
        input.setAttribute('aria-activedescendant', `llma-tool-opt-${LLMAToolPicker.active}`);
    }
    el.querySelectorAll('.llma-tool-popup-item').forEach(node => {
        // mousedown (not click) so it fires before the input loses focus.
        node.addEventListener('mousedown', (e) => {
            e.preventDefault();
            llmaPickerSelect(LLMAToolPicker.items[parseInt(node.dataset.idx, 10)]);
        });
    });
    const active = el.querySelector('.llma-tool-popup-item.active');
    if (active) active.scrollIntoView({ block: 'nearest' });
}

function llmaPickerMove(delta) {
    const n = LLMAToolPicker.items.length;
    if (n === 0) return;
    LLMAToolPicker.active = (LLMAToolPicker.active + delta + n) % n;
    llmaPickerRender();
}

// Returns true if the keydown was consumed by the popup (caller should not also act on it).
function llmaPickerOnKeydown(e) {
    if (!LLMAToolPicker.open) return false;
    if (e.key === 'ArrowDown' || (e.key === 'Tab' && !e.shiftKey)) { e.preventDefault(); llmaPickerMove(1); return true; }
    if (e.key === 'ArrowUp'   || (e.key === 'Tab' && e.shiftKey))  { e.preventDefault(); llmaPickerMove(-1); return true; }
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); llmaPickerSelect(LLMAToolPicker.items[LLMAToolPicker.active]); return true; }
    if (e.key === 'Escape') { e.preventDefault(); llmaPickerClose(); return true; }
    // Plain Enter and ordinary typing fall through — Enter sends (Option B).
    return false;
}

// Option A: strip the "/token" from the input and open the args form.
function llmaPickerSelect(tool) {
    if (!tool) return;
    const input = document.getElementById('llma-input');
    if (input && LLMAToolPicker.tokenStart >= 0) {
        const pos = input.selectionStart ?? input.value.length;
        input.value = input.value.slice(0, LLMAToolPicker.tokenStart) + input.value.slice(pos);
        input.dispatchEvent(new Event('input'));
    }
    llmaPickerClose();
    llmaToolFormOpen(tool);
}

// ── Option A: schema-driven args form, inline above the input ──────────────────────────────────
function llmaToolFormOpen(tool) {
    llmaToolFormClose();
    const area = document.querySelector('.llma-input-area');
    if (!area) return;
    const schema = tool.parameters || {};
    const props = schema.properties || {};
    const required = schema.required || [];
    let fields = '';
    for (const [key, def] of Object.entries(props)) {
        fields += llmaToolFormField(key, def || {}, required.includes(key));
    }
    if (!fields) fields = '<div class="llma-tool-form-empty">This tool takes no parameters.</div>';

    const form = document.createElement('div');
    form.className = 'llma-tool-form';
    form.id = 'llma-tool-form';
    form.innerHTML = `
        <div class="llma-tool-form-head">
            <span class="llma-tool-form-title">Run <code>/${llmaEscapeHtml(tool.id)}</code></span>
            <button type="button" class="llma-icon-btn llma-tool-form-close" aria-label="Cancel">&times;</button>
        </div>
        <div class="llma-tool-form-body">${fields}</div>
        <div class="llma-tool-form-actions">
            <button type="button" class="basic-button llma-tool-form-run">Run tool</button>
            <button type="button" class="llma-tool-form-cancel">Cancel</button>
        </div>`;
    area.insertBefore(form, area.firstChild);
    form.querySelector('.llma-tool-form-close')?.addEventListener('click', llmaToolFormClose);
    form.querySelector('.llma-tool-form-cancel')?.addEventListener('click', llmaToolFormClose);
    form.querySelector('.llma-tool-form-run')?.addEventListener('click', () => llmaToolFormRun(tool, form));
    form.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') { e.preventDefault(); llmaToolFormClose(); }
        else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); llmaToolFormRun(tool, form); }
    });
    const first = form.querySelector('input, textarea, select');
    if (first) setTimeout(() => first.focus(), 30);
}

function llmaToolFormField(key, def, req) {
    const type = def.type || 'string';
    const id = `llma-tf-${key}`.replace(/[^\w-]/g, '_');
    const safeKey = llmaEscapeHtml(key);
    const ph = llmaEscapeHtml(def.description || '');
    const label = `<label for="${id}">${safeKey}${req ? ' <span class="llma-tool-req">*</span>' : ''}</label>`;
    let control;
    if (Array.isArray(def.enum)) {
        control = `<select id="${id}" data-key="${safeKey}" data-type="string">${def.enum.map(v => `<option value="${llmaEscapeHtml(String(v))}">${llmaEscapeHtml(String(v))}</option>`).join('')}</select>`;
    } else if (type === 'boolean') {
        control = `<input type="checkbox" id="${id}" data-key="${safeKey}" data-type="boolean">`;
    } else if (type === 'number' || type === 'integer') {
        control = `<input type="number" id="${id}" data-key="${safeKey}" data-type="number" placeholder="${ph}">`;
    } else if (key === 'content' || key === 'text' || key === 'body' || (def.description || '').length > 70) {
        control = `<textarea id="${id}" data-key="${safeKey}" data-type="string" rows="4" placeholder="${ph}"></textarea>`;
    } else {
        control = `<input type="text" id="${id}" data-key="${safeKey}" data-type="string" placeholder="${ph}">`;
    }
    return `<div class="llma-tool-form-field">${label}${control}</div>`;
}

function llmaToolFormClose() {
    document.getElementById('llma-tool-form')?.remove();
}

async function llmaToolFormRun(tool, form) {
    const args = {};
    let missing = null;
    const required = tool.parameters?.required || [];
    form.querySelectorAll('[data-key]').forEach(ctrl => {
        const key = ctrl.dataset.key;
        const t = ctrl.dataset.type;
        let val;
        if (t === 'boolean') { val = ctrl.checked; }
        else if (t === 'number') { val = ctrl.value === '' ? undefined : Number(ctrl.value); }
        else { val = ctrl.value; }
        if (required.includes(key) && (val === undefined || val === '')) { missing = missing || key; }
        if (val !== undefined && val !== '' && val !== false) { args[key] = val; }
        else if (t === 'boolean' && val === true) { args[key] = true; }
    });
    if (missing) { llmaShowToast(`Please fill in the required field "${missing}".`, 'error'); return; }
    llmaToolFormClose();
    await llmaRunToolDirect(tool, args);
}

// Executes the tool directly and renders it in the chat as a tool_call + tool_result pair — visually
// identical to a model-initiated call. The backend persists it into the thread (when threadId is set).
async function llmaRunToolDirect(tool, args) {
    if (!LLMAState.activeThreadId) {
        await llmaCreateThread(LLMAState.activeAssistantId);
    }
    llmaShowChatPanel();
    const callId = llmaGenerateId();
    const msgId = llmaGenerateId();
    llmaAppendMessageToDOM('assistant', '', null, msgId);
    const bubble = document.querySelector(`[data-msg-id="${msgId}"] .llma-msg-bubble`);
    bubble?.querySelector('.llma-typing')?.remove();
    if (bubble) llmaRenderToolCall(bubble, { id: callId, name: tool.id, arguments: args });
    llmaScrollToBottom();

    let result;
    try {
        const res = await llmaRequest('LLMAssistantExecuteTool', {
            toolId: tool.id,
            arguments: args,
            callId,
            assistantId: LLMAState.activeAssistantId,
            threadId: LLMAState.activeThreadId,
        });
        result = res?.result ?? { success: res?.success !== false };
    } catch (e) {
        result = { success: false, error: llmaShortError(e) || 'Tool execution failed' };
    }
    if (bubble) llmaRenderToolResult(bubble, { id: callId, name: tool.id, result });
    llmaScrollToBottom();

    // Mirror into in-memory history + assets (the backend already persisted the thread copy).
    LLMAState.messages.push({
        id: msgId, role: 'assistant', content: '', timestamp: new Date().toISOString(),
        toolCalls: [{ id: callId, name: tool.id, arguments: args, result }],
    });
    if (typeof llmaRebuildAssetsForThread === 'function') llmaRebuildAssetsForThread();
    if (typeof llmaUpdatePanelStats === 'function') llmaUpdatePanelStats();
}

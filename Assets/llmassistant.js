/* ================================================================
   LLM Assistant - llmassistant.js
   Main entry point. Wires all modules together.
   Init sequence, top bar, settings modal, assistants, responsive.
   ================================================================ */

'use strict';

// -- Instruction Keys --
const LLMA_INSTRUCTION_KEYS = ['chat', 'vision', 'caption', 'prompt', 'randomprompt', 'instructiongen'];

// -- Editing state --
let llmaEditingAsstId = null;
let llmaActiveInstrTab = 'chat';

// -- Entry Point --
async function llmaInit() {
    if (!document.getElementById('llma-container')) return;

    const [, ] = await Promise.allSettled([
        llmaLoadCdnLibs(),
        llmaLoadSettings(),
    ]);

    await Promise.allSettled([
        llmaLoadAssistants(),
        llmaLoadThreads(),
        llmaLoadModels(),
    ]);

    // Apply loaded settings to state
    LLMAState.markdownEnabled = LLMAState.settings?.ui?.markdownEnabled !== false;
    LLMAState.enterToSend     = LLMAState.settings?.ui?.enterToSend     !== false;
    LLMAState.showTokens      = LLMAState.settings?.ui?.showTokens      !== false;
    LLMAState.currentModel    = LLMAState.settings?.currentModel        || null;

    llmaSetupTopBar();
    llmaSetupSidebar();
    llmaSetupInput();
    llmaSetupSettingsModal();
    llmaSetupAssistantPanel();
    llmaSetupKeyboardShortcuts();
    llmaSetupResponsive();

    llmaShowWelcome();
    llmaUpdateModelPill();
}

// -- Settings --
async function llmaLoadSettings() {
    try {
        const result = await llmaRequest('LLMAssistantGetSettings', {});
        LLMAState.settings = result?.settings
            ? llmaMergeSettings(LLMA_DEFAULT_SETTINGS, typeof result.settings === 'string' ? JSON.parse(result.settings) : result.settings)
            : { ...LLMA_DEFAULT_SETTINGS };
    } catch {
        LLMAState.settings = { ...LLMA_DEFAULT_SETTINGS };
    }
}

async function llmaSaveSettings() {
    try {
        await llmaRequest('LLMAssistantSaveSettings', { settings: JSON.stringify(LLMAState.settings) });
    } catch {
        llmaShowToast('Failed to save settings', 'error');
    }
}

// -- Assistants --
async function llmaLoadAssistants() {
    try {
        const result = await llmaRequest('LLMAssistantGetAssistants', {});
        LLMAState.assistants = Array.isArray(result?.assistants) ? result.assistants : [];
        LLMAState.activeAssistantId = result?.activeAssistantId || LLMAState.activeAssistantId || 'default';
    } catch {
        LLMAState.assistants = [];
    }
}

// -- Model Loading --
async function llmaLoadModels() {
    const statusEl = document.getElementById('llma-model-status');
    if (statusEl) statusEl.className = 'llma-model-status loading';

    try {
        const result = await llmaRequest('LLMAssistantGetModels', {});
        const models = Array.isArray(result?.models) ? result.models : [];

        const sel = document.getElementById('llma-model-select');
        if (sel) {
            sel.innerHTML = models.length
                ? models.map(m => `<option value="${llmaEscapeHtml(m.name)}">${llmaEscapeHtml(m.title || m.name)}</option>`).join('')
                : '<option value="" disabled>No models found</option>';
        }

        if (models.length > 0) {
            if (statusEl) statusEl.className = 'llma-model-status';
            // Restore saved model
            const saved = LLMAState.settings?.currentModel;
            if (saved && sel) {
                sel.value = saved;
                LLMAState.currentModel = saved;
            }
        } else {
            if (statusEl) statusEl.className = 'llma-model-status offline';
        }
    } catch {
        if (statusEl) statusEl.className = 'llma-model-status offline';
    }
    llmaUpdateModelPill();
}

// -- Top Bar --
function llmaSetupTopBar() {
    llmaSetupModelPill();
    llmaSetupParamsPopover();
    llmaSetupExportMenu();

    const settingsBtn = document.getElementById('llma-settings-btn');
    if (settingsBtn) settingsBtn.addEventListener('click', llmaOpenSettings);

    const asstToggle = document.getElementById('llma-asst-toggle');
    if (asstToggle) {
        asstToggle.addEventListener('click', () => {
            const panel = document.getElementById('llma-asst-panel');
            if (!panel) return;
            panel.classList.toggle('panel-open');
            asstToggle.classList.toggle('active', panel.classList.contains('panel-open'));
        });
    }
}

function llmaSetupModelPill() {
    const pill    = document.getElementById('llma-model-pill');
    const popover = document.getElementById('llma-model-popover');
    const apply   = document.getElementById('llma-pop-apply');
    if (!pill || !popover) return;

    let cleanup = null;
    pill.addEventListener('click', (e) => {
        e.stopPropagation();
        const open = popover.style.display === 'none';
        popover.style.display = open ? '' : 'none';
        pill.setAttribute('aria-expanded', String(open));
        if (open) {
            cleanup = llmaPopoverClickAway(popover, pill, () => {
                popover.style.display = 'none';
                pill.setAttribute('aria-expanded', 'false');
            });
        } else if (cleanup) { cleanup(); }
    });

    if (apply) {
        apply.addEventListener('click', async () => {
            const modelSel = document.getElementById('llma-model-select');
            const model    = modelSel?.value;
            if (!model) { llmaShowToast('Select a model', 'info'); return; }

            LLMAState.currentModel = model;
            LLMAState.settings.currentModel = model;
            await llmaSaveSettings();

            llmaUpdateModelPill();
            popover.style.display = 'none';
            pill.setAttribute('aria-expanded', 'false');
            llmaShowToast('Model applied', 'success');
        });
    }
}

function llmaUpdateModelPill() {
    const label  = document.getElementById('llma-model-label');
    const status = document.getElementById('llma-model-status');
    if (!label) return;

    if (LLMAState.currentModel) {
        const shortModel = LLMAState.currentModel.length > 24
            ? LLMAState.currentModel.slice(0, 24) + '\u2026'
            : LLMAState.currentModel;
        label.textContent = shortModel;
        if (status) status.className = 'llma-model-status';
    } else {
        label.textContent = 'No model selected';
        if (status) status.className = 'llma-model-status offline';
    }
}

// -- Params Popover --
function llmaSetupParamsPopover() {
    const btn     = document.getElementById('llma-params-btn');
    const popover = document.getElementById('llma-params-popover');
    const apply   = document.getElementById('llma-params-apply');
    const reset   = document.getElementById('llma-params-reset');
    if (!btn || !popover) return;

    const ranges = [
        { range: 'llma-p-temperature', val: 'llma-p-temperature-val' },
        { range: 'llma-p-top-p',       val: 'llma-p-top-p-val'       },
    ];
    for (const { range, val } of ranges) {
        const input = document.getElementById(range);
        const span  = document.getElementById(val);
        if (input && span) {
            input.addEventListener('input', () => { span.textContent = parseFloat(input.value).toFixed(1); });
        }
    }

    let cleanup = null;
    btn.addEventListener('click', (e) => {
        e.stopPropagation();
        llmaCloseAllPopovers(popover);
        const open = popover.style.display === 'none';
        popover.style.display = open ? '' : 'none';
        btn.classList.toggle('active', open);

        if (open && LLMAState.activeThreadId) {
            const tp = LLMAState.threadParams[LLMAState.activeThreadId] || {};
            const d  = LLMAState.settings?.defaults || {};
            const get = (k) => tp[k] ?? d[k];
            llmaSetEl('llma-p-temperature',     get('temperature')     ?? 0.8);
            llmaSetEl('llma-p-temperature-val', get('temperature')     ?? 0.8, 'text');
            llmaSetEl('llma-p-max-tokens',      get('maxTokens')       ?? 2048);
            llmaSetEl('llma-p-top-p',           get('topP')            ?? 0.9);
            llmaSetEl('llma-p-top-p-val',       get('topP')            ?? 0.9, 'text');
            llmaSetEl('llma-p-context',         get('contextMessages') ?? 0);
        }

        if (open) {
            cleanup = llmaPopoverClickAway(popover, btn, () => {
                popover.style.display = 'none';
                btn.classList.remove('active');
            });
        } else if (cleanup) { cleanup(); }
    });

    if (apply) {
        apply.addEventListener('click', () => {
            if (!LLMAState.activeThreadId) { llmaShowToast('No active thread', 'info'); return; }
            LLMAState.threadParams[LLMAState.activeThreadId] = {
                temperature:     parseFloat(document.getElementById('llma-p-temperature')?.value) || undefined,
                maxTokens:       parseInt(document.getElementById('llma-p-max-tokens')?.value, 10) || undefined,
                topP:            parseFloat(document.getElementById('llma-p-top-p')?.value) || undefined,
                contextMessages: parseInt(document.getElementById('llma-p-context')?.value, 10) || 0,
            };
            llmaShowToast('Parameters applied to this thread', 'success');
            popover.style.display = 'none';
            btn.classList.remove('active');
        });
    }

    if (reset) {
        reset.addEventListener('click', () => {
            if (LLMAState.activeThreadId) delete LLMAState.threadParams[LLMAState.activeThreadId];
            llmaShowToast('Thread parameters reset', 'info');
            popover.style.display = 'none';
            btn.classList.remove('active');
        });
    }
}

// -- Export Menu --
function llmaSetupExportMenu() {
    const btn  = document.getElementById('llma-export-btn');
    const menu = document.getElementById('llma-export-menu');
    if (!btn || !menu) return;

    let cleanup = null;
    btn.addEventListener('click', (e) => {
        e.stopPropagation();
        llmaCloseAllPopovers(menu);
        const open = menu.style.display === 'none';
        menu.style.display = open ? '' : 'none';
        btn.classList.toggle('active', open);
        if (open) {
            cleanup = llmaPopoverClickAway(menu, btn, () => {
                menu.style.display = 'none';
                btn.classList.remove('active');
            });
        } else if (cleanup) { cleanup(); }
    });

    document.getElementById('llma-export-json')?.addEventListener('click', () => { llmaExportThread('json'); menu.style.display = 'none'; });
    document.getElementById('llma-export-md')?.addEventListener('click',   () => { llmaExportThread('md');   menu.style.display = 'none'; });
    document.getElementById('llma-export-txt')?.addEventListener('click',  () => { llmaExportThread('txt');  menu.style.display = 'none'; });
}

// -- Sidebar --
function llmaSetupSidebar() {
    const toggle = document.getElementById('llma-sidebar-toggle');
    if (toggle) {
        toggle.addEventListener('click', () => {
            const sidebar = document.getElementById('llma-sidebar');
            if (!sidebar) return;
            const mobile = window.innerWidth <= 680;
            if (mobile) {
                sidebar.classList.toggle('sidebar-open');
            } else {
                sidebar.classList.toggle('collapsed');
            }
        });
    }

    const search = document.getElementById('llma-thread-search');
    if (search) {
        search.addEventListener('input', llmaDebounce(() => llmaFilterThreads(search.value), 200));
    }

    const newBtn = document.getElementById('llma-new-thread-btn');
    if (newBtn) {
        newBtn.addEventListener('click', () => {
            llmaShowWelcome();
            if (window.innerWidth <= 680) {
                document.getElementById('llma-sidebar')?.classList.remove('sidebar-open');
            }
        });
    }
}

// -- Assistant Panel --
function llmaSetupAssistantPanel() {
    const collapse = document.getElementById('llma-panel-collapse');
    if (collapse) {
        collapse.addEventListener('click', () => {
            const panel = document.getElementById('llma-asst-panel');
            if (!panel) return;
            const tablet = window.innerWidth <= 1050;
            if (tablet) {
                panel.classList.remove('panel-open');
                document.getElementById('llma-asst-toggle')?.classList.remove('active');
            } else {
                panel.classList.toggle('collapsed');
                const isCollapsed = panel.classList.contains('collapsed');
                collapse.querySelector('svg path')?.setAttribute('d',
                    isCollapsed ? 'M4 1.5L7.5 5.5L4 9.5' : 'M7 1.5L3.5 5.5L7 9.5'
                );
            }
        });
    }
}

function llmaRenderAssistantPanel(assistantId) {
    const inner = document.getElementById('llma-panel-inner');
    if (!inner) return;
    const assistant = LLMAState.assistants.find(a => a.id === assistantId);
    if (!assistant) { llmaRenderAssistantPanelEmpty(); return; }

    const icon  = llmaCategoryIcon(assistant.icon || assistant.category || 'chat');
    const msgCount = LLMAState.messages.length;
    const tokens   = llmaApproxTokens(LLMAState.messages);

    inner.innerHTML = `
        <div class="llma-panel-card">
            <div class="llma-panel-avatar" style="background:${llmaGradientBg(assistant.color)};">
                ${assistant.avatar ? `<img src="${llmaEscapeHtml(assistant.avatar)}">` : `<span>${icon}</span>`}
            </div>
            <div class="llma-panel-name">${llmaEscapeHtml(assistant.name)}</div>
            <div class="llma-panel-desc">${llmaEscapeHtml(assistant.description || '')}</div>
            <div class="llma-panel-actions">
                <button class="llma-panel-action-btn" onclick="llmaOpenSettings();document.querySelector('.llma-modal-tab[data-tab=assistants]')?.click();">Edit</button>
            </div>
        </div>
        <div class="llma-panel-stats">
            <div class="llma-stat-box"><div class="llma-stat-num">${msgCount}</div><div class="llma-stat-lbl">Messages</div></div>
            <div class="llma-stat-box"><div class="llma-stat-num">~${tokens}</div><div class="llma-stat-lbl">Tokens</div></div>
        </div>
        <div class="llma-panel-memories">
            <div class="llma-mem-header">
                <span>Capabilities</span>
            </div>
            <div class="llma-memory-chips">
                ${assistant.instructions?.chat ? '<span class="llma-mem-chip">Chat</span>' : ''}
                ${assistant.instructions?.vision ? '<span class="llma-mem-chip">Vision</span>' : ''}
                ${assistant.instructions?.caption ? '<span class="llma-mem-chip">Caption</span>' : ''}
                ${assistant.instructions?.prompt ? '<span class="llma-mem-chip">Prompt</span>' : ''}
                ${assistant.instructions?.randomprompt ? '<span class="llma-mem-chip">Random</span>' : ''}
            </div>
        </div>`;
}

function llmaRenderAssistantPanelEmpty() {
    const inner = document.getElementById('llma-panel-inner');
    if (inner) inner.innerHTML = '<div class="llma-panel-empty">Select an assistant to see details.</div>';
}

// -- Welcome Screen Assistant Grid --
function llmaRenderWelcomeAssistants() {
    const grid = document.getElementById('llma-assistant-grid');
    if (!grid) return;

    let html = '';
    for (const a of LLMAState.assistants) {
        const icon  = llmaCategoryIcon(a.icon || a.category || 'chat');
        const isActive = a.id === LLMAState.activeAssistantId;
        html += `
            <div class="llma-asst-card${isActive ? ' active-card' : ''}" data-assistant-id="${llmaEscapeHtml(a.id)}"
                 tabindex="0" role="button" aria-label="${llmaEscapeHtml(a.name)}">
                <div class="llma-card-visual">
                    ${a.avatar
                        ? `<img class="llma-card-img" src="${llmaEscapeHtml(a.avatar)}">`
                        : `<div class="llma-card-gradient" style="background:${llmaGradientBg(a.color)};"><span class="llma-card-icon">${icon}</span></div>`}
                </div>
                <div class="llma-card-body">
                    <div class="llma-card-name">${llmaEscapeHtml(a.name)}</div>
                    <div class="llma-card-desc">${llmaEscapeHtml(a.description || '')}</div>
                    <span class="llma-card-badge">${llmaEscapeHtml(a.icon || a.category || 'chat')}</span>
                </div>
            </div>`;
    }

    // Create new card
    html += `
        <div class="llma-asst-card create-card" tabindex="0" role="button" aria-label="Create new assistant">
            <div class="llma-card-visual"><span class="llma-create-plus">+</span></div>
            <div class="llma-card-body">
                <div class="llma-card-name">Create New</div>
                <div class="llma-card-desc">Build a custom assistant</div>
            </div>
        </div>`;

    grid.innerHTML = html;

    // Bind click events
    grid.querySelectorAll('.llma-asst-card:not(.create-card)').forEach(card => {
        card.addEventListener('click', () => {
            const asstId = card.dataset.assistantId;
            LLMAState.activeAssistantId = asstId;
            llmaCreateThread(asstId);
        });
        card.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                card.click();
            }
        });
    });

    grid.querySelector('.create-card')?.addEventListener('click', () => {
        llmaOpenSettings();
        setTimeout(() => {
            document.querySelector('.llma-modal-tab[data-tab="assistants"]')?.click();
            llmaOpenCreateEditor();
        }, 100);
    });
}

// -- Settings Modal --
function llmaSetupSettingsModal() {
    const overlay  = document.getElementById('llma-settings-overlay');
    const close    = document.getElementById('llma-settings-close');
    const save     = document.getElementById('llma-settings-save');
    const resetBtn = document.getElementById('llma-settings-reset');

    if (close)   close.addEventListener('click', llmaCloseSettings);
    if (overlay) overlay.addEventListener('click', (e) => { if (e.target === overlay) llmaCloseSettings(); });

    if (save) {
        save.addEventListener('click', async () => {
            llmaReadSettingsFromModal();
            await llmaSaveSettings();
            llmaApplySettingsToState();
            llmaCloseSettings();
            llmaShowToast('Settings saved', 'success');
        });
    }

    if (resetBtn) {
        resetBtn.addEventListener('click', async () => {
            if (!confirm('Reset all settings to defaults?')) return;
            LLMAState.settings = { ...LLMA_DEFAULT_SETTINGS };
            llmaWriteSettingsToModal();
            await llmaSaveSettings();
            llmaApplySettingsToState();
            llmaShowToast('Settings reset to defaults', 'info');
        });
    }

    // Tab switching
    document.querySelectorAll('.llma-modal-tab').forEach(tab => {
        tab.addEventListener('click', () => {
            document.querySelectorAll('.llma-modal-tab').forEach(t => {
                t.classList.remove('active');
                t.setAttribute('aria-selected', 'false');
            });
            tab.classList.add('active');
            tab.setAttribute('aria-selected', 'true');
            document.querySelectorAll('.llma-modal-panel').forEach(p => { p.style.display = 'none'; });
            const panel = document.getElementById(`llma-settings-${tab.dataset.tab}`);
            if (panel) panel.style.display = '';
        });
    });

    // Sync range sliders
    const rangeMap = [
        { range: 'llma-s-temperature',    val: 'llma-s-temperature-val'    },
        { range: 'llma-s-top-p',          val: 'llma-s-top-p-val'          },
        { range: 'llma-s-repeat-penalty', val: 'llma-s-repeat-penalty-val' },
    ];
    for (const { range, val } of rangeMap) {
        const input = document.getElementById(range);
        const span  = document.getElementById(val);
        if (input && span) {
            input.addEventListener('input', () => { span.textContent = parseFloat(input.value).toFixed(2).replace(/\.?0+$/, ''); });
        }
    }

    // Assistant editor buttons
    document.getElementById('llma-create-asst-btn')?.addEventListener('click', llmaOpenCreateEditor);
    document.getElementById('llma-asst-save')?.addEventListener('click', llmaSaveAssistantFromEditor);
    document.getElementById('llma-asst-delete')?.addEventListener('click', () => llmaDeleteAssistant(llmaEditingAsstId));
    document.getElementById('llma-asst-cancel')?.addEventListener('click', () => {
        document.getElementById('llma-asst-editor').style.display = 'none';
    });
    document.getElementById('llma-editor-close')?.addEventListener('click', () => {
        document.getElementById('llma-asst-editor').style.display = 'none';
    });

    llmaSetupAvatarUpload();
    llmaSetupInstrTabs();
}

function llmaOpenSettings() {
    const overlay = document.getElementById('llma-settings-overlay');
    if (!overlay) return;
    llmaWriteSettingsToModal();
    llmaRenderAssistantList();
    overlay.style.display = '';
    setTimeout(() => document.getElementById('llma-settings-close')?.focus(), 60);
}

function llmaCloseSettings() {
    const overlay = document.getElementById('llma-settings-overlay');
    if (overlay) overlay.style.display = 'none';
    document.getElementById('llma-asst-editor').style.display = 'none';
}

function llmaReadSettingsFromModal() {
    const g = LLMAState.settings.defaults || {};
    g.temperature     = parseFloat(document.getElementById('llma-s-temperature')?.value)      || 0.8;
    g.maxTokens       = parseInt(document.getElementById('llma-s-max-tokens')?.value, 10)     || 2048;
    g.topP            = parseFloat(document.getElementById('llma-s-top-p')?.value)            || 0.9;
    g.topK            = parseInt(document.getElementById('llma-s-top-k')?.value, 10)          || 40;
    g.repeatPenalty   = parseFloat(document.getElementById('llma-s-repeat-penalty')?.value)   || 1.1;
    g.seed            = parseInt(document.getElementById('llma-s-seed')?.value, 10)           ?? -1;
    g.contextMessages = parseInt(document.getElementById('llma-s-context')?.value, 10)        || 0;
    g.stream          = document.getElementById('llma-s-stream')?.checked ?? true;
    LLMAState.settings.defaults = g;

    const u = LLMAState.settings.ui || {};
    u.markdownEnabled = document.getElementById('llma-s-markdown')?.checked ?? true;
    u.enterToSend     = document.getElementById('llma-s-enter-send')?.checked ?? true;
    u.showTokens      = document.getElementById('llma-s-show-tokens')?.checked ?? true;
    LLMAState.settings.ui = u;
}

function llmaWriteSettingsToModal() {
    const g = LLMAState.settings?.defaults || LLMA_DEFAULT_SETTINGS.defaults;
    llmaSetEl('llma-s-temperature',         g.temperature     ?? 0.8);
    llmaSetEl('llma-s-temperature-val',     g.temperature     ?? 0.8, 'text');
    llmaSetEl('llma-s-max-tokens',          g.maxTokens       ?? 2048);
    llmaSetEl('llma-s-top-p',               g.topP            ?? 0.9);
    llmaSetEl('llma-s-top-p-val',           g.topP            ?? 0.9, 'text');
    llmaSetEl('llma-s-top-k',               g.topK            ?? 40);
    llmaSetEl('llma-s-repeat-penalty',      g.repeatPenalty   ?? 1.1);
    llmaSetEl('llma-s-repeat-penalty-val',  g.repeatPenalty   ?? 1.1, 'text');
    llmaSetEl('llma-s-seed',                g.seed            ?? -1);
    llmaSetEl('llma-s-context',             g.contextMessages ?? 0);
    llmaSetElChecked('llma-s-stream',       g.stream          !== false);

    const u = LLMAState.settings?.ui || LLMA_DEFAULT_SETTINGS.ui;
    llmaSetElChecked('llma-s-markdown',     u.markdownEnabled !== false);
    llmaSetElChecked('llma-s-enter-send',   u.enterToSend     !== false);
    llmaSetElChecked('llma-s-show-tokens',  u.showTokens      !== false);
}

function llmaApplySettingsToState() {
    LLMAState.markdownEnabled = LLMAState.settings?.ui?.markdownEnabled !== false;
    LLMAState.enterToSend     = LLMAState.settings?.ui?.enterToSend     !== false;
    LLMAState.showTokens      = LLMAState.settings?.ui?.showTokens      !== false;
    llmaUpdateContextBar();
}

// -- Assistant List in Settings --
function llmaRenderAssistantList() {
    const container = document.getElementById('llma-assistant-list');
    if (!container) return;

    if (LLMAState.assistants.length === 0) {
        container.innerHTML = '<div class="llma-empty-state">No assistants found.</div>';
        return;
    }

    let html = '';
    for (const a of LLMAState.assistants) {
        const icon  = llmaCategoryIcon(a.icon || a.category || 'chat');
        html += `
            <div class="llma-asst-list-item" data-asst-id="${llmaEscapeHtml(a.id)}">
                <div class="llma-list-avatar" style="background:${llmaGradientBg(a.color)};">
                    ${a.avatar ? `<img src="${llmaEscapeHtml(a.avatar)}">` : icon}
                </div>
                <div class="llma-list-info">
                    <div class="llma-list-name">${llmaEscapeHtml(a.name)}${a.isBuiltIn ? ' <span class="llma-builtin-badge">(built-in)</span>' : ''}</div>
                    <div class="llma-list-desc">${llmaEscapeHtml(a.description || '')}</div>
                </div>
                <button class="llma-list-edit" data-asst-id="${llmaEscapeHtml(a.id)}">Edit</button>
            </div>`;
    }
    container.innerHTML = html;

    container.querySelectorAll('.llma-list-edit').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            const asstId = btn.dataset.asstId;
            const assistant = LLMAState.assistants.find(a => a.id === asstId);
            if (assistant) llmaShowAssistantEditor(assistant);
        });
    });
}

// -- Assistant Editor --
function llmaOpenCreateEditor() {
    llmaShowAssistantEditor(null);
}

function llmaShowAssistantEditor(assistant) {
    const editor = document.getElementById('llma-asst-editor');
    if (!editor) return;
    editor.style.display = '';
    llmaEditingAsstId = assistant ? assistant.id : null;

    const title = document.getElementById('llma-editor-title');
    if (title) title.textContent = assistant ? `Edit: ${assistant.name}` : 'New Assistant';

    const deleteBtn = document.getElementById('llma-asst-delete');
    if (deleteBtn) deleteBtn.style.display = (assistant && !assistant.isBuiltIn) ? '' : 'none';

    // Basic fields
    llmaSetEl('llma-asst-name', assistant?.name || '');
    llmaSetEl('llma-asst-desc', assistant?.description || '');

    const catSel = document.getElementById('llma-asst-category');
    if (catSel) catSel.value = assistant?.icon || assistant?.category || 'chat';

    const colorInput = document.getElementById('llma-asst-color');
    if (colorInput) colorInput.value = assistant?.color || colorInput.value;

    // Avatar preview
    const preview = document.getElementById('llma-avatar-preview');
    if (preview) {
        if (assistant?.avatar) {
            preview.innerHTML = `<img src="${llmaEscapeHtml(assistant.avatar)}">`;
        } else {
            preview.style.background = llmaGradientBg(assistant?.color);
            preview.textContent = llmaCategoryIcon(assistant?.icon || assistant?.category || 'chat');
        }
    }

    // Instruction fields
    const instructions = assistant?.instructions || {};
    for (const key of LLMA_INSTRUCTION_KEYS) {
        const hidden = document.getElementById(`llma-instr-${key}`);
        if (hidden) hidden.value = instructions[key] || '';
    }
    // Show first tab
    llmaActiveInstrTab = 'chat';
    llmaSwitchInstrTab('chat');

    // Parameters
    llmaSetEl('llma-asst-temperature', assistant?.parameters?.temperature ?? '');
    llmaSetEl('llma-asst-max-tokens',  assistant?.parameters?.maxTokens ?? '');
    llmaSetEl('llma-asst-top-p',       assistant?.parameters?.topP ?? '');
}

async function llmaSaveAssistantFromEditor() {
    const name = document.getElementById('llma-asst-name')?.value?.trim();
    if (!name) { llmaShowToast('Name is required', 'error'); return; }

    // Save current instruction tab content
    const area = document.getElementById('llma-instr-area');
    const hidden = document.getElementById(`llma-instr-${llmaActiveInstrTab}`);
    if (area && hidden) hidden.value = area.value;

    const assistant = {
        id:          llmaEditingAsstId || null,
        name:        name,
        description: document.getElementById('llma-asst-desc')?.value?.trim() || '',
        icon:        document.getElementById('llma-asst-category')?.value || 'chat',
        color:       document.getElementById('llma-asst-color')?.value || '',
        instructions: {},
        parameters:   {},
    };

    for (const key of LLMA_INSTRUCTION_KEYS) {
        const val = document.getElementById(`llma-instr-${key}`)?.value?.trim();
        if (val) assistant.instructions[key] = val;
    }

    const temp = document.getElementById('llma-asst-temperature');
    const maxTok = document.getElementById('llma-asst-max-tokens');
    const topP = document.getElementById('llma-asst-top-p');
    if (temp?.value) assistant.parameters.temperature = parseFloat(temp.value);
    if (maxTok?.value) assistant.parameters.maxTokens = parseInt(maxTok.value, 10);
    if (topP?.value) assistant.parameters.topP = parseFloat(topP.value);

    try {
        await llmaRequest('LLMAssistantSaveAssistant', { assistant });
        document.getElementById('llma-asst-editor').style.display = 'none';
        await llmaLoadAssistants();
        llmaRenderAssistantList();
        llmaRenderWelcomeAssistants();
        llmaShowToast('Assistant saved', 'success');
    } catch (ex) {
        llmaShowToast('Failed to save assistant', 'error');
    }
}

async function llmaDeleteAssistant(id) {
    if (!id || !confirm('Delete this assistant?')) return;
    try {
        await llmaRequest('LLMAssistantDeleteAssistant', { assistantId: id });
        document.getElementById('llma-asst-editor').style.display = 'none';
        await llmaLoadAssistants();
        llmaRenderAssistantList();
        llmaRenderWelcomeAssistants();
        llmaShowToast('Assistant deleted', 'info');
    } catch {
        llmaShowToast('Failed to delete assistant', 'error');
    }
}

// -- Avatar Upload --
function llmaSetupAvatarUpload() {
    const uploadBtn = document.getElementById('llma-avatar-upload-btn');
    const fileInput = document.getElementById('llma-avatar-file');
    if (uploadBtn && fileInput) {
        uploadBtn.addEventListener('click', () => fileInput.click());
        fileInput.addEventListener('change', async () => {
            if (!fileInput.files?.length) return;
            const dataUrl = await llmaFileToBase64(fileInput.files[0]);
            const preview = document.getElementById('llma-avatar-preview');
            if (preview) {
                preview.innerHTML = `<img src="${dataUrl}">`;
                preview.dataset.avatarData = dataUrl;
            }
            fileInput.value = '';
        });
    }

    // Color picker updates preview
    const colorInput = document.getElementById('llma-asst-color');
    if (colorInput) {
        colorInput.addEventListener('input', () => {
            const preview = document.getElementById('llma-avatar-preview');
            if (preview && !preview.querySelector('img')) {
                preview.style.background = `linear-gradient(135deg,${colorInput.value},${llmaShiftColor(colorInput.value, -40)})`;
            }
        });
    }
}

// -- Instruction Tabs --
function llmaSetupInstrTabs() {
    document.querySelectorAll('.llma-instr-tab').forEach(tab => {
        tab.addEventListener('click', () => {
            const mode = tab.dataset.mode;
            if (mode) llmaSwitchInstrTab(mode);
        });
    });
}

function llmaSwitchInstrTab(mode) {
    // Save current tab content
    const area = document.getElementById('llma-instr-area');
    const prevHidden = document.getElementById(`llma-instr-${llmaActiveInstrTab}`);
    if (area && prevHidden) prevHidden.value = area.value;

    // Switch to new tab
    llmaActiveInstrTab = mode;
    document.querySelectorAll('.llma-instr-tab').forEach(t => {
        t.classList.toggle('active', t.dataset.mode === mode);
    });

    // Load new tab content
    const newHidden = document.getElementById(`llma-instr-${mode}`);
    if (area && newHidden) area.value = newHidden.value;
}

// -- Keyboard Shortcuts --
function llmaSetupKeyboardShortcuts() {
    document.addEventListener('keydown', (e) => {
        if (!document.getElementById('llma-container')) return;
        if (e.ctrlKey && e.key === 'n' && !e.shiftKey) {
            if (document.activeElement?.closest('#llma-container')) {
                e.preventDefault();
                llmaShowWelcome();
            }
        }
        if (e.key === 'Escape') {
            llmaCloseAllPopovers();
            if (document.getElementById('llma-settings-overlay')?.style.display !== 'none') {
                llmaCloseSettings();
            }
        }
    });
}

// -- Responsive Layout --
function llmaSetupResponsive() {
    const check = () => {
        const w = window.innerWidth;
        const toggle = document.getElementById('llma-asst-toggle');
        if (toggle) toggle.style.display = w <= 1050 ? '' : 'none';
    };
    check();
    window.addEventListener('resize', llmaDebounce(check, 120));
}

// -- Popover Management --
function llmaCloseAllPopovers(except) {
    const popovers = ['llma-model-popover', 'llma-params-popover', 'llma-export-menu'];
    for (const id of popovers) {
        const el = document.getElementById(id);
        if (el && el !== except) el.style.display = 'none';
    }
    document.querySelectorAll('.llma-icon-btn.active').forEach(b => b.classList.remove('active'));
    document.getElementById('llma-model-pill')?.setAttribute('aria-expanded', 'false');
}

// -- Prompt Buttons (Generate Tab) --
let llmaPromptButtonsInjected = false;

function llmaSetupPromptButtons() {
    if (llmaPromptButtonsInjected) return;
    const promptBox = document.getElementById('alt_prompt_textbox');
    if (!promptBox) return;
    if (document.getElementById('llma-enhance-prompt-btn')) { llmaPromptButtonsInjected = true; return; }

    const parent = promptBox.closest('.prompt-area, .prompt_box_area, .input-group') || promptBox.parentElement;
    if (!parent) return;

    const btnRow = document.createElement('div');
    btnRow.className = 'llma-prompt-btn-row';

    const enhanceBtn = document.createElement('button');
    enhanceBtn.id = 'llma-enhance-prompt-btn';
    enhanceBtn.className = 'basic-button';
    enhanceBtn.textContent = 'Enhance Prompt';
    enhanceBtn.addEventListener('click', async () => {
        const current = promptBox.value?.trim();
        if (!current) return;
        enhanceBtn.disabled = true;
        enhanceBtn.textContent = 'Enhancing...';
        try {
            const resp = await llmaRequest('LLMAssistantSendMessage', { message: current, instructionId: 'prompt' });
            if (resp.response) { promptBox.value = resp.response; if (typeof triggerChangeFor === 'function') triggerChangeFor(promptBox); }
        } catch { llmaShowToast('Enhance failed', 'error'); }
        finally { enhanceBtn.disabled = false; enhanceBtn.textContent = 'Enhance Prompt'; }
    });

    const randomBtn = document.createElement('button');
    randomBtn.id = 'llma-random-prompt-btn';
    randomBtn.className = 'basic-button';
    randomBtn.textContent = 'Random Prompt';
    randomBtn.addEventListener('click', async () => {
        randomBtn.disabled = true;
        randomBtn.textContent = 'Generating...';
        try {
            const resp = await llmaRequest('LLMAssistantSendMessage', { message: 'Generate a random prompt.', instructionId: 'randomprompt' });
            if (resp.response) { promptBox.value = resp.response; if (typeof triggerChangeFor === 'function') triggerChangeFor(promptBox); }
        } catch { llmaShowToast('Random prompt failed', 'error'); }
        finally { randomBtn.disabled = false; randomBtn.textContent = 'Random Prompt'; }
    });

    btnRow.appendChild(enhanceBtn);
    btnRow.appendChild(randomBtn);
    parent.appendChild(btnRow);
    llmaPromptButtonsInjected = true;
}

// -- Auto-boot --
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => {
        if (document.getElementById('llma-container')) llmaInit();
    });
} else {
    if (document.getElementById('llma-container')) {
        llmaInit();
    } else {
        const obs = new MutationObserver(() => {
            if (document.getElementById('llma-container')) {
                obs.disconnect();
                llmaInit();
            }
        });
        obs.observe(document.body, { childList: true, subtree: true });
    }
}

// Also try to inject prompt buttons when DOM changes
const llmaPromptObs = new MutationObserver(() => {
    if (!llmaPromptButtonsInjected) llmaSetupPromptButtons();
    if (llmaPromptButtonsInjected) llmaPromptObs.disconnect();
});
llmaPromptObs.observe(document.body, { childList: true, subtree: true });

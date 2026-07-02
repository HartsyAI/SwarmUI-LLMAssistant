/* ================================================================
   LLM Assistant - llmassistant.js
   Main entry point. Wires all modules together.
   Init sequence, top bar, settings modal, assistants, responsive.
   ================================================================ */

'use strict';

// -- Instruction Keys --
const LLMA_INSTRUCTION_KEYS = ['chat', 'vision', 'caption', 'prompt', 'randomprompt', 'instructiongen', 'companion'];

// Rotating tips shown on the personalized welcome hero. One is picked at random
// each time llmaRenderPersonalizedWelcome() runs (per the user's "change each time" pref).
// Keep these short, useful, and Swarm/extension-specific.
const LLMA_WELCOME_TIPS = [
    "Tip: I can write to long-term memory — share your name, what you're working on, or any preference and I'll remember it next time.",
    "Tip: Use the <b>generate_image</b> tool in chat to spin up a SwarmUI render without leaving the conversation.",
    "Tip: Drop an image into the input area to ask questions about it (vision-capable assistants only).",
    "Tip: Each assistant has its own personality and tool set — try creating a coding agent, a prompt-crafter, and a research agent and switching between them.",
    "Tip: Press <kbd>Ctrl</kbd>+<kbd>K</kbd> to jump straight to the input box.",
    "Tip: Threads are grouped by date in the sidebar — search by title to find an old conversation fast.",
    "Tip: Click any code block in a reply to copy it. Markdown, mermaid diagrams, and KaTeX math all render inline.",
    "Tip: Adjust temperature, max tokens, and the model per-thread from the assistant panel on the right.",
    "Tip: Tool calls (web search, file read, image gen) show as compact cards in chat — click to expand.",
    "Tip: Your memory profile is strictly per-user — no other Swarm user can read it, ever.",
];

// -- Editing state --
let llmaEditingAsstId = null;
let llmaActiveInstrTab = 'chat';

// -- Model list auto-retry --
// On a fresh restart the LLM backend may still be initializing when the tab opens, so its
// provider hasn't self-registered yet and the model list comes back empty. Rather than make the
// user click refresh, poll until the backend registers and models appear (or we give up).
let llmaModelRetryTimer = null;
let llmaModelRetryCount = 0;
const LLMA_MODEL_RETRY_MAX = 20;         // give up after ~1 minute of polling
const LLMA_MODEL_RETRY_INTERVAL = 3000;  // 3s between attempts

// -- Entry Point --
async function llmaInit() {
    if (!document.getElementById('llma-container')) return;

    // Paint skeleton cards immediately so the gallery isn't blank during the first network round.
    // If the load finishes faster than the user can blink, llmaRenderWelcomeAssistants() will
    // replace the skeleton with real cards before they ever render visibly.
    llmaRenderWelcomeSkeleton();

    const [, ] = await Promise.allSettled([
        llmaLoadVendoredLibs(),
        llmaLoadSettings(),
    ]);

    const [, , , , sessionStateResult, ] = await Promise.allSettled([
        llmaLoadAssistants(),
        llmaLoadThreads(),
        llmaLoadModels(),
        llmaLoadTools(),
        llmaGetSessionState(),
        llmaLoadUserProfile(),
    ]);
    const sessionState = sessionStateResult?.status === 'fulfilled' ? (sessionStateResult.value || {}) : {};

    // Apply loaded settings to state
    LLMAState.markdownEnabled = LLMAState.settings?.ui?.markdownEnabled !== false;
    LLMAState.enterToSend     = LLMAState.settings?.ui?.enterToSend     !== false;
    LLMAState.showTokens      = LLMAState.settings?.ui?.showTokens      !== false;
    LLMAState.currentModel    = sessionState.currentModel || LLMAState.settings?.currentModel || null;
    // Restore side-by-side compare selection (lane B model + whether compare was on + per-lane device).
    LLMAState.compareMode     = sessionState.compareMode === true;
    LLMAState.compareModelB   = sessionState.compareModelB || null;
    LLMAState.laneBackends     = (sessionState.laneBackends && typeof sessionState.laneBackends === 'object') ? sessionState.laneBackends : {};
    // llmaLoadModels() ran in parallel with the session-state load above, so the dropdown may have
    // been populated before LLMAState.currentModel was known. Re-apply the remembered model now.
    llmaApplyModelSelection();

    llmaSetupTopBar();
    llmaSetupSidebar();
    llmaSetupInput();
    if (typeof llmaSetupScroll === 'function') llmaSetupScroll();
    if (typeof llmaSetupFindBar === 'function') llmaSetupFindBar();
    if (typeof llmaSetupBulkBar === 'function') llmaSetupBulkBar();
    llmaSetupSettingsModal();
    llmaSetupAssistantPanel();
    llmaSetupSplitBars();
    llmaSetupKeyboardShortcuts();
    llmaSetupResponsive();
    if (typeof llmaSetupAssets === 'function') llmaSetupAssets();

    // Restore the last active thread from session state if present and still exists.
    const resumeThreadId = sessionState.activeThreadId;
    const resumeThread   = resumeThreadId && LLMAState.threads.find(t => t.id === resumeThreadId);
    if (resumeThread) {
        await llmaSwitchThread(resumeThreadId);
    } else {
        // Fall back: drop straight into an empty chat with the last-used assistant.
        // No thread is persisted until the user actually sends a message.
        const startAsst = LLMAState.assistants.find(a => a.id === LLMAState.activeAssistantId)
                        || LLMAState.assistants[0];
        if (startAsst) {
            llmaStartChatWithAssistant(startAsst.id, { persist: false });
        } else {
            llmaShowWelcome();
        }
    }
    llmaUpdateModelStatus();
}

// Start a fresh empty chat session with an assistant without persisting
// a new thread to the backend. The thread is only saved when the user sends
// their first message (see llmaSendMessage → llmaCreateThread).
function llmaStartChatWithAssistant(assistantId, { persist = true } = {}) {
    const assistant = LLMAState.assistants.find(a => a.id === assistantId);
    if (!assistant) return;

    LLMAState.activeAssistantId = assistantId;
    LLMAState.activeThreadId    = null;
    LLMAState.messages          = [];
    LLMAState.assets            = [];

    // Clear rendered messages so the empty-chat CSS state kicks in
    const msgList = document.getElementById('llma-messages');
    if (msgList) msgList.innerHTML = '';

    const titleEl = document.getElementById('llma-thread-title');
    if (titleEl) titleEl.textContent = `Chat with ${assistant.name}`;

    llmaShowChatPanel();
    llmaUpdateContextBar();
    llmaRenderThreadList(LLMAState.threads);
    llmaRenderAssistantPanel(assistantId);
    llmaRenderEmptyChatGreeting();

    if (persist) {
        llmaRequest('LLMAssistantSetActiveAssistant', { assistantId }).catch(() => {});
    }

    setTimeout(() => document.getElementById('llma-input')?.focus(), LLMA_CONSTANTS.INPUT_FOCUS_DELAY_MS);
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
        LLMAState.canWriteShared = !!result?.canWriteShared;
        LLMAState.loadErrors.assistants = null;
    } catch (e) {
        LLMAState.assistants = [];
        LLMAState.loadErrors.assistants = llmaShortError(e) || 'Failed to load assistants';
    }
}

// -- Model Loading --
// opts.noRetry: skip auto-scheduling a readiness poll on an empty/failed result (used by the
// retry tick itself, which manages its own loop).
async function llmaLoadModels(opts = {}) {
    try {
        const result = await llmaRequest('LLMAssistantGetModels', {});
        const models = Array.isArray(result?.models) ? result.models : [];
        LLMAState.availableModels = models;
        LLMAState.loadErrors.models = null;
        // Empty model list with no API error = no backends running. Distinguish from a load failure
        // so the welcome banner can show the right copy (and link to Server > Backends).
        LLMAState.noBackendsRunning = models.length === 0;
        // Some backends may have timed out / errored while others returned models — surface a
        // non-blocking notice rather than silently dropping them from the list.
        const modelWarnings = Array.isArray(result?.warnings) ? result.warnings : [];
        LLMAState.loadErrors.modelWarnings = modelWarnings;
        if (modelWarnings.length > 0 && typeof llmaShowToast === 'function') {
            llmaShowToast(`Some LLM backends did not respond: ${modelWarnings.join('; ')}`, 'warning');
        }

        const sel = document.getElementById('llma-model-select');
        if (sel) {
            if (models.length > 0) {
                // Group models by provider for clearer display
                const byProvider = {};
                for (const m of models) {
                    const prov = m.provider || 'unknown';
                    if (!byProvider[prov]) byProvider[prov] = [];
                    byProvider[prov].push(m);
                }
                const providerKeys = Object.keys(byProvider);
                // Append a small 📷 to vision-capable model names so users can pick one for image input
                // without having to send-and-discover. Unknown vision-capability stays badge-less.
                const labelFor = (m) => {
                    const base = llmaEscapeHtml(m.name || m.id);
                    return llmaIsVisionModel(m) === true ? `${base} 📷` : base;
                };
                if (providerKeys.length === 1) {
                    sel.innerHTML = models.map(m =>
                        `<option value="${llmaEscapeHtml(m.id)}">${labelFor(m)}</option>`
                    ).join('');
                } else {
                    sel.innerHTML = providerKeys.map(prov =>
                        `<optgroup label="${llmaEscapeHtml(prov)}">` +
                        byProvider[prov].map(m =>
                            `<option value="${llmaEscapeHtml(m.id)}">${labelFor(m)}</option>`
                        ).join('') +
                        `</optgroup>`
                    ).join('');
                }
            } else {
                sel.innerHTML = '<option value="" disabled>No models found</option>';
            }
        }

        if (models.length > 0) {
            llmaSetModelSelectError(false);
            llmaApplyModelSelection();
            // Backend registered and gave us models — stop any in-flight readiness polling.
            llmaStopModelRetry();
        } else {
            llmaSetModelSelectError(true);
            // Empty list usually means the backend is still loading — keep polling until it shows up.
            if (!opts.noRetry) llmaScheduleModelRetry();
        }
    } catch (e) {
        llmaSetModelSelectError(true);
        LLMAState.loadErrors.models = llmaShortError(e) || 'Failed to load models';
        LLMAState.noBackendsRunning = false;
        if (!opts.noRetry) llmaScheduleModelRetry();
    }
    llmaUpdateModelStatus();
}

// Apply the remembered model to the dropdown. Prefers the resolved per-user choice
// (LLMAState.currentModel, set from session state) over the global settings fallback, so the
// user's selection wins regardless of the parallel load order at startup. Falls back to the first
// model only when nothing is remembered or the saved one is gone (eg the GGUF was deleted).
// True when SwarmUI's bundled jQuery + select2 are available (they're loaded globally by core).
function llmaModelSelect2Ready() {
    return typeof window.$ === 'function' && window.$.fn && typeof window.$.fn.select2 === 'function';
}

// Upgrade a native model <select> to SwarmUI's searchable select2 dropdown (the same component the
// Generate tab uses, theme "bootstrap-5"). Re-init-safe: destroys any prior instance first, so a
// model-list refresh that replaces the <option>s is always reflected. select2 raises its selection
// on the jQuery layer, which does NOT trigger addEventListener('change') handlers — so we bridge it
// to a native change event, keeping the existing "save model" handlers firing exactly once.
// No-ops (leaving the plain native select) if select2 isn't present.
function llmaInitModelSelect2(sel) {
    if (!sel || !llmaModelSelect2Ready()) return;
    const $sel = window.$(sel);
    if ($sel.data('select2')) {
        try { $sel.select2('destroy'); } catch (_) { /* not initialized */ }
    }
    // dropdownCssClass tags the body-appended panel so our theme CSS can reach it (the panel lives
    // outside .llma-topbar, so scoped selectors miss it — this is why the list looked unstyled).
    $sel.select2({ theme: 'bootstrap-5', width: '240px', dropdownCssClass: 'llma-select2-dropdown' });
    $sel.off('select2:select.llma').on('select2:select.llma', () => sel.dispatchEvent(new Event('change', { bubbles: true })));
}

function llmaApplyModelSelection() {
    const sel = document.getElementById('llma-model-select');
    const models = LLMAState.availableModels || [];
    if (!sel || models.length === 0) return;
    const saved = LLMAState.currentModel || LLMAState.settings?.currentModel;
    if (saved && models.some(m => m.id === saved)) {
        sel.value = saved;
        LLMAState.currentModel = saved;
    } else if (!sel.value || !models.some(m => m.id === sel.value)) {
        sel.selectedIndex = 0;
        LLMAState.currentModel = sel.value;
    }
    // Enhance to the searchable dropdown (after the value is set so it shows the right selection).
    llmaInitModelSelect2(sel);
    llmaUpdateModelStatus();
    // Keep the compare lane-B picker in sync with the freshly-loaded model list.
    if (typeof llmaCompareOnModelsLoaded === 'function') llmaCompareOnModelsLoaded();
}

// Schedule a single delayed re-fetch of the model list. Each empty load reschedules the next, so
// the list fills in automatically once the backend finishes loading — no manual refresh needed.
function llmaScheduleModelRetry() {
    if (llmaModelRetryTimer) return;                          // already polling
    if (llmaModelRetryCount >= LLMA_MODEL_RETRY_MAX) return;  // gave up
    llmaModelRetryTimer = setTimeout(async () => {
        llmaModelRetryTimer = null;
        llmaModelRetryCount++;
        await llmaLoadModels();  // reschedules itself if still empty, stops once models appear
    }, LLMA_MODEL_RETRY_INTERVAL);
}

function llmaStopModelRetry() {
    if (llmaModelRetryTimer) {
        clearTimeout(llmaModelRetryTimer);
        llmaModelRetryTimer = null;
    }
    llmaModelRetryCount = 0;
}

// -- Top Bar --
function llmaSetupTopBar() {
    llmaSetupModelSelect();
    if (typeof llmaSetupCompare === 'function') llmaSetupCompare();
    llmaSetupModelMenu();
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

// -- Model menu (compact popover on narrow bars) --
// A ResizeObserver adds .llma-bar-compact to the top bar below a width threshold; CSS then turns the inline
// model selects into a popover. This opens/closes it via the trigger button, on outside click, or after a
// selection.
function llmaSetupModelMenu() {
    const anchor = document.getElementById('llma-model-anchor');
    const btn    = document.getElementById('llma-model-menu-btn');
    if (!anchor || !btn) return;
    // Collapse to the popover when the top bar itself is too narrow for the inline selects. A ResizeObserver
    // on the bar catches width changes from sidebar/panel collapse too (not just window resize).
    const topbar = document.querySelector('.llma-topbar');
    if (topbar && typeof ResizeObserver !== 'undefined') {
        const COMPACT_BELOW = 900;
        const applyCompact = () => {
            const compact = topbar.clientWidth < COMPACT_BELOW;
            topbar.classList.toggle('llma-bar-compact', compact);
            if (!compact) { anchor.classList.remove('llma-model-open'); btn.classList.remove('active'); }
        };
        new ResizeObserver(applyCompact).observe(topbar);
        applyCompact();
    }
    btn.addEventListener('click', (e) => {
        e.stopPropagation();
        const open = !anchor.classList.contains('llma-model-open');
        llmaCloseAllPopovers();
        anchor.classList.toggle('llma-model-open', open);
        btn.classList.toggle('active', open);
    });
    // Close after picking a model (native change bubbles up from either lane's select).
    anchor.addEventListener('change', () => {
        anchor.classList.remove('llma-model-open');
        btn.classList.remove('active');
    });
    // Outside-click closes it (the select2 dropdown lives on <body>, so ignore clicks inside it).
    document.addEventListener('click', (e) => {
        if (!anchor.classList.contains('llma-model-open')) return;
        if (anchor.contains(e.target) || e.target.closest('.select2-container, .select2-dropdown')) return;
        anchor.classList.remove('llma-model-open');
        btn.classList.remove('active');
    });
}

function llmaSetupModelSelect() {
    const sel = document.getElementById('llma-model-select');
    if (!sel) return;
    sel.addEventListener('change', async () => {
        const model = sel.value;
        if (!model) return;
        LLMAState.currentModel = model;
        LLMAState.settings.currentModel = model;
        await llmaSaveSettings();
        llmaSetSessionState({ currentModel: model });
        llmaUpdateModelStatus();
    });
    // Refresh button — calls core's TriggerRefresh (re-scans every model handler including the
    // LLM one the extension registered), then re-fetches our model list. Lets the user drop a
    // new GGUF into Models/llm/ and see it show up without restarting SwarmUI.
    const refreshBtn = document.getElementById('llma-model-refresh');
    if (refreshBtn) {
        refreshBtn.addEventListener('click', async () => {
            if (refreshBtn.dataset.llmaBusy === '1') return;
            refreshBtn.dataset.llmaBusy = '1';
            refreshBtn.classList.add('spinning');
            try {
                // strong=true triggers a full RefreshAllModelSets server-side.
                llmaStopModelRetry();  // reset the give-up counter so polling can restart if still empty
                await llmaRequest('TriggerRefresh', { strong: true });
                await llmaLoadModels();
                llmaShowToast('Model list refreshed.', 'info');
            } catch (e) {
                llmaShowToast(llmaShortError(e) || 'Refresh failed', 'error');
            } finally {
                refreshBtn.classList.remove('spinning');
                refreshBtn.dataset.llmaBusy = '0';
            }
        });
    }

    // Unload button — frees the resident LLM model's VRAM/RAM so an image model can load. The model
    // lazily reloads on the next chat message, so this is safe to click any time.
    const unloadBtn = document.getElementById('llma-model-unload');
    if (unloadBtn) {
        unloadBtn.addEventListener('click', async () => {
            if (unloadBtn.dataset.llmaBusy === '1') return;
            unloadBtn.dataset.llmaBusy = '1';
            unloadBtn.classList.add('spinning');
            try {
                const res = await llmaRequest('LLMAssistantUnloadModels', {});
                const freed = res?.freed || 0;
                llmaShowToast(freed > 0 ? 'LLM model unloaded — VRAM freed.' : 'No LLM model was loaded.', 'info');
            } catch (e) {
                llmaShowToast(llmaShortError(e) || 'Unload failed', 'error');
            } finally {
                unloadBtn.classList.remove('spinning');
                unloadBtn.dataset.llmaBusy = '0';
            }
        });
    }
}

// Reflects "something's wrong with model selection" as a red outline on the dropdown (no model
// available / none selected). Healthy state shows no indicator — matches SwarmUI's quiet styling.
function llmaSetModelSelectError(hasError) {
    document.getElementById('llma-model-select')?.classList.toggle('error', !!hasError);
}

// Briefly pulse + focus the model dropdown to point the user at it (eg they tried to send with no
// model selected). The toast says what's wrong; this says where to fix it.
function llmaFlashModelSelect() {
    const sel = document.getElementById('llma-model-select');
    if (!sel) return;
    sel.classList.add('error', 'llma-flash');
    try { sel.focus({ preventScroll: false }); } catch { /* older browsers */ }
    setTimeout(() => sel.classList.remove('llma-flash'), 1100);
}

function llmaUpdateModelStatus() {
    llmaSetModelSelectError(!LLMAState.currentModel);
    llmaUpdateAttachAvailability();
}

// Toggles the paperclip / attach-image button based on the active model's vision capability.
// Tri-state: known-vision (enabled, normal tooltip), known-text-only (disabled, hint tooltip),
// unknown (enabled, no change) — see llmaIsVisionModel for the fail-open semantics.
function llmaUpdateAttachAvailability() {
    const label = document.getElementById('llma-attach-label');
    if (!label) return;
    const model = (LLMAState.availableModels || []).find(m => m.id === LLMAState.currentModel);
    const cap = llmaIsVisionModel(model);
    if (cap === false) {
        label.classList.add('llma-attach-disabled');
        label.setAttribute('aria-disabled', 'true');
        label.title = "The current model doesn't support image input. Pick a vision model to attach images.";
    } else {
        label.classList.remove('llma-attach-disabled');
        label.removeAttribute('aria-disabled');
        label.title = 'Attach image';
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
            const d  = LLMAState.settings?.parameters || {};
            const get = (k) => tp[k] ?? d[k];
            llmaSetEl('llma-p-temperature',     get('temperature')        ?? 0.8);
            llmaSetEl('llma-p-temperature-val', get('temperature')        ?? 0.8, 'text');
            llmaSetEl('llma-p-max-tokens',      get('maxTokens')          ?? 2048);
            llmaSetEl('llma-p-top-p',           get('topP')               ?? 0.9);
            llmaSetEl('llma-p-top-p-val',       get('topP')               ?? 0.9, 'text');
            llmaSetEl('llma-p-context',         get('maxContextMessages') ?? 0);
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
                topP:               parseFloat(document.getElementById('llma-p-top-p')?.value) || undefined,
                maxContextMessages: parseInt(document.getElementById('llma-p-context')?.value, 10) || 0,
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
            const cont = document.getElementById('llma-container');
            if (!cont) return;
            const mobile = window.innerWidth <= 680;
            if (mobile) {
                document.getElementById('llma-sidebar')?.classList.toggle('sidebar-open');
            } else {
                const isCollapsed = cont.classList.contains('sidebar-collapsed');
                cont.classList.toggle('sidebar-collapsed', !isCollapsed);
                localStorage.setItem('llma_sidebar_collapsed', !isCollapsed ? 'true' : 'false');
                const leftBtn = document.getElementById('llma-split-left-btn');
                if (leftBtn) {
                    leftBtn.innerHTML = !isCollapsed ? '&#x21DB;' : '&#x21DA;';
                }
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
            // Start a fresh empty chat with the current assistant (no thread
            // is saved until the first message is sent). If there is no
            // current assistant, fall back to the welcome gallery.
            if (LLMAState.activeAssistantId
                && LLMAState.assistants.some(a => a.id === LLMAState.activeAssistantId)) {
                llmaStartChatWithAssistant(LLMAState.activeAssistantId, { persist: false });
            } else {
                llmaShowWelcome();
            }
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
            const cont = document.getElementById('llma-container');
            if (!cont) return;
            const tablet = window.innerWidth <= 1050;
            if (tablet) {
                const panel = document.getElementById('llma-asst-panel');
                panel?.classList.remove('panel-open');
                document.getElementById('llma-asst-toggle')?.classList.remove('active');
            } else {
                // Use the same container-level toggle as the split bar quickbutton
                const isCollapsed = cont.classList.contains('panel-collapsed');
                cont.classList.toggle('panel-collapsed', !isCollapsed);
                localStorage.setItem('llma_panel_collapsed', !isCollapsed ? 'true' : 'false');
                const rightBtn = document.getElementById('llma-split-right-btn');
                if (rightBtn) {
                    rightBtn.innerHTML = !isCollapsed ? '&#x21DA;' : '&#x21DB;';
                }
            }
        });
    }
}

// -- Resizable split bars (like the Generate page) --
function llmaSetupSplitBars() {
    const container = document.getElementById('llma-container');
    if (!container) return;

    // Restore saved sizes
    const savedLeft  = parseInt(localStorage.getItem('llma_sidebar_w') || '0', 10);
    const savedRight = parseInt(localStorage.getItem('llma_panel_w')   || '0', 10);
    if (savedLeft  > 0) container.style.setProperty('--llma-sidebar-w', `${savedLeft}px`);
    if (savedRight > 0) container.style.setProperty('--llma-panel-w',   `${savedRight}px`);

    const leftBar  = document.getElementById('llma-split-left');
    const rightBar = document.getElementById('llma-split-right');

    // Layout clamp ranges pulled from LLMA_CONSTANTS so the rest of the UI agrees on bounds.
    const MIN_SIDEBAR = LLMA_CONSTANTS.MIN_SIDEBAR_PX;
    const MAX_SIDEBAR = LLMA_CONSTANTS.MAX_SIDEBAR_PX;
    const MIN_PANEL   = LLMA_CONSTANTS.MIN_PANEL_PX;
    const MAX_PANEL   = LLMA_CONSTANTS.MAX_PANEL_PX;
    const BAR_WIDTH   = LLMA_CONSTANTS.SPLIT_BAR_PX;

    const attach = (bar, which) => {
        if (!bar) return;
        let dragging = false;

        const onMove = (e) => {
            if (!dragging) return;
            const rect = container.getBoundingClientRect();
            const x = e.clientX - rect.left;
            if (which === 'left') {
                const w = Math.max(MIN_SIDEBAR, Math.min(MAX_SIDEBAR, x));
                container.style.setProperty('--llma-sidebar-w', `${w}px`);
            } else {
                const fromRight = rect.width - x - BAR_WIDTH;
                const w = Math.max(MIN_PANEL, Math.min(MAX_PANEL, fromRight));
                container.style.setProperty('--llma-panel-w', `${w}px`);
            }
            e.preventDefault();
        };

        const onUp = () => {
            if (!dragging) return;
            dragging = false;
            bar.classList.remove('dragging');
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            // Persist
            const cs = getComputedStyle(container);
            if (which === 'left') {
                const px = parseInt(cs.getPropertyValue('--llma-sidebar-w'), 10);
                if (px > 0) localStorage.setItem('llma_sidebar_w', `${px}`);
            } else {
                const px = parseInt(cs.getPropertyValue('--llma-panel-w'), 10);
                if (px > 0) localStorage.setItem('llma_panel_w', `${px}`);
            }
        };

        bar.addEventListener('mousedown', (e) => {
            if (e.button !== 0 || e.target.classList.contains('t2i-split-quickbutton')) return;
            // Uncollapse if collapsed — user is dragging to resize
            if (which === 'left' && container.classList.contains('sidebar-collapsed')) {
                container.classList.remove('sidebar-collapsed');
                localStorage.setItem('llma_sidebar_collapsed', 'false');
                const lb = document.getElementById('llma-split-left-btn');
                if (lb) lb.innerHTML = '&#x21DA;';
            } else if (which === 'right' && container.classList.contains('panel-collapsed')) {
                container.classList.remove('panel-collapsed');
                localStorage.setItem('llma_panel_collapsed', 'false');
                const rb = document.getElementById('llma-split-right-btn');
                if (rb) rb.innerHTML = '&#x21DB;';
            }
            dragging = true;
            bar.classList.add('dragging');
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
            e.preventDefault();
        });

        // Double-click to reset to default
        bar.addEventListener('dblclick', () => {
            if (which === 'left') {
                container.style.removeProperty('--llma-sidebar-w');
                localStorage.removeItem('llma_sidebar_w');
            } else {
                container.style.removeProperty('--llma-panel-w');
                localStorage.removeItem('llma_panel_w');
            }
        });
    };

    attach(leftBar,  'left');
    attach(rightBar, 'right');

    // Quickbutton toggle handlers
    const leftBtn  = document.getElementById('llma-split-left-btn');
    const rightBtn = document.getElementById('llma-split-right-btn');

    const applySidebarState = (collapsed) => {
        container.classList.toggle('sidebar-collapsed', collapsed);
        localStorage.setItem('llma_sidebar_collapsed', collapsed ? 'true' : 'false');
        if (leftBtn) {
            leftBtn.innerHTML = collapsed ? '&#x21DB;' : '&#x21DA;';
        }
    };

    const applyPanelState = (collapsed) => {
        container.classList.toggle('panel-collapsed', collapsed);
        localStorage.setItem('llma_panel_collapsed', collapsed ? 'true' : 'false');
        if (rightBtn) {
            rightBtn.innerHTML = collapsed ? '&#x21DA;' : '&#x21DB;';
        }
    };

    // Restore persisted collapsed states
    const sidebarCollapsed = localStorage.getItem('llma_sidebar_collapsed') === 'true';
    const panelCollapsed   = localStorage.getItem('llma_panel_collapsed')   === 'true';
    if (sidebarCollapsed) { applySidebarState(true); }
    if (panelCollapsed)   { applyPanelState(true);   }

    if (leftBtn) {
        leftBtn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            const isCollapsed = container.classList.contains('sidebar-collapsed');
            applySidebarState(!isCollapsed);
        });
    }
    if (rightBtn) {
        rightBtn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            const isCollapsed = container.classList.contains('panel-collapsed');
            applyPanelState(!isCollapsed);
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
                <button class="llma-panel-action-btn" type="button" data-action="edit">Edit</button>
                <button class="llma-panel-action-btn" type="button" data-action="switch">Switch</button>
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
                ${assistant.instructions?.companion ? '<span class="llma-mem-chip">Companion</span>' : ''}
            </div>
        </div>
        <div class="llma-panel-assets">
            <div class="llma-panel-assets-header">
                <span>Assets</span>
                <span class="llma-asset-count" id="llma-asset-count">0</span>
            </div>
            <div class="llma-asset-list" id="llma-asset-list"></div>
        </div>`;

    // Wire the panel actions (no inline handlers — CSP-clean).
    inner.querySelector('[data-action="edit"]')?.addEventListener('click', () => {
        llmaOpenSettings();
        document.querySelector('.llma-modal-tab[data-tab="assistants"]')?.click();
    });
    inner.querySelector('[data-action="switch"]')?.addEventListener('click', () => llmaShowWelcome());

    // Populate the freshly-created asset list (derived from current messages)
    if (typeof llmaRenderAssetSidebar === 'function') {
        llmaRenderAssetSidebar();
    }
}

function llmaRenderAssistantPanelEmpty() {
    const inner = document.getElementById('llma-panel-inner');
    if (inner) inner.innerHTML = '<div class="llma-panel-empty">Select an assistant to see details.</div>';
}

// Lightweight in-place update for the Messages/Tokens stat boxes.
// Called from chat.js on user send and on stream completion so the right panel
// reflects the active thread's live counts without a full re-render.
//
// Uses the exact backend token count (LLMAState.exactTokenCount) when available,
// otherwise falls back to the cheap char/4 heuristic. Exact counts are requested
// via llmaRefreshExactTotalTokens() on stream completion.
function llmaUpdatePanelStats() {
    const boxes = document.querySelectorAll('#llma-panel-inner .llma-panel-stats .llma-stat-num');
    if (!boxes || boxes.length < 2) return;
    const msgs = LLMAState.messages || [];
    boxes[0].textContent = String(msgs.length);
    const exact = LLMAState.exactTokenCount;
    if (typeof exact === 'number' && LLMAState.exactTokenCountForLen === msgs.length) {
        boxes[1].textContent = exact.toLocaleString();
    }
    else {
        boxes[1].textContent = '~' + llmaApproxTokens(msgs);
    }
}

// Fires a background request to get an exact token count for the current thread's
// full message history and patches LLMAState.exactTokenCount when it lands.
// Fire-and-forget — safe to call without awaiting. If the active LLM backend
// cannot tokenize (not loaded / wrong backend type), the server returns the same
// heuristic and this stays as an approximation.
async function llmaRefreshExactTotalTokens() {
    const msgs = LLMAState.messages || [];
    if (msgs.length === 0) {
        LLMAState.exactTokenCount = 0;
        LLMAState.exactTokenCountForLen = 0;
        llmaUpdatePanelStats();
        llmaUpdateContextBar();
        return;
    }
    // Snapshot length so we can invalidate if more messages land while we're waiting.
    const snapshotLen = msgs.length;
    const payload = { messages: msgs.map(m => ({ role: m.role, content: m.content || '' })) };
    try {
        const result = await llmaRequest('LLMAssistantCountTokens', payload);
        if (!result || result.success === false) return;
        // Only apply if the conversation hasn't moved on since we requested.
        if ((LLMAState.messages || []).length !== snapshotLen) return;
        LLMAState.exactTokenCount = Number(result.count) || 0;
        LLMAState.exactTokenCountForLen = snapshotLen;
        LLMAState.exactTokenCountIsExact = !!result.exact;
        llmaUpdatePanelStats();
        llmaUpdateContextBar();
    } catch {
        // Silent — heuristic stays.
    }
}

// -- User profile (per-user memory) --
// The profile is strictly per-user and stored server-side via UserProfileService.
// We cache it here so the welcome hero can personalize without an extra round-trip.
async function llmaLoadUserProfile() {
    try {
        const result = await llmaRequest('LLMAssistantGetUserProfile', {});
        LLMAState.userProfile = result?.profile || null;
    } catch {
        LLMAState.userProfile = null;
    }
}

// -- Personalized Welcome Hero --
// Builds the avatar + greeting + rotating tip block at the top of the welcome screen.
// Picks a random tip every call (per user preference). Greeting wording adapts to
// what we know about the user from their memory profile.
function llmaRenderPersonalizedWelcome() {
    const hero = document.getElementById('llma-welcome-hero');
    if (!hero) return;

    // Pick a featured assistant: prefer the active one, then the last-used,
    // otherwise the first available. The hero is hidden if there are zero assistants.
    const assistant = LLMAState.assistants.find(a => a.id === LLMAState.activeAssistantId)
        || LLMAState.assistants[0];
    if (!assistant) {
        hero.style.display = 'none';
        return;
    }

    const profile     = LLMAState.userProfile || {};
    const userName    = (profile.preferredName || '').trim();
    const currentWork = (profile.currentWork || '').trim();
    const asstName    = assistant.name || 'Assistant';

    // Greeting strategy:
    //  - Have name + currentWork → familiar "still working on X?" line
    //  - Have name only          → friendly welcome back
    //  - First-time user         → introductory ask-for-name
    //  - Otherwise               → neutral how-can-i-help
    let heading;
    let sub;
    if (userName && currentWork) {
        heading = `Welcome back, ${llmaEscapeHtml(userName)}.`;
        sub     = `I'm ${llmaEscapeHtml(asstName)}. Still working on <em>${llmaEscapeHtml(currentWork)}</em>?`;
    } else if (userName) {
        heading = `Welcome back, ${llmaEscapeHtml(userName)}.`;
        sub     = `I'm ${llmaEscapeHtml(asstName)}. What are we working on today?`;
    } else if (LLMAState.userProfile === null || (!profile.preferredName && !profile.bio && !(profile.facts || []).length)) {
        heading = `Hi, I'm ${llmaEscapeHtml(asstName)}.`;
        sub     = `Tell me what to call you and what you're working on — I'll remember it for next time.`;
    } else {
        heading = `Hi, I'm ${llmaEscapeHtml(asstName)}.`;
        sub     = `How can I help you today?`;
    }

    // Pick a fresh tip on every render.
    const tip = LLMA_WELCOME_TIPS[Math.floor(Math.random() * LLMA_WELCOME_TIPS.length)];

    // Avatar: real image if assigned, otherwise gradient + category icon.
    const icon = llmaCategoryIcon(assistant.icon || assistant.category || 'chat');
    const avatarHtml = assistant.avatar
        ? `<img class="llma-welcome-avatar-img" src="${llmaEscapeHtml(assistant.avatar)}" alt="${llmaEscapeHtml(asstName)}">`
        : `<div class="llma-welcome-avatar-gradient" style="background:${llmaGradientBg(assistant.color)};"><span class="llma-welcome-avatar-icon">${icon}</span></div>`;

    // Layout: avatar on the left, a chat-style speech bubble on the right (with a
    // little tail pointing back at the avatar) so the greeting visually reads as
    // the assistant *talking to* the user. The rotating tip is rendered as a
    // separate, lighter system note below the bubble.
    hero.innerHTML = `
        <div class="llma-welcome-avatar" title="${llmaEscapeHtml(asstName)}">${avatarHtml}</div>
        <div class="llma-welcome-text">
            <div class="llma-welcome-bubble">
                <div class="llma-welcome-bubble-name">${llmaEscapeHtml(asstName)}</div>
                <h1 class="llma-welcome-heading">${heading}</h1>
                <p class="llma-welcome-sub">${sub}</p>
            </div>
            <div class="llma-welcome-tip"><span class="llma-welcome-tip-icon">💡</span><span class="llma-welcome-tip-text">${tip}</span></div>
        </div>`;
    hero.style.display = '';
}

// -- Empty-chat greeting --
// Renders the personalized greeting block that sits above the input box when
// the active thread has zero messages. The block shows the active assistant's
// circular avatar, their name, and a greeting that adapts to the user's
// memory profile (preferred name + current work). Hidden automatically as
// soon as the thread has any messages.
function llmaRenderEmptyChatGreeting() {
    const greet = document.getElementById('llma-empty-greeting');
    if (!greet) return;

    if (LLMAState.messages && LLMAState.messages.length > 0) {
        greet.style.display = 'none';
        greet.innerHTML = '';
        return;
    }

    const assistant = LLMAState.assistants.find(a => a.id === LLMAState.activeAssistantId)
        || LLMAState.assistants[0];
    if (!assistant) {
        greet.style.display = 'none';
        return;
    }

    const profile     = LLMAState.userProfile || {};
    const userName    = (profile.preferredName || '').trim();
    const currentWork = (profile.currentWork || '').trim();
    const asstName    = assistant.name || 'Assistant';

    // Greeting strategy mirrors the welcome hero so the assistant feels
    // consistent across both surfaces. Description fallback gives the
    // assistant a "voice" even on first contact.
    let heading;
    let sub;
    // The name in the heading is accent-coloured (replaces the separate uppercase name label above).
    if (userName && currentWork) {
        heading = `Welcome back, <span class="llma-greeting-accent">${llmaEscapeHtml(userName)}</span>.`;
        sub     = `Still working on <em>${llmaEscapeHtml(currentWork)}</em>? I'm ready when you are.`;
    } else if (userName) {
        heading = `Hey <span class="llma-greeting-accent">${llmaEscapeHtml(userName)}</span> — what should we work on?`;
        sub     = assistant.description
            ? llmaEscapeHtml(assistant.description)
            : `I'm ${llmaEscapeHtml(asstName)}. Ask me anything.`;
    } else {
        heading = `Hi, I'm <span class="llma-greeting-accent">${llmaEscapeHtml(asstName)}</span>. How can I help?`;
        sub     = assistant.description
            ? llmaEscapeHtml(assistant.description)
            : `Tell me what to call you and what you're working on — I'll remember it next time.`;
    }

    const icon = llmaCategoryIcon(assistant.icon || assistant.category || 'chat');
    const avatarInner = assistant.avatar
        ? `<img src="${llmaEscapeHtml(assistant.avatar)}" alt="${llmaEscapeHtml(asstName)}">`
        : `<span class="llma-greet-icon">${icon}</span>`;
    const avatarStyle = assistant.avatar ? '' : ` style="background:${llmaGradientBg(assistant.color)};"`;

    greet.innerHTML = `
        <div class="llma-empty-greeting-avatar"${avatarStyle}>${avatarInner}</div>
        <h2 class="llma-empty-greeting-heading">${heading}</h2>
        <p class="llma-empty-greeting-sub">${sub}</p>`;
    greet.style.display = '';
}

// -- Welcome Screen Assistant Grid --
// Renders placeholder skeleton cards in the welcome gallery — used during the very first
// data fetch so the page never sits blank. Replaced by llmaRenderWelcomeAssistants() as soon
// as the real assistant list arrives.
function llmaRenderWelcomeSkeleton() {
    const grid = document.getElementById('llma-assistant-grid');
    if (!grid) return;
    let html = '';
    for (let i = 0; i < 4; i++) {
        html += `
            <div class="llma-asst-card skeleton" aria-hidden="true">
                <div class="llma-card-visual"></div>
                <div class="llma-card-body">
                    <div class="llma-card-name">loading</div>
                    <div class="llma-card-desc">loading description</div>
                </div>
            </div>`;
    }
    grid.innerHTML = html;
}

// Renders the welcome banner that sits between the personalized hero and the assistant grid.
// Three states, in priority order:
//   1. Hard load failure on any of (assistants, models, tools) -> red banner + Retry
//   2. No backends running (models call succeeded but list is empty) -> amber banner + link to Server > Backends
//   3. All clean -> banner hidden
// Re-runs the loads on Retry and re-renders. The "Open Server > Backends" link defers to whatever
// host hooks the user has wired up via `llma-open-backends-href` on the banner (override target).
function llmaRenderWelcomeBanner() {
    const banner = document.getElementById('llma-welcome-banner');
    if (!banner) return;
    const errs = LLMAState.loadErrors || {};
    const hardError = errs.assistants || errs.models || errs.tools;
    if (hardError) {
        const which = errs.assistants ? 'assistants' : (errs.models ? 'models' : 'tools');
        banner.className = 'llma-welcome-banner error';
        banner.innerHTML = `
            <span class="llma-banner-icon" aria-hidden="true">⚠</span>
            <span class="llma-banner-text">Couldn't load ${which}: <em>${llmaEscapeHtml(hardError)}</em></span>
            <button class="basic-button llma-banner-retry" type="button">Retry</button>`;
        banner.style.display = '';
        banner.querySelector('.llma-banner-retry')?.addEventListener('click', llmaRetryWelcomeLoads);
        return;
    }
    if (LLMAState.noBackendsRunning) {
        banner.className = 'llma-welcome-banner info';
        banner.innerHTML = `
            <span class="llma-banner-icon" aria-hidden="true">💡</span>
            <span class="llma-banner-text">No LLM backends are running. Add one in <strong>Server &rsaquo; Backends</strong> to start chatting.</span>
            <button class="basic-button llma-banner-retry" type="button">Retry</button>`;
        banner.style.display = '';
        banner.querySelector('.llma-banner-retry')?.addEventListener('click', llmaRetryWelcomeLoads);
        return;
    }
    banner.style.display = 'none';
    banner.innerHTML = '';
}

// Re-runs the three welcome-dependent loads in parallel, then re-renders. Toasts on completion
// so the user gets confirmation that Retry actually did something (silent retries feel broken).
async function llmaRetryWelcomeLoads() {
    const banner = document.getElementById('llma-welcome-banner');
    if (banner) {
        banner.classList.add('loading');
        const btn = banner.querySelector('.llma-banner-retry');
        if (btn) { btn.disabled = true; btn.textContent = 'Retrying…'; }
    }
    await Promise.all([
        llmaLoadAssistants(),
        llmaLoadModels(),
        typeof llmaLoadTools === 'function' ? llmaLoadTools() : Promise.resolve(),
    ]);
    llmaRenderWelcomeAssistants();
    const stillError = LLMAState.loadErrors.assistants || LLMAState.loadErrors.models || LLMAState.loadErrors.tools;
    if (stillError) {
        llmaShowToast('Still failing — check that the server is running.', 'error');
    } else if (LLMAState.noBackendsRunning) {
        llmaShowToast('No backends found. Add one in Server > Backends.', 'info');
    } else {
        llmaShowToast('Reloaded.', 'info');
    }
}

function llmaRenderWelcomeAssistants() {
    // Refresh the personalized hero alongside the gallery so the tip rotates
    // each time the user lands on / returns to the welcome view.
    llmaRenderPersonalizedWelcome();
    // Refresh the load-error / no-backend banner before the gallery so users see actionable
    // feedback (instead of just a blank grid) when assistants/models/tools failed to fetch.
    llmaRenderWelcomeBanner();

    const grid = document.getElementById('llma-assistant-grid');
    if (!grid) return;

    // Empty-state card: the assistants list is legitimately empty (no error) AND not still loading
    // (the skeleton placeholder cleared). Distinct from the load-error banner above so users know
    // there's nothing to pick — the path forward is to create one.
    const isEmpty = (LLMAState.assistants?.length ?? 0) === 0;
    const hasLoadError = LLMAState.loadErrors?.assistants;
    if (isEmpty && !hasLoadError) {
        grid.innerHTML = `
            <div class="llma-asst-empty">
                <div class="llma-asst-empty-icon" aria-hidden="true">✨</div>
                <h3 class="llma-asst-empty-title">No assistants yet</h3>
                <p class="llma-asst-empty-desc">Create your first assistant to start chatting. You can give it a name, a system prompt, and a set of tools.</p>
                <button class="basic-button llma-asst-empty-cta" type="button">Create your first assistant</button>
            </div>`;
        grid.querySelector('.llma-asst-empty-cta')?.addEventListener('click', () => {
            llmaOpenSettings();
            setTimeout(() => {
                document.querySelector('.llma-modal-tab[data-tab="assistants"]')?.click();
                llmaOpenCreateEditor();
            }, 100);
        });
        return;
    }

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
            llmaStartChatWithAssistant(asstId);
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
            // Full factory reset: hits the server's LLMAssistantResetSettings with
            // scope=shared so the persisted shared blob (assistants, tools, instructions,
            // params) is rebuilt from C#-side DefaultSettings. The local scalar fallback
            // alone is not enough — the assistants dict lives server-side.
            const sharedReset = !!LLMAState.canWriteShared;
            const msg = sharedReset
                ? 'Reset ALL shared settings (assistants, tools, instructions) to factory defaults? This affects every user.'
                : 'Reset your personal LLM Assistant settings to defaults?';
            if (!confirm(msg)) return;
            try {
                await llmaRequest('LLMAssistantResetSettings', sharedReset ? { scope: 'shared' } : {});
                // Reload everything so the newly-defaulted Swarmie (and its avatar) show up.
                await llmaLoadSettings();
                await llmaLoadAssistants();
                llmaWriteSettingsToModal();
                llmaRenderAssistantList?.();
                if (LLMAState.activeAssistantId) {
                    llmaRenderAssistantPanel(LLMAState.activeAssistantId);
                }
                llmaRenderWelcomeAssistants?.();
                llmaShowToast('Settings reset to defaults', 'success');
            } catch (e) {
                llmaShowToast('Reset failed: ' + (e?.message || 'unknown error'), 'error');
            }
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
            if (tab.dataset.tab === 'tools') llmaLoadTools();
        });
    });

    // Sync range sliders
    const rangeMap = [
        { range: 'llma-s-temperature',        val: 'llma-s-temperature-val'        },
        { range: 'llma-s-top-p',              val: 'llma-s-top-p-val'              },
        { range: 'llma-s-companion-opacity',  val: 'llma-s-companion-opacity-val'  },
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
    // Starter template dropdown — populated lazily on first use, cloned into the editor on pick.
    const tmplSelect = document.getElementById('llma-template-select');
    if (tmplSelect) {
        tmplSelect.addEventListener('focus', llmaPopulateTemplatesDropdown, { once: true });
        tmplSelect.addEventListener('change', () => {
            const id = tmplSelect.value;
            if (!id) return;
            llmaOpenEditorFromTemplate(id);
            tmplSelect.value = '';
        });
    }
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
    llmaSetupToolsTab();
}

function llmaOpenSettings() {
    const overlay = document.getElementById('llma-settings-overlay');
    if (!overlay) return;
    llmaWriteSettingsToModal();
    llmaRenderAssistantList();
    overlay.style.display = '';
    llmaInstallFocusTrap(overlay);
    setTimeout(() => document.getElementById('llma-settings-close')?.focus(), LLMA_CONSTANTS.MODAL_FOCUS_DELAY_MS);
}

function llmaCloseSettings() {
    const overlay = document.getElementById('llma-settings-overlay');
    if (overlay) overlay.style.display = 'none';
    llmaRemoveFocusTrap(overlay);
    document.getElementById('llma-asst-editor').style.display = 'none';
}

// -- Focus trap --
// Keyboard accessibility: while a modal is open, Tab cycles between focusable elements
// inside it. Without this, Tab walks into the underlying tab UI and the user loses track.
// Pure DOM helper — installs / removes a single listener stored on the element via dataset.
const LLMA_FOCUSABLE_SELECTOR = 'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]):not([type="hidden"]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';

function llmaInstallFocusTrap(modal) {
    if (!modal || modal._llmaFocusTrap) return;
    const handler = (e) => {
        if (e.key !== 'Tab') return;
        const focusable = Array.from(modal.querySelectorAll(LLMA_FOCUSABLE_SELECTOR))
            .filter(el => el.offsetParent !== null);
        if (focusable.length === 0) return;
        const first = focusable[0];
        const last  = focusable[focusable.length - 1];
        if (e.shiftKey && document.activeElement === first) {
            e.preventDefault();
            last.focus();
        } else if (!e.shiftKey && document.activeElement === last) {
            e.preventDefault();
            first.focus();
        }
    };
    modal.addEventListener('keydown', handler);
    modal._llmaFocusTrap = handler;
}

function llmaRemoveFocusTrap(modal) {
    if (!modal || !modal._llmaFocusTrap) return;
    modal.removeEventListener('keydown', modal._llmaFocusTrap);
    delete modal._llmaFocusTrap;
}

function llmaReadSettingsFromModal() {
    const g = LLMAState.settings.parameters || {};
    g.temperature        = parseFloat(document.getElementById('llma-s-temperature')?.value)  || 0.8;
    g.maxTokens          = parseInt(document.getElementById('llma-s-max-tokens')?.value, 10) || 2048;
    g.topP               = parseFloat(document.getElementById('llma-s-top-p')?.value)        || 0.9;
    const seedRaw        = parseInt(document.getElementById('llma-s-seed')?.value, 10);
    g.seed               = Number.isFinite(seedRaw) ? seedRaw : -1;
    g.maxContextMessages = parseInt(document.getElementById('llma-s-context')?.value, 10)    || 0;
    g.stream             = document.getElementById('llma-s-stream')?.checked ?? true;
    LLMAState.settings.parameters = g;

    const u = LLMAState.settings.ui || {};
    u.markdownEnabled = document.getElementById('llma-s-markdown')?.checked ?? true;
    u.enterToSend     = document.getElementById('llma-s-enter-send')?.checked ?? true;
    u.showTokens      = document.getElementById('llma-s-show-tokens')?.checked ?? true;
    LLMAState.settings.ui = u;

    // Companion settings — preserve unread fields (offsetX/Y, expanded) so dragging the
    // overlay on screen and saving from the modal don't fight each other.
    const c = LLMAState.settings.companion || {};
    c.enabled   = document.getElementById('llma-s-companion-enabled')?.checked ?? false;
    c.personaId = document.getElementById('llma-s-companion-persona')?.value ?? '';
    c.corner    = document.getElementById('llma-s-companion-corner')?.value ?? 'bottom-right';
    c.opacity   = parseFloat(document.getElementById('llma-s-companion-opacity')?.value) || 0.95;
    c.buttons = c.buttons || {};
    c.buttons.ask                 = document.getElementById('llma-s-companion-btn-ask')?.checked ?? true;
    c.buttons.critique_last_image = document.getElementById('llma-s-companion-btn-critique')?.checked ?? true;
    c.buttons.help_with_prompt    = document.getElementById('llma-s-companion-btn-prompt')?.checked ?? true;
    c.buttons.suggest_preset      = document.getElementById('llma-s-companion-btn-preset')?.checked ?? true;
    c.buttons.explain_feature     = document.getElementById('llma-s-companion-btn-explain')?.checked ?? true;
    c.buttons.daily_tip           = document.getElementById('llma-s-companion-btn-tip')?.checked ?? true;
    c.chatter = c.chatter || {};
    c.chatter.quietMode      = document.getElementById('llma-s-companion-chat-quiet')?.checked ?? false;
    c.chatter.greeting       = document.getElementById('llma-s-companion-chat-greeting')?.checked ?? true;
    c.chatter.reactions      = document.getElementById('llma-s-companion-chat-reactions')?.checked ?? false;
    c.chatter.idle           = document.getElementById('llma-s-companion-chat-idle')?.checked ?? false;
    c.chatter.idleMinutes    = Math.max(2, parseInt(document.getElementById('llma-s-companion-chat-idle-min')?.value, 10) || 8);
    c.chatter.maxPerSession  = Math.max(0, parseInt(document.getElementById('llma-s-companion-chat-cap')?.value, 10) || 5);
    c.chatter.quietHours     = document.getElementById('llma-s-companion-chat-qh')?.checked ?? false;
    c.chatter.quietStart     = Math.max(0, Math.min(23, parseInt(document.getElementById('llma-s-companion-chat-qh-start')?.value, 10) || 22));
    c.chatter.quietEnd       = Math.max(0, Math.min(23, parseInt(document.getElementById('llma-s-companion-chat-qh-end')?.value, 10) || 8));
    LLMAState.settings.companion = c;
}

function llmaWriteSettingsToModal() {
    const g = LLMAState.settings?.parameters || LLMA_DEFAULT_SETTINGS.parameters;
    llmaSetEl('llma-s-temperature',         g.temperature        ?? 0.8);
    llmaSetEl('llma-s-temperature-val',     g.temperature        ?? 0.8, 'text');
    llmaSetEl('llma-s-max-tokens',          g.maxTokens          ?? 2048);
    llmaSetEl('llma-s-top-p',               g.topP               ?? 0.9);
    llmaSetEl('llma-s-top-p-val',           g.topP               ?? 0.9, 'text');
    llmaSetEl('llma-s-seed',                g.seed               ?? -1);
    llmaSetEl('llma-s-context',             g.maxContextMessages ?? 0);
    llmaSetElChecked('llma-s-stream',       g.stream             !== false);

    const u = LLMAState.settings?.ui || LLMA_DEFAULT_SETTINGS.ui;
    llmaSetElChecked('llma-s-markdown',     u.markdownEnabled !== false);
    llmaSetElChecked('llma-s-enter-send',   u.enterToSend     !== false);
    llmaSetElChecked('llma-s-show-tokens',  u.showTokens      !== false);

    // Companion section
    const c = LLMAState.settings?.companion || LLMA_DEFAULT_SETTINGS.companion;
    llmaSetElChecked('llma-s-companion-enabled', !!c.enabled);
    llmaSetEl('llma-s-companion-corner', c.corner ?? 'bottom-right');
    llmaSetEl('llma-s-companion-opacity', c.opacity ?? 0.95);
    llmaSetEl('llma-s-companion-opacity-val', c.opacity ?? 0.95, 'text');
    llmaPopulateCompanionPersonaSelect(c.personaId ?? '');
    llmaSetElChecked('llma-s-companion-btn-ask',      c.buttons?.ask                 !== false);
    llmaSetElChecked('llma-s-companion-btn-critique', c.buttons?.critique_last_image !== false);
    llmaSetElChecked('llma-s-companion-btn-prompt',   c.buttons?.help_with_prompt    !== false);
    llmaSetElChecked('llma-s-companion-btn-preset',   c.buttons?.suggest_preset      !== false);
    llmaSetElChecked('llma-s-companion-btn-explain',  c.buttons?.explain_feature     !== false);
    llmaSetElChecked('llma-s-companion-btn-tip',      c.buttons?.daily_tip           !== false);

    const ch = c.chatter || LLMA_DEFAULT_SETTINGS.companion.chatter;
    llmaSetElChecked('llma-s-companion-chat-quiet',     !!ch.quietMode);
    llmaSetElChecked('llma-s-companion-chat-greeting',  ch.greeting !== false);
    llmaSetElChecked('llma-s-companion-chat-reactions', !!ch.reactions);
    llmaSetElChecked('llma-s-companion-chat-idle',      !!ch.idle);
    llmaSetEl('llma-s-companion-chat-idle-min', ch.idleMinutes ?? 8);
    llmaSetEl('llma-s-companion-chat-cap',      ch.maxPerSession ?? 5);
    llmaSetElChecked('llma-s-companion-chat-qh',         !!ch.quietHours);
    llmaSetEl('llma-s-companion-chat-qh-start', ch.quietStart ?? 22);
    llmaSetEl('llma-s-companion-chat-qh-end',   ch.quietEnd   ?? 8);
}

// Populates the Companion persona dropdown from the loaded assistants list. Selects
// the persisted personaId, or "" (= follow active assistant) when none is saved or the
// saved id is no longer valid (e.g. assistant was deleted).
function llmaPopulateCompanionPersonaSelect(selectedId) {
    const sel = document.getElementById('llma-s-companion-persona');
    if (!sel) return;
    const opts = ['<option value="">(Use my active assistant)</option>'];
    for (const a of (LLMAState.assistants || [])) {
        const id = llmaEscapeHtml(a.id);
        const name = llmaEscapeHtml(a.name || a.id);
        opts.push(`<option value="${id}">${name}</option>`);
    }
    sel.innerHTML = opts.join('');
    // Only restore the saved value if it still matches one of the options; otherwise blank.
    const exists = !selectedId || (LLMAState.assistants || []).some(a => a.id === selectedId);
    sel.value = exists ? (selectedId || '') : '';
}

function llmaApplySettingsToState() {
    LLMAState.markdownEnabled = LLMAState.settings?.ui?.markdownEnabled !== false;
    LLMAState.enterToSend     = LLMAState.settings?.ui?.enterToSend     !== false;
    LLMAState.showTokens      = LLMAState.settings?.ui?.showTokens      !== false;
    llmaUpdateContextBar();
    // Companion overlay lives outside the LLM Assistant tab — re-apply visibility / position
    // / opacity now that settings have been saved.
    if (typeof llmaCompanionApplySettings === 'function') {
        llmaCompanionApplySettings();
    }
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
        const scopeBadge = a._scope === 'shared'
            ? ' <span class="llma-scope-badge llma-scope-shared" title="Shared — visible to all users on this instance">shared</span>'
            : (a._scope === 'personal' ? ' <span class="llma-scope-badge llma-scope-personal" title="Personal — only visible to you">personal</span>' : '');
        html += `
            <div class="llma-asst-list-item" data-asst-id="${llmaEscapeHtml(a.id)}">
                <div class="llma-list-avatar" style="background:${llmaGradientBg(a.color)};">
                    ${a.avatar ? `<img src="${llmaEscapeHtml(a.avatar)}">` : icon}
                </div>
                <div class="llma-list-info">
                    <div class="llma-list-name">${llmaEscapeHtml(a.name)}${a.isBuiltIn ? ' <span class="llma-builtin-badge">(built-in)</span>' : ''}${scopeBadge}</div>
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

// Lazy load: only ask the server for templates the first time the dropdown is interacted with.
let _llmaTemplateCache = null;
async function llmaPopulateTemplatesDropdown() {
    const sel = document.getElementById('llma-template-select');
    if (!sel) return;
    try {
        if (!_llmaTemplateCache) {
            const result = await llmaRequest('LLMAssistantGetStarterTemplates', {});
            _llmaTemplateCache = Array.isArray(result?.templates) ? result.templates : [];
        }
        // Replace options (preserving the placeholder).
        sel.innerHTML = '<option value="">Clone from template…</option>';
        for (const tmpl of _llmaTemplateCache) {
            const opt = document.createElement('option');
            opt.value = tmpl.id;
            opt.textContent = tmpl.name + (tmpl.description ? ' — ' + tmpl.description : '');
            sel.appendChild(opt);
        }
    } catch {
        // Silent — the +Create button still works.
    }
}

// Clones the template into a fresh editor with a new id so the user can tweak before save.
function llmaOpenEditorFromTemplate(templateId) {
    if (!_llmaTemplateCache) return;
    const tmpl = _llmaTemplateCache.find(t => t.id === templateId);
    if (!tmpl) return;
    // Build a draft assistant from the template. Strip the template's id so we don't collide with
    // anything already saved; llmaShowAssistantEditor will assign a fresh one.
    const draft = JSON.parse(JSON.stringify(tmpl));
    delete draft.id;
    delete draft._scope;
    draft.isBuiltIn = false;
    llmaShowAssistantEditor(draft);
}

function llmaShowAssistantEditor(assistant) {
    const editor = document.getElementById('llma-asst-editor');
    if (!editor) return;
    editor.style.display = '';
    // Pre-assign an id for new assistants so avatar upload can target a stable file before save.
    llmaEditingAsstId = assistant ? assistant.id : ('assistant-' + llmaGenerateId());

    const title = document.getElementById('llma-editor-title');
    if (title) title.textContent = assistant ? `Edit: ${assistant.name}` : 'New Assistant';

    const deleteBtn = document.getElementById('llma-asst-delete');
    if (deleteBtn) deleteBtn.style.display = (assistant && !assistant.isBuiltIn) ? '' : 'none';

    // Scope toggle: only admins (canWriteShared) see it. When editing an existing assistant,
    // pre-fill from its current scope so they can re-save to the same layer.
    const scopeWrap = document.getElementById('llma-asst-scope-toggle');
    const scopeCheckbox = document.getElementById('llma-asst-scope-shared');
    if (scopeWrap) {
        scopeWrap.style.display = LLMAState.canWriteShared ? '' : 'none';
    }
    if (scopeCheckbox) {
        scopeCheckbox.checked = assistant?._scope === 'shared';
    }

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
        delete preview.dataset.avatarData;
        if (assistant?.avatar) {
            preview.innerHTML = `<img src="${llmaEscapeHtml(assistant.avatar)}">`;
            preview.dataset.avatarData = assistant.avatar;
        } else {
            preview.innerHTML = '';
            preview.style.background = llmaGradientBg(assistant?.color);
            preview.textContent = llmaCategoryIcon(assistant?.icon || assistant?.category || 'chat');
        }
    }

    // Instructions: parse into editing state. Each instruction can be a plain string (legacy)
    // or { default, variants[] } (new shape with per-model overrides).
    LLMAState._editingInstructions = llmaParseInstructionsForEditor(assistant?.instructions);
    llmaActiveInstrTab = 'chat';
    llmaRenderInstructionTab('chat');
    document.querySelectorAll('.llma-instr-tab').forEach(t => {
        t.classList.toggle('active', t.dataset.mode === 'chat');
    });

    // Extends dropdown — list every other assistant the user can see as a possible parent
    llmaPopulateExtendsDropdown(assistant);

    // Parameters
    llmaSetEl('llma-asst-temperature', assistant?.parameters?.temperature ?? '');
    llmaSetEl('llma-asst-max-tokens',  assistant?.parameters?.maxTokens ?? '');
    llmaSetEl('llma-asst-top-p',       assistant?.parameters?.topP ?? '');

    // Enabled tools checklist (and the per-assistant tool config blocks below it)
    llmaRenderAssistantToolsChecklist(assistant?.enabledToolIds || []);
    llmaRenderAssistantToolConfig(assistant);
}

// Normalizes any instruction value into the editing-state shape.
// Accepts plain string (legacy), { default, variants[] } (new shape), or null/undefined.
function llmaParseInstructionsForEditor(rawInstructions) {
    const out = {};
    const src = rawInstructions || {};
    for (const key of LLMA_INSTRUCTION_KEYS) {
        const node = src[key];
        if (typeof node === 'string') {
            out[key] = { default: node, variants: [] };
        } else if (node && typeof node === 'object') {
            out[key] = {
                default: typeof node.default === 'string' ? node.default : '',
                variants: Array.isArray(node.variants) ? node.variants.map(v => ({
                    matchKind: v.matchKind || 'glob',
                    match:     v.match || '',
                    text:      v.text || ''
                })) : []
            };
        } else {
            out[key] = { default: '', variants: [] };
        }
    }
    return out;
}

// Populates the "Extends" dropdown with every assistant except the one being edited
// (preventing self-parenting). Cycles further down the chain are caught by the resolver.
function llmaPopulateExtendsDropdown(assistant) {
    const sel = document.getElementById('llma-asst-extends');
    if (!sel) return;
    const current = assistant?.extends || '';
    sel.innerHTML = '<option value="">(no parent — standalone)</option>';
    for (const a of LLMAState.assistants) {
        if (assistant && a.id === assistant.id) continue;
        const opt = document.createElement('option');
        opt.value = a.id;
        opt.textContent = a.name + (a._scope === 'shared' ? ' (shared)' : '');
        if (a.id === current) opt.selected = true;
        sel.appendChild(opt);
    }
}

async function llmaSaveAssistantFromEditor() {
    const name = document.getElementById('llma-asst-name')?.value?.trim();
    if (!name) { llmaShowToast('Name is required', 'error'); return; }

    // Sync the currently-visible instruction tab into the editing state, then serialize all tabs.
    llmaSyncCurrentInstructionTabToState();
    const extendsId = document.getElementById('llma-asst-extends')?.value?.trim() || '';

    const assistant = {
        id:          llmaEditingAsstId || null,
        name:        name,
        description: document.getElementById('llma-asst-desc')?.value?.trim() || '',
        icon:        document.getElementById('llma-asst-category')?.value || 'chat',
        color:       document.getElementById('llma-asst-color')?.value || '',
        instructions: llmaSerializeInstructionsForSave(),
        parameters:   {},
    };
    if (extendsId) assistant.extends = extendsId;

    const temp = document.getElementById('llma-asst-temperature');
    const maxTok = document.getElementById('llma-asst-max-tokens');
    const topP = document.getElementById('llma-asst-top-p');
    if (temp?.value) assistant.parameters.temperature = parseFloat(temp.value);
    if (maxTok?.value) assistant.parameters.maxTokens = parseInt(maxTok.value, 10);
    if (topP?.value) assistant.parameters.topP = parseFloat(topP.value);

    assistant.enabledToolIds = llmaReadAssistantEnabledToolIds();
    const toolConfig = llmaSerializeAssistantToolConfig();
    if (toolConfig) assistant.toolConfig = toolConfig;

    const avatarPreview = document.getElementById('llma-avatar-preview');
    const avatarData = avatarPreview?.dataset.avatarData;
    if (avatarData) {
        // Only persist URL-shaped avatars — never inline data: URIs (those bloat the assistant
        // blob and were already uploaded to a real file by the file-pick handler).
        if (avatarData.startsWith('data:')) {
            llmaShowToast('Avatar upload didn\'t complete — saved without avatar.', 'warning');
        } else {
            assistant.avatar = avatarData;
        }
    }

    // Scope: only send "shared" if the admin explicitly opted in AND has the permission.
    const scopeCheckbox = document.getElementById('llma-asst-scope-shared');
    const wantsShared = !!(scopeCheckbox?.checked && LLMAState.canWriteShared);
    const scope = wantsShared ? 'shared' : 'personal';

    try {
        const result = await llmaRequest('LLMAssistantSaveAssistant', { assistant, scope });
        if (result && result.success === false) {
            llmaShowToast(result.error || 'Failed to save assistant', 'error');
            return;
        }
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
    // Pass scope so an admin's delete of a shared assistant goes to the shared layer,
    // and a personal delete goes to the personal layer.
    const assistant = LLMAState.assistants.find(a => a.id === id);
    const scope = assistant?._scope || undefined;
    try {
        const result = await llmaRequest('LLMAssistantDeleteAssistant', { assistantId: id, scope });
        if (result && result.success === false) {
            llmaShowToast(result.error || 'Failed to delete assistant', 'error');
            return;
        }
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
// Picks a file → reads as data URI for preview → uploads to the per-user OutputDirectory
// via LLMAssistantUploadAssistantAvatar → stores the returned URL on the preview's dataset.
// We upload eagerly (on file pick, not on save) so the assistant JSON never carries base64 —
// it carries a URL. The /Output/{*Path} route already enforces per-user auth.
function llmaSetupAvatarUpload() {
    const uploadBtn = document.getElementById('llma-avatar-upload-btn');
    const fileInput = document.getElementById('llma-avatar-file');
    if (uploadBtn && fileInput) {
        uploadBtn.addEventListener('click', () => fileInput.click());
        fileInput.addEventListener('change', async () => {
            if (!fileInput.files?.length) return;
            const file = fileInput.files[0];
            const dataUrl = await llmaFileToBase64(file);
            const preview = document.getElementById('llma-avatar-preview');
            if (preview) {
                preview.innerHTML = `<img src="${dataUrl}">`;
                preview.dataset.avatarData = dataUrl; // shown immediately while upload runs
            }
            fileInput.value = '';
            try {
                const result = await llmaRequest('LLMAssistantUploadAssistantAvatar', {
                    assistantId: llmaEditingAsstId,
                    imageData: dataUrl
                });
                if (!result?.success || !result.url) {
                    llmaShowToast(result?.error || 'Avatar upload failed', 'error');
                    return;
                }
                if (preview) {
                    // Swap the preview to the served URL so we know the upload landed.
                    preview.innerHTML = `<img src="${llmaEscapeHtml(result.url)}">`;
                    preview.dataset.avatarData = result.url;
                }
            } catch (ex) {
                llmaShowToast('Avatar upload failed', 'error');
            }
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
    document.getElementById('llma-add-variant')?.addEventListener('click', () => {
        llmaSyncCurrentInstructionTabToState();
        const state = llmaGetEditingInstructionState(llmaActiveInstrTab);
        state.variants.push({ matchKind: 'glob', match: '', text: '' });
        llmaRenderInstructionVariants(llmaActiveInstrTab);
    });
    // Test button: toggles the inline preview panel; Run actually fires the LLM call.
    document.getElementById('llma-instr-test-btn')?.addEventListener('click', () => {
        const panel = document.getElementById('llma-instr-test-panel');
        if (panel) panel.style.display = panel.style.display === 'none' ? '' : 'none';
        const input = document.getElementById('llma-instr-test-input');
        if (input && panel?.style.display === '') input.focus();
    });
    document.getElementById('llma-instr-test-run')?.addEventListener('click', llmaRunInstructionTest);
    document.getElementById('llma-instr-test-input')?.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            llmaRunInstructionTest();
        }
    });
}

// Runs an unsaved instruction's Default text against the LLM with the user-supplied sample
// input. No persistence, no thread — pure preview. Picks up the assistant's name from the
// editor so {{assistantName}} substitutes correctly.
async function llmaRunInstructionTest() {
    const area = document.getElementById('llma-instr-area');
    const sample = document.getElementById('llma-instr-test-input');
    const result = document.getElementById('llma-instr-test-result');
    if (!area || !sample || !result) return;
    const instructionText = area.value;
    const sampleInput = (sample.value || '').trim();
    if (!instructionText) { llmaShowToast('Default instruction is empty', 'info'); return; }
    if (!sampleInput) { llmaShowToast('Enter a sample message first', 'info'); return; }
    result.textContent = 'Running…';
    try {
        const res = await llmaRequest('LLMAssistantTestInstruction', {
            instructionText,
            sampleInput,
            model: LLMAState.currentModel || '',
            assistantName: document.getElementById('llma-asst-name')?.value || ''
        });
        if (res?.success) {
            result.textContent = res.response || '(empty response)';
        } else {
            result.textContent = 'Error: ' + (res?.error || 'unknown');
        }
    } catch (ex) {
        result.textContent = 'Error: ' + (ex?.message || String(ex));
    }
}

function llmaGetEditingInstructionState(mode) {
    LLMAState._editingInstructions = LLMAState._editingInstructions || {};
    LLMAState._editingInstructions[mode] = LLMAState._editingInstructions[mode] || { default: '', variants: [] };
    return LLMAState._editingInstructions[mode];
}

function llmaSwitchInstrTab(mode) {
    // Save the currently-edited tab into state before switching.
    llmaSyncCurrentInstructionTabToState();
    llmaActiveInstrTab = mode;
    document.querySelectorAll('.llma-instr-tab').forEach(t => {
        t.classList.toggle('active', t.dataset.mode === mode);
    });
    llmaRenderInstructionTab(mode);
}

// Pulls the editor controls' current values into LLMAState._editingInstructions[currentMode].
function llmaSyncCurrentInstructionTabToState() {
    const mode = llmaActiveInstrTab;
    if (!mode) return;
    const state = llmaGetEditingInstructionState(mode);
    const area = document.getElementById('llma-instr-area');
    if (area) state.default = area.value;
    const rows = document.querySelectorAll('#llma-instr-variants .llma-variant-row');
    state.variants = Array.from(rows).map(row => ({
        matchKind: row.querySelector('.llma-variant-kind')?.value || 'glob',
        match:     (row.querySelector('.llma-variant-pattern')?.value || '').trim(),
        text:      row.querySelector('.llma-variant-text')?.value || ''
    }));
}

// Renders the editor controls from LLMAState._editingInstructions[mode].
function llmaRenderInstructionTab(mode) {
    const state = llmaGetEditingInstructionState(mode);
    const area = document.getElementById('llma-instr-area');
    if (area) area.value = state.default || '';
    llmaRenderInstructionVariants(mode);
}

function llmaRenderInstructionVariants(mode) {
    const container = document.getElementById('llma-instr-variants');
    if (!container) return;
    container.innerHTML = '';
    const state = llmaGetEditingInstructionState(mode);
    state.variants.forEach((variant, idx) => {
        const row = document.createElement('div');
        row.className = 'llma-variant-row';
        row.innerHTML = `
            <div class="llma-variant-row-head">
                <select class="llma-variant-kind" title="How to interpret the pattern">
                    <option value="exact">Exact</option>
                    <option value="family">Family</option>
                    <option value="provider">Provider</option>
                    <option value="tag">Tag</option>
                    <option value="glob">Glob</option>
                </select>
                <input type="text" class="llma-variant-pattern" placeholder="claude-* | family:llama | provider:anthropic" autocomplete="off">
                <button type="button" class="llma-variant-remove" title="Remove variant">&times;</button>
            </div>
            <textarea class="llma-variant-text" rows="3" placeholder="System prompt used for this matcher (overrides Default when matched)..."></textarea>
        `;
        row.querySelector('.llma-variant-kind').value = variant.matchKind || 'glob';
        row.querySelector('.llma-variant-pattern').value = variant.match || '';
        row.querySelector('.llma-variant-text').value = variant.text || '';
        row.querySelector('.llma-variant-remove').addEventListener('click', () => {
            llmaSyncCurrentInstructionTabToState();
            llmaGetEditingInstructionState(mode).variants.splice(idx, 1);
            llmaRenderInstructionVariants(mode);
        });
        container.appendChild(row);
    });
}

// Builds the assistant.instructions JSON to ship to the server.
// Empty defaults with no variants are dropped so we don't bloat the saved blob.
function llmaSerializeInstructionsForSave() {
    const out = {};
    for (const key of LLMA_INSTRUCTION_KEYS) {
        const state = llmaGetEditingInstructionState(key);
        const variants = (state.variants || [])
            .filter(v => v.match || v.text)
            .map(v => ({
                matchKind: v.matchKind || 'glob',
                match:     v.match || '',
                text:      v.text || ''
            }));
        const defaultText = state.default || '';
        if (defaultText || variants.length > 0) {
            out[key] = { default: defaultText, variants };
        }
    }
    return out;
}

// -- Keyboard Shortcuts --
// Returns true when the LLM Assistant tab is the focused part of the page. We don't have a
// reliable "is the tab active" signal from SwarmUI's tab system, so we approximate: the
// container exists, is visible, and either has focus inside it or the user isn't focused
// anywhere meaningful. Used to gate global shortcuts so they don't fire on other tabs.
function llmaIsTabActive() {
    const c = document.getElementById('llma-container');
    if (!c) return false;
    // The tab pane is hidden via display:none in SwarmUI's tab system. offsetParent is null
    // for display:none elements (and a few edge cases like position:fixed root, which we don't use).
    if (c.offsetParent === null) return false;
    return true;
}

// Modifier key that matches the user's platform — Cmd on Mac, Ctrl elsewhere. We honor both
// so cross-platform documentation can just say "Cmd/Ctrl" and either works for everyone.
function llmaModKey(e) {
    return e.ctrlKey || e.metaKey;
}

function llmaSetupKeyboardShortcuts() {
    document.addEventListener('keydown', (e) => {
        if (!llmaIsTabActive()) return;
        const tag = document.activeElement?.tagName;
        const inEditable = tag === 'INPUT' || tag === 'TEXTAREA' || document.activeElement?.isContentEditable;

        // Cmd/Ctrl + K → focus the thread search. Works even when an input has focus elsewhere
        // (matches the slash-command-bar convention from VS Code / Slack / GitHub).
        if (llmaModKey(e) && (e.key === 'k' || e.key === 'K') && !e.shiftKey && !e.altKey) {
            const search = document.getElementById('llma-thread-search');
            if (search) {
                e.preventDefault();
                search.focus();
                search.select();
                return;
            }
        }

        // Cmd/Ctrl + Shift + F → open the in-thread find bar (browser's Cmd+F stays untouched).
        if (llmaModKey(e) && e.shiftKey && (e.key === 'F' || e.key === 'f')) {
            if (LLMAState.activeThreadId && typeof llmaOpenFindBar === 'function') {
                e.preventDefault();
                llmaOpenFindBar();
                return;
            }
        }

        // Cmd/Ctrl + N → new thread with the active assistant. Skip when typing so users can still
        // type the letter 'n' in messages with Ctrl held for browser shortcuts they expect.
        if (llmaModKey(e) && (e.key === 'n' || e.key === 'N') && !e.shiftKey && !e.altKey) {
            if (inEditable) return;
            e.preventDefault();
            if (LLMAState.activeAssistantId && LLMAState.assistants.some(a => a.id === LLMAState.activeAssistantId)) {
                llmaStartChatWithAssistant(LLMAState.activeAssistantId, { persist: false });
            } else {
                llmaShowWelcome();
            }
            return;
        }

        // Up/Down arrows in the thread list move the highlighted item and (on Enter) switch threads.
        // Only intercepted while focus is on a thread row — otherwise arrows are free for textareas.
        if ((e.key === 'ArrowUp' || e.key === 'ArrowDown') && document.activeElement?.classList?.contains('llma-thread-item')) {
            e.preventDefault();
            const items = Array.from(document.querySelectorAll('.llma-thread-item'));
            const idx = items.indexOf(document.activeElement);
            const next = e.key === 'ArrowDown' ? Math.min(items.length - 1, idx + 1) : Math.max(0, idx - 1);
            items[next]?.focus();
            return;
        }

        if (e.key === 'Escape') {
            llmaCloseAllPopovers();
            const assetOverlay = document.getElementById('llma-asset-overlay');
            if (assetOverlay && assetOverlay.style.display !== 'none') {
                if (typeof llmaCloseAssetViewer === 'function') llmaCloseAssetViewer();
                return;
            }
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
    const popovers = ['llma-params-popover', 'llma-export-menu'];
    for (const id of popovers) {
        const el = document.getElementById(id);
        if (el && el !== except) el.style.display = 'none';
    }
    // The model menu is a class-toggled popover (not display:none), so close it separately.
    const modelAnchor = document.getElementById('llma-model-anchor');
    if (modelAnchor && modelAnchor !== except) modelAnchor.classList.remove('llma-model-open');
    document.querySelectorAll('.llma-icon-btn.active').forEach(b => b.classList.remove('active'));
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

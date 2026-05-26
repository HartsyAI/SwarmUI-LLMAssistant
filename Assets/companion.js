/* ================================================================
   LLM Assistant - companion.js
   Floating in-page overlay (Clippy-style helper).

   Lives outside the LLM Assistant tab — attaches to document.body
   so it stays visible across all SwarmUI tabs. Opt-in (default OFF)
   via Settings > Companion.

   Phase 1 surface:
     - Compact / expanded states (portrait + buttons + bubble).
     - Snap-corner positioning with free-drag offset.
     - Three quick actions: Ask anything / Critique my last image /
       Help with my prompt.
   Streams its replies through the existing LLMAssistantSendMessageWS
   endpoint with instructionId="companion".
   ================================================================ */

'use strict';

// Throttle for offset persistence so dragging doesn't spam SaveSettings.
const LLMA_COMPANION_SAVE_THROTTLE_MS = 600;

// Strips <tool_call> / <tool_result> wrappers from raw model output for the
// speech bubble. Tool events render as a tiny inline pill instead.
const LLMA_COMPANION_TOOL_TAG_RE = /<tool_call>[\s\S]*?(<\/tool_call>|$)|<tool_result\b[^>]*>[\s\S]*?(<\/tool_result>|$)/g;

function llmaCompanionState() {
    if (!LLMAState.companion) {
        LLMAState.companion = {
            threadId:    null,   // rolling per-session companion thread; created on first send
            isStreaming: false,
            socket:      null,
            lastImage:   null,   // cached payload from LLMAssistantGetCompanionContext
            saveTimer:   null,
            booted:      false,
        };
    }
    return LLMAState.companion;
}

// Look up the assistant the companion should impersonate. Honors the per-user
// personaId override; falls back to the active assistant id; falls back to the
// first assistant in the list. Returns null only if no assistants exist.
function llmaCompanionResolvePersona() {
    const cfg = LLMAState.settings?.companion || {};
    const list = LLMAState.assistants || [];
    if (!list.length) return null;
    const explicit = cfg.personaId && list.find(a => a.id === cfg.personaId);
    if (explicit) return explicit;
    const active = LLMAState.activeAssistantId && list.find(a => a.id === LLMAState.activeAssistantId);
    if (active) return active;
    return list[0];
}

// -- DOM creation -------------------------------------------------

function llmaCompanionRender() {
    if (document.getElementById('llma-companion-root')) return;
    const root = document.createElement('div');
    root.id = 'llma-companion-root';
    root.className = 'llma-companion';
    root.style.display = 'none';
    root.innerHTML = `
        <div class="llma-companion-portrait-wrap" id="llma-companion-portrait-wrap" title="Click to expand / collapse">
            <img class="llma-companion-portrait" id="llma-companion-portrait" alt="">
            <button class="llma-companion-close" id="llma-companion-close" title="Hide for this session" aria-label="Hide companion">&times;</button>
        </div>
        <div class="llma-companion-bubble" id="llma-companion-bubble" aria-live="polite"></div>
        <div class="llma-companion-actions" id="llma-companion-actions"></div>
        <div class="llma-companion-input-wrap" id="llma-companion-input-wrap" style="display:none;">
            <input type="text" id="llma-companion-input" class="llma-companion-input"
                   placeholder="Ask anything..." aria-label="Ask the companion" autocomplete="off" maxlength="500">
            <button class="llma-companion-send" id="llma-companion-send" title="Send" aria-label="Send">
                <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
                    <path d="M2 7h10M8 3l4 4-4 4" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>
            </button>
        </div>`;
    document.body.appendChild(root);

    // Click portrait → toggle expanded; close button → hide for session.
    document.getElementById('llma-companion-portrait-wrap')?.addEventListener('click', (e) => {
        if (e.target.closest('.llma-companion-close')) return; // close handled separately
        llmaCompanionToggleExpanded();
    });
    document.getElementById('llma-companion-close')?.addEventListener('click', (e) => {
        e.stopPropagation();
        llmaCompanionHideForSession();
    });
    document.getElementById('llma-companion-send')?.addEventListener('click', llmaCompanionAskFromInput);
    document.getElementById('llma-companion-input')?.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            llmaCompanionAskFromInput();
        } else if (e.key === 'Escape') {
            // Escape cancels a queued pending-action (e.g. "explain") without sending. If
            // there's no pending state, let the keydown fall through so users can also use
            // Escape to blur via the browser's default chain.
            if (llmaCompanionState().pendingAction) {
                e.preventDefault();
                llmaCompanionClearPendingAction();
            }
        }
    });

    llmaCompanionSetupDrag(root);
}

// Drag the whole overlay by holding the portrait. Updates corner snap + offsets,
// then throttle-persists to settings so position survives reloads.
function llmaCompanionSetupDrag(root) {
    const handle = document.getElementById('llma-companion-portrait-wrap');
    if (!handle) return;
    let startX = 0, startY = 0, originX = 0, originY = 0, dragging = false;
    handle.addEventListener('mousedown', (e) => {
        if (e.button !== 0) return;
        if (e.target.closest('.llma-companion-close')) return;
        const rect = root.getBoundingClientRect();
        startX = e.clientX; startY = e.clientY;
        originX = rect.left; originY = rect.top;
        dragging = false;
    });
    document.addEventListener('mousemove', (e) => {
        if (e.buttons !== 1) return;
        if (Math.abs(e.clientX - startX) + Math.abs(e.clientY - startY) < 4) return;
        dragging = true;
        // While dragging we use absolute positioning anchored to top-left.
        const w = root.offsetWidth, h = root.offsetHeight;
        const left = Math.max(4, Math.min(window.innerWidth - w - 4, originX + (e.clientX - startX)));
        const top  = Math.max(4, Math.min(window.innerHeight - h - 4, originY + (e.clientY - startY)));
        root.style.left = left + 'px';
        root.style.top  = top + 'px';
        root.style.right = 'auto';
        root.style.bottom = 'auto';
    });
    document.addEventListener('mouseup', () => {
        if (!dragging) return;
        dragging = false;
        // Snap to nearest corner and store offsets relative to that corner so the
        // overlay re-anchors correctly after window resize / reload.
        const rect = root.getBoundingClientRect();
        const midX = window.innerWidth / 2;
        const midY = window.innerHeight / 2;
        const horizontal = (rect.left + rect.width / 2) < midX ? 'left' : 'right';
        const vertical   = (rect.top  + rect.height / 2) < midY ? 'top'  : 'bottom';
        const corner = `${vertical}-${horizontal}`;
        const offsetX = horizontal === 'left' ? rect.left : (window.innerWidth - rect.right);
        const offsetY = vertical   === 'top'  ? rect.top  : (window.innerHeight - rect.bottom);
        const cfg = LLMAState.settings.companion = LLMAState.settings.companion || {};
        cfg.corner  = corner;
        cfg.offsetX = Math.round(offsetX);
        cfg.offsetY = Math.round(offsetY);
        llmaCompanionApplyPosition();
        llmaCompanionPersistThrottled();
    });
}

function llmaCompanionPersistThrottled() {
    const s = llmaCompanionState();
    if (s.saveTimer) clearTimeout(s.saveTimer);
    s.saveTimer = setTimeout(() => {
        s.saveTimer = null;
        // Use the existing scalar-settings save path. The server's filter strips
        // assistants/tools but lets `companion` through to the personal layer.
        llmaSaveSettings?.();
    }, LLMA_COMPANION_SAVE_THROTTLE_MS);
}

// -- Settings application ----------------------------------------

function llmaCompanionApplySettings() {
    const root = document.getElementById('llma-companion-root');
    if (!root) {
        // First call: render lazily so we don't add DOM cost when companion is off.
        if (LLMAState.settings?.companion?.enabled) {
            llmaCompanionRender();
            return llmaCompanionApplySettings();
        }
        return;
    }
    const cfg = LLMAState.settings?.companion || {};
    if (!cfg.enabled || llmaCompanionState()._hiddenForSession) {
        root.style.display = 'none';
        return;
    }
    root.style.display = '';
    root.style.opacity = String(cfg.opacity ?? 0.95);
    llmaCompanionApplyPosition();
    llmaCompanionApplyExpanded(!!cfg.expanded);
    llmaCompanionApplyPortrait();
    llmaCompanionRenderActions();
    // Re-arm chatter wiring whenever settings change. attach* helpers are
    // idempotent (set once, return early after); the idle reschedule picks up
    // the new minutes value or cancels the timer if the user just turned idle off.
    llmaCompanionAttachActivityListeners();
    llmaCompanionAttachGenerationHook();
    llmaCompanionRescheduleIdle();
}

function llmaCompanionApplyPosition() {
    const root = document.getElementById('llma-companion-root');
    if (!root) return;
    const cfg = LLMAState.settings?.companion || {};
    const corner  = cfg.corner ?? 'bottom-right';
    const offsetX = cfg.offsetX ?? 24;
    const offsetY = cfg.offsetY ?? 24;
    const [v, h] = corner.split('-');
    root.style.left   = h === 'left'   ? offsetX + 'px' : 'auto';
    root.style.right  = h === 'right'  ? offsetX + 'px' : 'auto';
    root.style.top    = v === 'top'    ? offsetY + 'px' : 'auto';
    root.style.bottom = v === 'bottom' ? offsetY + 'px' : 'auto';
}

function llmaCompanionApplyExpanded(expanded) {
    const root = document.getElementById('llma-companion-root');
    if (!root) return;
    root.classList.toggle('llma-companion-expanded', expanded);
    const inputWrap = document.getElementById('llma-companion-input-wrap');
    if (inputWrap) inputWrap.style.display = expanded ? '' : 'none';
}

function llmaCompanionToggleExpanded() {
    const cfg = LLMAState.settings.companion = LLMAState.settings.companion || {};
    cfg.expanded = !cfg.expanded;
    llmaCompanionApplyExpanded(cfg.expanded);
    if (cfg.expanded) {
        setTimeout(() => document.getElementById('llma-companion-input')?.focus(), 50);
    }
    llmaCompanionPersistThrottled();
}

function llmaCompanionApplyPortrait() {
    const persona = llmaCompanionResolvePersona();
    const img = document.getElementById('llma-companion-portrait');
    const wrap = document.getElementById('llma-companion-portrait-wrap');
    if (!img || !wrap) return;
    const fallback = '/ExtensionFile/LLMAssistantExtension/Assets/swarmui-logo.jpg';
    img.src = persona?.avatar || fallback;
    img.alt = persona?.name ? `${persona.name} avatar` : 'Companion';
    wrap.style.borderColor = persona?.color || '#7c8aff';
}

// -- Action buttons ---------------------------------------------

function llmaCompanionRenderActions() {
    const host = document.getElementById('llma-companion-actions');
    if (!host) return;
    const cfg = LLMAState.settings?.companion || {};
    const btns = cfg.buttons || {};
    const items = [];
    if (btns.ask !== false) {
        items.push({ id: 'ask', label: 'Ask', title: 'Ask the companion anything', handler: llmaCompanionExpandAndFocus });
    }
    if (btns.critique_last_image !== false) {
        items.push({ id: 'critique', label: 'Critique', title: 'Critique my last image', handler: llmaCompanionRunCritique });
    }
    if (btns.help_with_prompt !== false) {
        items.push({ id: 'prompt', label: 'Prompt', title: 'Suggest fixes for my current Generate-tab prompt', handler: llmaCompanionRunPromptHelp });
    }
    if (btns.suggest_preset !== false) {
        items.push({ id: 'preset', label: 'Preset', title: 'Suggest a saved preset that matches what you\'re making', handler: llmaCompanionRunPresetSuggest });
    }
    if (btns.explain_feature !== false) {
        items.push({ id: 'explain', label: 'Explain', title: 'Ask about a SwarmUI feature (uses the docs)', handler: llmaCompanionRunExplain });
    }
    if (btns.daily_tip !== false) {
        items.push({ id: 'tip', label: 'Tip', title: 'Get one quick SwarmUI tip', handler: llmaCompanionRunDailyTip });
    }
    host.innerHTML = items.map(it =>
        `<button class="llma-companion-action" data-act="${it.id}" title="${llmaEscapeHtml(it.title)}">${llmaEscapeHtml(it.label)}</button>`
    ).join('');
    for (const it of items) {
        host.querySelector(`button[data-act="${it.id}"]`)?.addEventListener('click', it.handler);
    }
}

// -- Hide for session (close button) ----------------------------

function llmaCompanionHideForSession() {
    llmaCompanionState()._hiddenForSession = true;
    const root = document.getElementById('llma-companion-root');
    if (root) root.style.display = 'none';
    llmaShowToast?.('Companion hidden for this session — re-enable it in Settings > Companion', 'info');
}

// -- Send a message ---------------------------------------------

// Pending-action state lets buttons like "Explain a feature" wrap the user's
// next free-text message with a context prefix (e.g. "use the swarm_docs tool")
// instead of opening a separate prompt() dialog. The state lives on
// LLMAState.companion.pendingAction and is cleared after a single send,
// or when the user presses Escape, or when they click another action button.
const LLMA_COMPANION_PENDING_ACTIONS = {
    explain: {
        placeholder: 'Which SwarmUI feature should I explain?',
        prefix: '[Context: the user wants this SwarmUI feature explained. If you have the swarm_docs tool, list and read the most relevant doc(s) before answering. Otherwise answer from your training knowledge but say so.]',
    },
};

function llmaCompanionSetPendingAction(actionId) {
    const s = llmaCompanionState();
    s.pendingAction = actionId;
    const input = document.getElementById('llma-companion-input');
    const wrap = document.getElementById('llma-companion-input-wrap');
    if (!input) return;
    const def = actionId ? LLMA_COMPANION_PENDING_ACTIONS[actionId] : null;
    input.placeholder = def?.placeholder || 'Ask anything...';
    if (wrap) wrap.classList.toggle('llma-companion-pending', !!actionId);
}

function llmaCompanionClearPendingAction() {
    llmaCompanionSetPendingAction(null);
}

async function llmaCompanionAskFromInput() {
    const input = document.getElementById('llma-companion-input');
    if (!input) return;
    const text = input.value?.trim();
    if (!text) return;
    input.value = '';
    const s = llmaCompanionState();
    const def = s.pendingAction ? LLMA_COMPANION_PENDING_ACTIONS[s.pendingAction] : null;
    if (def) {
        llmaCompanionClearPendingAction();
        await llmaCompanionAsk(text, null, def.prefix);
    } else {
        await llmaCompanionAsk(text);
    }
}

function llmaCompanionExpandAndFocus() {
    const cfg = LLMAState.settings.companion = LLMAState.settings.companion || {};
    if (!cfg.expanded) {
        cfg.expanded = true;
        llmaCompanionApplyExpanded(true);
    }
    setTimeout(() => document.getElementById('llma-companion-input')?.focus(), 50);
}

async function llmaCompanionGetOrCreateThread(personaId) {
    const s = llmaCompanionState();
    if (s.threadId) return s.threadId;
    try {
        const result = await llmaRequest('LLMAssistantCreateThread', {
            assistantId: personaId || '',
            title: 'Companion chat'
        });
        if (!result?.success || !result.thread) return null;
        const thread = typeof result.thread === 'string' ? JSON.parse(result.thread) : result.thread;
        s.threadId = thread.id;
        return s.threadId;
    } catch {
        return null;
    }
}

// Core "ask" path: ensures a thread exists, builds the payload, opens the
// streaming WS, renders chunks into the bubble. `mediaPayload`, when present,
// is an array of { url, mediaType } already saved server-side (eg by the
// chat upload endpoint or sourced from GetCompanionContext).
async function llmaCompanionAsk(message, mediaPayload = null, contextPrefix = null) {
    const s = llmaCompanionState();
    if (s.isStreaming) {
        llmaShowToast?.('Companion is already replying...', 'info');
        return;
    }
    const persona = llmaCompanionResolvePersona();
    if (!persona) {
        llmaCompanionSetBubbleText('No assistants are configured yet — open the LLM Assistant tab to set one up.');
        return;
    }
    const threadId = await llmaCompanionGetOrCreateThread(persona.id);
    if (!threadId) {
        llmaCompanionSetBubbleText('Could not start a companion thread. Check that you have LLM Chat permission.');
        return;
    }
    // Make sure the bubble is visible (auto-expand on first send).
    const cfg = LLMAState.settings.companion = LLMAState.settings.companion || {};
    if (!cfg.expanded) {
        cfg.expanded = true;
        llmaCompanionApplyExpanded(true);
    }
    const fullMessage = contextPrefix ? `${contextPrefix}\n\n${message}` : message;
    const payload = {
        threadId,
        message: fullMessage,
        instructionId: 'companion',
    };
    if (LLMAState.currentModel) payload.model = LLMAState.currentModel;
    if (mediaPayload) payload.media = mediaPayload;

    s.isStreaming = true;
    llmaCompanionSetBubbleStreaming();
    let streamingText = '';
    try {
        s.socket = makeWSRequest('LLMAssistantSendMessageWS', payload, (data) => {
            if (!data) return;
            if (data.error) {
                llmaCompanionSetBubbleText(`Error: ${data.error}`);
                s.isStreaming = false;
                return;
            }
            if (data.chunk) {
                streamingText += data.chunk;
                llmaCompanionRenderBubbleStream(streamingText);
            }
            if (data.tool_call) {
                llmaCompanionAppendToolPill(`Calling ${data.tool_call.name}...`);
            }
            if (data.tool_result) {
                llmaCompanionAppendToolPill(`✓ ${data.tool_result.name}`);
            }
            if (data.done) {
                const finalText = (data.full_text ?? streamingText) || '(no reply)';
                llmaCompanionSetBubbleText(llmaCompanionStripToolTags(finalText) || '(no reply)');
                s.isStreaming = false;
            }
        }, 0, (err) => {
            llmaCompanionSetBubbleText(`Connection error: ${err?.message || 'unknown'}`);
            s.isStreaming = false;
        });
    } catch (e) {
        llmaCompanionSetBubbleText(`Failed to start: ${e?.message || e}`);
        s.isStreaming = false;
    }
}

// -- Bubble rendering -------------------------------------------

function llmaCompanionSetBubbleText(text) {
    const bubble = document.getElementById('llma-companion-bubble');
    if (!bubble) return;
    bubble.classList.remove('llma-companion-bubble-streaming');
    bubble.textContent = text;
}

function llmaCompanionSetBubbleStreaming() {
    const bubble = document.getElementById('llma-companion-bubble');
    if (!bubble) return;
    bubble.classList.add('llma-companion-bubble-streaming');
    bubble.innerHTML = '<span class="llma-companion-typing">···</span>';
}

function llmaCompanionRenderBubbleStream(text) {
    const bubble = document.getElementById('llma-companion-bubble');
    if (!bubble) return;
    bubble.classList.add('llma-companion-bubble-streaming');
    // Plain text in v1 — markdown rendering would require pulling in marked at
    // load time which we'd rather not do for a tiny speech bubble. Newlines
    // collapse via `white-space: pre-wrap` in the CSS.
    bubble.textContent = llmaCompanionStripToolTags(text);
}

function llmaCompanionAppendToolPill(label) {
    // Pills are non-essential progress hints; safely skip if the bubble is gone.
    const bubble = document.getElementById('llma-companion-bubble');
    if (!bubble) return;
    const pill = document.createElement('span');
    pill.className = 'llma-companion-tool-pill';
    pill.textContent = label;
    bubble.appendChild(pill);
}

function llmaCompanionStripToolTags(text) {
    if (!text) return '';
    return text.replace(LLMA_COMPANION_TOOL_TAG_RE, '').trim();
}

// -- Action: Critique my last image ------------------------------

async function llmaCompanionRunCritique() {
    if (llmaCompanionState().isStreaming) return;
    llmaCompanionSetBubbleText('Looking at your last image...');
    let ctx;
    try {
        ctx = await llmaRequest('LLMAssistantGetCompanionContext', {});
    } catch {
        llmaCompanionSetBubbleText("Couldn't fetch your image history.");
        return;
    }
    if (!ctx?.success) {
        llmaCompanionSetBubbleText(ctx?.error || "Couldn't fetch your image history.");
        return;
    }
    if (!ctx.lastImage) {
        llmaCompanionSetBubbleText("You haven't generated any images yet — try the Generate tab first.");
        return;
    }
    const img = ctx.lastImage;
    llmaCompanionState().lastImage = img;
    // Build a metadata snippet the LLM can reason about. We trim to the most
    // relevant fields so we don't dump kilobytes of EXIF junk into the prompt.
    const metaSnippet = llmaCompanionSummarizeMetadata(img.metadata);
    const contextPrefix = `[Context: critique my most recent image]\n${metaSnippet}`.trim();
    const message = "Look at the image I just sent. What's the single biggest thing I should change to get a better result? Be specific about parameters or prompt wording.";
    const mediaPayload = img.url ? [{ url: img.url, mediaType: img.mediaType || 'image/png' }] : null;
    if (!mediaPayload) {
        // No URL — fall back to text-only critique using metadata alone. Better than nothing.
        llmaCompanionAsk(`${message}\n\nNote: image bytes are not accessible from this server config; only the metadata is below.`, null, contextPrefix);
        return;
    }
    llmaCompanionAsk(message, mediaPayload, contextPrefix);
}

// Pulls out the most LLM-useful fields from a Swarm metadata blob.
// Quietly tolerates malformed metadata (returns "(no metadata)") rather than
// failing the whole flow when one image is weird.
function llmaCompanionSummarizeMetadata(metadataStr) {
    if (!metadataStr) return '(no metadata)';
    let parsed;
    try { parsed = typeof metadataStr === 'string' ? JSON.parse(metadataStr) : metadataStr; }
    catch { return '(metadata not parseable)'; }
    // Swarm wraps the actual params in either sui_image_params or comparable keys.
    const params = parsed?.sui_image_params || parsed?.params || parsed;
    if (!params || typeof params !== 'object') return '(no metadata fields)';
    const interesting = ['model', 'prompt', 'negativeprompt', 'steps', 'cfgscale', 'sampler',
                         'scheduler', 'width', 'height', 'aspectratio', 'seed', 'loras', 'refinermodel'];
    const lines = [];
    for (const k of interesting) {
        if (params[k] === undefined || params[k] === null || params[k] === '') continue;
        let v = params[k];
        if (typeof v === 'string' && v.length > 200) v = v.slice(0, 200) + '…';
        lines.push(`${k}: ${v}`);
    }
    return lines.length ? lines.join('\n') : '(no metadata fields)';
}

// -- Action: Help with my prompt --------------------------------

async function llmaCompanionRunPromptHelp() {
    if (llmaCompanionState().isStreaming) return;
    // getGenInput is the SwarmUI core helper that returns the Generate tab's
    // current parameter snapshot. Available globally on any page that has the
    // Generate UI loaded — return early with a toast otherwise.
    if (typeof getGenInput !== 'function') {
        llmaCompanionSetBubbleText("Open the Generate tab once first so I can read your current prompt.");
        return;
    }
    let snap;
    try { snap = getGenInput(); }
    catch { llmaCompanionSetBubbleText("Couldn't read the Generate tab parameters."); return; }
    const prompt = snap?.prompt;
    if (!prompt || !String(prompt).trim()) {
        llmaCompanionSetBubbleText("Your Generate-tab prompt is empty — type something there first.");
        return;
    }
    const interesting = ['model', 'cfgscale', 'steps', 'sampler', 'width', 'height', 'aspectratio'];
    const lines = interesting
        .filter(k => snap[k] !== undefined && snap[k] !== null && snap[k] !== '')
        .map(k => `${k}: ${snap[k]}`);
    const negative = snap.negativeprompt ? `\nnegative prompt: ${snap.negativeprompt}` : '';
    const contextPrefix = `[Context: critique my Generate-tab setup]\nprompt: ${prompt}${negative}\n${lines.join('\n')}`;
    const message = "What's the single biggest improvement I could make here? Suggest one prompt edit OR one parameter tweak — not both.";
    llmaCompanionAsk(message, null, contextPrefix);
}

// -- Action: Suggest a preset -----------------------------------

async function llmaCompanionRunPresetSuggest() {
    if (llmaCompanionState().isStreaming) return;
    let presetResult;
    try {
        presetResult = await llmaRequest('LLMAssistantGetImagePresets', {});
    } catch {
        llmaCompanionSetBubbleText("Couldn't read your preset list.");
        return;
    }
    const presets = Array.isArray(presetResult?.presets) ? presetResult.presets : [];
    if (presets.length === 0) {
        llmaCompanionSetBubbleText("You don't have any saved presets yet. Save one on the Generate tab first.");
        return;
    }
    // Read the user's current Generate-tab snapshot so the recommendation can match
    // the prompt + model they're aiming at. Tolerate missing getGenInput (page hasn't
    // loaded the Generate UI yet) — preset list alone is still useful.
    let snap = null;
    if (typeof getGenInput === 'function') {
        try { snap = getGenInput(); } catch { snap = null; }
    }
    const presetLines = presets.map(p => {
        // Prefer the richer summary; fall back to title — description for older payloads.
        const summary = p.summary || (p.description ? `${p.title} — ${p.description}` : p.title);
        return `- ${summary}`;
    }).join('\n');
    const promptText = snap?.prompt && String(snap.prompt).trim() ? String(snap.prompt).trim() : null;
    const currentLines = [];
    if (promptText) currentLines.push(`prompt: ${promptText}`);
    for (const k of ['model', 'cfgscale', 'steps', 'sampler', 'width', 'height', 'aspectratio']) {
        if (snap?.[k] !== undefined && snap[k] !== null && snap[k] !== '') {
            currentLines.push(`${k}: ${snap[k]}`);
        }
    }
    const currentBlock = currentLines.length ? `My current Generate-tab setup:\n${currentLines.join('\n')}\n\n` : '';
    const contextPrefix = `[Context: pick a saved preset that best fits what the user is trying to make]\n${currentBlock}Available presets:\n${presetLines}`;
    const message = promptText
        ? "Which preset best fits the prompt and setup above, and why? Name exactly one preset, in one short sentence."
        : "I haven't decided what to make yet. Pick one preset from the list that you'd recommend as a great everyday default, and say why in one sentence.";
    llmaCompanionAsk(message, null, contextPrefix);
}

// -- Action: Explain a feature ----------------------------------

// Two-step interaction: first click queues a pending action and focuses the
// input (changing its placeholder); the user types what they want explained
// and presses Enter, which sends with the explain prefix wrapped in.
function llmaCompanionRunExplain() {
    if (llmaCompanionState().isStreaming) return;
    llmaCompanionSetPendingAction('explain');
    llmaCompanionExpandAndFocus();
    // Hint in the bubble so it's obvious *why* the input changed; users can also
    // press Escape to cancel the queued action.
    llmaCompanionSetBubbleText("What feature should I explain? Type below and press Enter (Esc to cancel).");
}

// -- Action: Daily tip ------------------------------------------

async function llmaCompanionRunDailyTip() {
    if (llmaCompanionState().isStreaming) return;
    const contextPrefix = "[Context: surface one quick, non-obvious tip that helps the user get more out of SwarmUI. Pick a different angle each time — prompt syntax, a setting, a workflow, a keyboard shortcut, an extension, etc.]";
    const message = "Give me one short, useful SwarmUI tip — under 30 words, no preamble.";
    llmaCompanionAsk(message, null, contextPrefix);
}

// -- Chatter scheduler ------------------------------------------
// Single gate function every unsolicited trigger funnels through. Centralizes
// the master mute, per-trigger toggle, quiet-hours, max-per-session, and a
// global cooldown so the user only ever has to think about quiet mode if any
// of this gets noisy. All counters live in memory and reset on page reload —
// session-level granularity is what the UI promises ("max per session").

const LLMA_COMPANION_MIN_INTERVAL_MS = 90 * 1000;        // global gap between any two unsolicited messages
const LLMA_COMPANION_REACTION_COOLDOWN_MS = 60 * 1000;   // burst window after a reaction fires
const LLMA_COMPANION_REACTION_DEBOUNCE_MS = 4 * 1000;    // wait this long after the LAST image of a batch
const LLMA_COMPANION_GREETING_KEY = 'llma-companion-greeted';

function llmaCompanionChatterState() {
    const s = llmaCompanionState();
    if (!s._chatter) {
        s._chatter = {
            firedThisSession: 0,
            lastFiredAt: 0,
            lastReactionAt: 0,
            reactionDebounce: null,
            pendingReactionImage: null,
            idleTimer: null,
            lastUserActivityAt: Date.now(),
            activityHooked: false,
            genHookAttached: false,
        };
    }
    return s._chatter;
}

// Returns true if the current local time falls inside the user's configured
// quiet-hours window. Honors midnight-spanning ranges (e.g. 22→8).
function llmaCompanionInQuietHours(chatter) {
    if (!chatter?.quietHours) return false;
    const start = ((chatter.quietStart ?? 22) | 0) % 24;
    const end   = ((chatter.quietEnd   ?? 8)  | 0) % 24;
    if (start === end) return false; // empty range
    const h = new Date().getHours();
    return start < end ? (h >= start && h < end) : (h >= start || h < end);
}

// Central eligibility check. `triggerId` is one of 'greeting' | 'reactions' | 'idle'
// and must also be enabled in `companion.chatter` for the message to fire.
// Returns true and consumes one slot from the per-session cap on success.
function llmaCompanionCanChatter(triggerId) {
    const cfg  = LLMAState.settings?.companion;
    const chat = cfg?.chatter;
    if (!cfg?.enabled || !chat) return false;
    if (chat.quietMode) return false;
    if (!chat[triggerId]) return false;
    if (llmaCompanionState().isStreaming) return false;
    if (llmaCompanionInQuietHours(chat)) return false;
    if (llmaCompanionState()._hiddenForSession) return false;
    if (typeof document !== 'undefined' && document.hidden) return false;
    const cs = llmaCompanionChatterState();
    const cap = chat.maxPerSession ?? 5;
    if (cap > 0 && cs.firedThisSession >= cap) return false;
    if (Date.now() - cs.lastFiredAt < LLMA_COMPANION_MIN_INTERVAL_MS) return false;
    return true;
}

function llmaCompanionConsumeChatterSlot() {
    const cs = llmaCompanionChatterState();
    cs.firedThisSession += 1;
    cs.lastFiredAt = Date.now();
}

// -- Greeting -----------------------------------------------------

// Fires once per browser session if greetings are enabled. The flag lives in
// sessionStorage so refreshing the page doesn't re-greet, but a fresh tab in a
// new browser session does. A short delay after boot avoids racing the LLMA
// tab's own startup work.
function llmaCompanionMaybeGreet() {
    if (!llmaCompanionCanChatter('greeting')) return;
    try {
        if (sessionStorage.getItem(LLMA_COMPANION_GREETING_KEY) === '1') return;
    } catch { /* sessionStorage might be disabled — fall through and just greet, no big deal */ }
    try { sessionStorage.setItem(LLMA_COMPANION_GREETING_KEY, '1'); } catch { /* same */ }
    llmaCompanionConsumeChatterSlot();
    const persona = llmaCompanionResolvePersona();
    const personaHint = persona?.name ? `as ${persona.name}` : '';
    const contextPrefix = `[Context: greet the user ${personaHint} for the start of a new SwarmUI session. Keep it warm, under 20 words, and reference the user by name only if you know it. No emoji.]`;
    const message = "Say hi.";
    llmaCompanionAsk(message, null, contextPrefix);
}

// -- Generation reactions ---------------------------------------

// Hook into SwarmUI's gotImageResult by monkey-patching. The original function
// is invoked first, our reaction logic runs after — failure-isolated so any
// error here can't break the Generate tab. Batches are debounced: in a 4-image
// batch the four `gotImageResult` calls collapse to one reaction with the last
// image. A global cooldown then prevents back-to-back generations from
// burning chatter budget.
function llmaCompanionAttachGenerationHook() {
    const cs = llmaCompanionChatterState();
    if (cs.genHookAttached) return;
    if (typeof window === 'undefined' || typeof window.gotImageResult !== 'function') return;
    const original = window.gotImageResult;
    window.gotImageResult = function (image, metadata, batchId) {
        const ret = original.apply(this, arguments);
        try { llmaCompanionOnImageGenerated(image, metadata, batchId); }
        catch (e) { console.warn('[LLMAssistant] gotImageResult hook error:', e); }
        return ret;
    };
    cs.genHookAttached = true;
}

function llmaCompanionOnImageGenerated(image, metadata, batchId) {
    const cs = llmaCompanionChatterState();
    // Always remember the latest image so the debounced fire can use it. We
    // don't gate on `canChatter` here — settings might change while a batch
    // is finishing — gate at fire time instead.
    cs.pendingReactionImage = { image, metadata, batchId };
    if (cs.reactionDebounce) clearTimeout(cs.reactionDebounce);
    cs.reactionDebounce = setTimeout(llmaCompanionFireReaction, LLMA_COMPANION_REACTION_DEBOUNCE_MS);
}

function llmaCompanionFireReaction() {
    const cs = llmaCompanionChatterState();
    cs.reactionDebounce = null;
    const pending = cs.pendingReactionImage;
    cs.pendingReactionImage = null;
    if (!pending) return;
    if (!llmaCompanionCanChatter('reactions')) return;
    if (Date.now() - cs.lastReactionAt < LLMA_COMPANION_REACTION_COOLDOWN_MS) return;
    cs.lastReactionAt = Date.now();
    llmaCompanionConsumeChatterSlot();
    const metaSnippet = llmaCompanionSummarizeMetadata(pending.metadata);
    // No image attachment for reactions — they fire passively, and shipping
    // base64 image data on every generation would multiply the LLM cost.
    // Metadata-only commentary is what we want here anyway: a quick "your CFG
    // is high — expect saturated colors" or "30 steps is plenty for this
    // sampler" rather than a full vision critique.
    const contextPrefix = `[Context: an image just finished generating in SwarmUI's Generate tab. Drop one short, friendly reaction tied to the metadata below — celebrate, give a quick tip, or note something noteworthy. Under 25 words. No emoji.]\n${metaSnippet}`;
    const message = "React to my latest generation in one short line.";
    llmaCompanionAsk(message, null, contextPrefix);
}

// -- Idle nudges -------------------------------------------------

// Reset the idle countdown on any user input; when the timer expires and all
// chatter gates pass, send a contextual nudge. We avoid being clever about
// which SwarmUI tab is active in v1 — the LLM gets a generic "user has been
// idle" prompt and produces something appropriate. Tab-aware idle prompts
// can layer on later without changing this scaffolding.
function llmaCompanionAttachActivityListeners() {
    const cs = llmaCompanionChatterState();
    if (cs.activityHooked) return;
    cs.activityHooked = true;
    const reset = () => {
        cs.lastUserActivityAt = Date.now();
        llmaCompanionRescheduleIdle();
    };
    // Passive listeners: zero perf impact and we don't preventDefault anyway.
    for (const evt of ['mousemove', 'mousedown', 'keydown', 'touchstart', 'wheel']) {
        document.addEventListener(evt, reset, { passive: true });
    }
    document.addEventListener('visibilitychange', () => {
        if (!document.hidden) reset();
    });
}

function llmaCompanionRescheduleIdle() {
    const cs = llmaCompanionChatterState();
    if (cs.idleTimer) clearTimeout(cs.idleTimer);
    const cfg = LLMAState.settings?.companion?.chatter;
    if (!cfg?.idle) return;
    const minutes = Math.max(2, cfg.idleMinutes ?? 8);
    cs.idleTimer = setTimeout(llmaCompanionFireIdle, minutes * 60 * 1000);
}

function llmaCompanionFireIdle() {
    if (!llmaCompanionCanChatter('idle')) {
        // Even if we can't fire right now (cap reached, quiet hours, etc.),
        // reschedule so we'll try again after the next idle window. Cheap.
        llmaCompanionRescheduleIdle();
        return;
    }
    llmaCompanionConsumeChatterSlot();
    const contextPrefix = "[Context: the user has been idle for several minutes. Pipe up with one friendly, useful nudge — could be a tip, a question about what they're working on, or an offer to help. Under 25 words. No emoji. Don't apologize for being quiet.]";
    const message = "Drop a friendly idle nudge.";
    llmaCompanionAsk(message, null, contextPrefix);
    // Re-arm so a long idle period continues to fire — bounded by maxPerSession.
    llmaCompanionRescheduleIdle();
}

// -- Boot --------------------------------------------------------

async function llmaCompanionBoot() {
    const s = llmaCompanionState();
    if (s.booted) return;
    s.booted = true;
    try {
        // Anyone without llm_companion permission can skip silently — the endpoint
        // call below would 403 otherwise and we don't want a noisy console error.
        if (typeof permissions !== 'undefined' && permissions?.hasPermission && !permissions.hasPermission('llm_companion')) {
            return;
        }
        if (!LLMAState.settings || Object.keys(LLMAState.settings).length === 0) {
            await llmaLoadSettings();
        }
        if (!LLMAState.assistants || LLMAState.assistants.length === 0) {
            await llmaLoadAssistants();
        }
    } catch (e) {
        console.warn('[LLMAssistant] Companion boot deferred:', e);
        return;
    }
    if (!LLMAState.settings?.companion?.enabled) return;
    llmaCompanionRender();
    llmaCompanionApplySettings();
    // Chatter wiring — listeners and the gen hook are global / cheap, install
    // once even if specific triggers are off (toggling them on later won't
    // require a refresh). Greeting fires after a short delay so the LLM Assistant
    // tab's own init isn't competing with us for the first request slot.
    llmaCompanionAttachActivityListeners();
    llmaCompanionAttachGenerationHook();
    llmaCompanionRescheduleIdle();
    setTimeout(llmaCompanionMaybeGreet, 1500);
}

// Hook into SwarmUI's session-ready callback if available; fall back to a
// MutationObserver if companion script loads before main.js defines the array.
(function llmaCompanionAttachBoot() {
    function tryAttach() {
        if (typeof sessionReadyCallbacks !== 'undefined' && Array.isArray(sessionReadyCallbacks)) {
            sessionReadyCallbacks.push(llmaCompanionBoot);
            return true;
        }
        return false;
    }
    if (tryAttach()) return;
    // Defer until the array exists. Cheap; only runs at page boot.
    let tries = 0;
    const iv = setInterval(() => {
        tries++;
        if (tryAttach() || tries > 50) {
            clearInterval(iv);
            if (tries > 50) console.warn('[LLMAssistant] sessionReadyCallbacks never appeared; companion will not auto-boot.');
        }
    }, 100);
})();

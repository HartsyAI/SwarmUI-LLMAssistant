/* ================================================================
   LLM Assistant - chat/chat-compare.js
   Side-by-side model comparison ("compare mode").

   One user prompt is fanned out to two models. Both replies stream
   concurrently over ONE WebSocket (the server tags each event with a
   `lane`); we demux by lane into two columns. The replies persist as
   sibling children of the same user message (shared groupId), so the
   history renderer shows them as columns and "Keep this one" just
   moves the active leaf.

   Designed for N lanes (data model + protocol carry a lane index); the
   UI currently exposes two. Device placement (cuda:N / cpu / cloud)
   rides along per lane for display + persistence and is forward-
   compatible with multi-GPU (the device label can become "cuda:0+1").
   ================================================================ */

(function () {
    'use strict';

    // ---- Model + device helpers -------------------------------------------------

    /** Device label a model is served on, from its backend metadata. Cloud models have none. */
    function llmaGetModelDevice(modelId) {
        const m = (LLMAState.availableModels || []).find(x => x.id === modelId);
        return (m && m.metadata && m.metadata.device) ? m.metadata.device : 'cloud';
    }

    /** Display name for a model id. */
    function llmaModelName(modelId) {
        const m = (LLMAState.availableModels || []).find(x => x.id === modelId);
        return (m && (m.name || m.id)) || modelId || '(model)';
    }

    /** Every place a given model id can run — one option per (backend, device) pair. A single local backend
     *  contributes several (its `devices` metadata: eg "cuda:0,cpu"); multiple backends each add their own.
     *  Each: { backendId, device, provider }. */
    function llmaModelDeviceOptions(modelId) {
        const seen = new Set();
        const out = [];
        for (const m of (LLMAState.availableModels || [])) {
            if (m.id !== modelId) { continue; }
            const backendId = (typeof m.backend_id === 'number') ? m.backend_id : -1;
            // Prefer the multi-device list; fall back to the single device (or cloud).
            const listed = (m.metadata && m.metadata.devices) ? String(m.metadata.devices).split(',') : null;
            const devices = (listed && listed.length) ? listed : [(m.metadata && m.metadata.device) || 'cloud'];
            for (const raw of devices) {
                const device = (raw || '').trim() || 'cloud';
                const key = `${backendId}|${device}`;
                if (seen.has(key)) { continue; }
                seen.add(key);
                out.push({ backendId, device, provider: m.provider || '' });
            }
        }
        return out;
    }

    /** Resolve the (backend, device) a lane will run on for the NEXT send: honor the per-lane override
     *  (set via the column-header dropdown) when it still matches an option for this model, else default
     *  to the model's first option. Returns { backendId, device }. */
    function llmaLaneDeviceInfo(lane, modelId) {
        const opts = llmaModelDeviceOptions(modelId);
        const pref = LLMAState.laneBackends ? LLMAState.laneBackends[lane] : null;
        let chosen = (pref && typeof pref === 'object')
            ? opts.find(o => o.backendId === pref.backendId && o.device === pref.device)
            : null;
        if (!chosen) { chosen = opts[0] || { backendId: -1, device: llmaGetModelDevice(modelId) || 'cloud' }; }
        return chosen;
    }

    /** Column-header inner markup: model name + a device control. When the model can run on more than one
     *  device/backend the device becomes a <select> (pick where this lane runs for the next send); with a
     *  single device it's a static badge. `device` is the device that this column ran on (or will run on). */
    function llmaLaneHeadHtml(model, device, lane) {
        const name = llmaModelName(model);
        const dev = device || llmaGetModelDevice(model) || 'cloud';
        const full = dev && dev !== 'cloud' ? `${name} · ${dev}` : name;
        const nameEl = `<span class="llma-compare-model" title="${llmaEscapeHtml(full)}">${llmaEscapeHtml(name)}</span>`;
        const opts = llmaModelDeviceOptions(model);
        // A single option (or lane index unknown) → read-only badge; nothing to switch between.
        if (opts.length < 2 || typeof lane !== 'number') {
            return `${nameEl}<span class="llma-compare-device" title="Runs on ${llmaEscapeHtml(dev)}">${llmaEscapeHtml(dev)}</span>`;
        }
        // Preselect: the lane's saved override if valid, else the device this column used.
        const pref = LLMAState.laneBackends ? LLMAState.laneBackends[lane] : null;
        const sel = (pref && typeof pref === 'object' && opts.some(o => o.backendId === pref.backendId && o.device === pref.device))
            ? pref
            : (opts.find(o => o.device === dev) || opts[0]);
        const optHtml = opts.map(o => {
            const val = `${o.backendId}|${o.device}`;
            const isSel = o.backendId === sel.backendId && o.device === sel.device;
            return `<option value="${llmaEscapeHtml(val)}"${isSel ? ' selected' : ''}>${llmaEscapeHtml(o.device)}</option>`;
        }).join('');
        return `${nameEl}` +
            `<select class="llma-compare-device-sel" data-lane="${lane}" title="Run this lane on a specific GPU / device (applies to the next message). Put the two lanes on different devices to generate at the same time.">${optHtml}</select>`;
    }

    /** Bind the column-header device selectors once (delegated on the message list). Changing one pins that
     *  lane to the chosen backend for the next send and persists it to session state. */
    function llmaSetupLaneDeviceSelectors() {
        const list = document.getElementById('llma-messages');
        if (!list || list.dataset.llmaLaneDevBound === '1') { return; }
        list.dataset.llmaLaneDevBound = '1';
        list.addEventListener('change', (e) => {
            const sel = e.target.closest && e.target.closest('.llma-compare-device-sel');
            if (!sel) { return; }
            const lane = parseInt(sel.dataset.lane, 10);
            const sep = String(sel.value).indexOf('|');
            if (isNaN(lane) || sep < 0) { return; }
            const backendId = parseInt(sel.value.slice(0, sep), 10);
            const device = sel.value.slice(sep + 1);
            if (isNaN(backendId)) { return; }
            LLMAState.laneBackends = LLMAState.laneBackends || {};
            LLMAState.laneBackends[lane] = { backendId, device };
            llmaSetSessionState({ laneBackends: LLMAState.laneBackends });
            llmaMaybeWarnSameDevice();
        });
    }

    /** If both lanes resolve to the same local device, they can't run at the same time (one device, one
     *  pipeline) — nudge the user to split them across devices. Fires only on an explicit device change. */
    function llmaMaybeWarnSameDevice() {
        const a = llmaLaneDeviceInfo(0, LLMAState.currentModel);
        const b = llmaLaneDeviceInfo(1, LLMAState.compareModelB);
        if (a && b && a.device === b.device && a.device !== 'cloud' && a.backendId === b.backendId) {
            if (typeof llmaShowToast === 'function') {
                llmaShowToast(`Both lanes are on ${a.device} — they'll run one after another. Put one on a different device (e.g. cpu) to generate at the same time.`, 'info');
            }
        }
    }

    /** Compare mode is "active" (will fan out) only when it's toggled on AND lane B has a model. */
    function llmaIsCompareActive() {
        return LLMAState.compareMode === true && !!LLMAState.compareModelB && !!LLMAState.currentModel;
    }

    /** Restore VS mode from the just-loaded thread so a reload lands exactly where you left off. Compare
     *  state is derived from the thread's own content (per-thread), NOT global session state: if the latest
     *  user turn fanned out to two models, re-enter compare mode with the same lane-A/B models + devices and
     *  the wide layout; otherwise leave compare off. Idempotent; safe to call on every thread load. */
    function llmaRestoreCompareFromThread() {
        const msgs = LLMAState.messages || [];
        let lastUser = null;
        for (let i = msgs.length - 1; i >= 0; i--) { if (msgs[i].role === 'user') { lastUser = msgs[i]; break; } }
        const sibs = (lastUser && typeof llmaCompareSiblings === 'function') ? llmaCompareSiblings(lastUser.id) : [];
        if (sibs.length < 2) {
            // Latest turn wasn't a comparison → make sure compare mode isn't leaking in from another thread.
            if (LLMAState.compareMode) { llmaToggleCompareMode(false, { silent: true }); }
            return;
        }
        const laneA = sibs.find(s => (s.lane ?? 0) === 0) || sibs[0];
        const laneB = sibs.find(s => (s.lane ?? 0) === 1) || sibs[1];
        // Lane A is the active model; lane B seeds the comparison pill.
        if (laneA && laneA.meta && laneA.meta.model) { LLMAState.currentModel = laneA.meta.model; }
        if (laneB && laneB.meta && laneB.meta.model) { LLMAState.compareModelB = laneB.meta.model; }
        // Restore each lane's device by resolving it against the current model options (backend id isn't
        // persisted in meta, so we look it up from the device the reply actually ran on).
        LLMAState.laneBackends = LLMAState.laneBackends || {};
        const resolve = (model, device) => {
            const opts = llmaModelDeviceOptions(model);
            return opts.find(o => o.device === device) || opts[0] || null;
        };
        if (laneA && laneA.meta) { const o = resolve(laneA.meta.model, laneA.meta.device); if (o) LLMAState.laneBackends[0] = { backendId: o.backendId, device: o.device }; }
        if (laneB && laneB.meta) { const o = resolve(laneB.meta.model, laneB.meta.device); if (o) LLMAState.laneBackends[1] = { backendId: o.backendId, device: o.device }; }
        // Reflect it in the UI (toggle active, lane-B pill visible, wide layout) without re-persisting.
        llmaToggleCompareMode(true, { silent: true });
        // Point lane A's pill at the restored model.
        const selA = document.getElementById('llma-model-select');
        if (selA && [...selA.options].some(o => o.value === LLMAState.currentModel)) {
            selA.value = LLMAState.currentModel;
            if (typeof llmaInitModelSelect2 === 'function') llmaInitModelSelect2(selA);
        }
    }

    // ---- Top-bar wiring ---------------------------------------------------------

    /** Wire the compare toggle + lane-B model select. Idempotent. Called from llmaSetupTopBar. */
    function llmaSetupCompare() {
        const toggle = document.getElementById('llma-compare-toggle');
        if (toggle && toggle.dataset.llmaBound !== '1') {
            toggle.dataset.llmaBound = '1';
            toggle.addEventListener('click', () => llmaToggleCompareMode(!LLMAState.compareMode));
        }
        const selB = document.getElementById('llma-model-select-b');
        if (selB && selB.dataset.llmaBound !== '1') {
            selB.dataset.llmaBound = '1';
            selB.addEventListener('change', () => {
                LLMAState.compareModelB = selB.value || null;
                llmaSetSessionState({ compareModelB: LLMAState.compareModelB });
            });
        }
        // Restore persisted state (init applied session state onto LLMAState before this runs).
        if (LLMAState.compareMode) {
            llmaToggleCompareMode(true, { silent: true });
        }
        llmaSetupLaneDeviceSelectors();
    }

    /** Show/hide lane B + reflect the toggle/body state. Persists to session state. */
    function llmaToggleCompareMode(on, opts = {}) {
        LLMAState.compareMode = !!on;
        const toggle = document.getElementById('llma-compare-toggle');
        const wrap   = document.getElementById('llma-model-select-b-wrap');
        if (toggle) toggle.classList.toggle('active', LLMAState.compareMode);
        if (wrap)   wrap.style.display = LLMAState.compareMode ? '' : 'none';
        const container = document.getElementById('llma-container');
        if (container) container.classList.toggle('llma-compare-on', LLMAState.compareMode);
        if (LLMAState.compareMode) {
            llmaPopulateCompareSelect();
        }
        if (!opts.silent) {
            llmaSetSessionState({ compareMode: LLMAState.compareMode });
        }
    }

    /** Fill the lane-B select from the loaded model list (grouped by provider, device in the label).
     *  Also stamps device sublabels onto lane A's options so both pills read "model · device". */
    function llmaPopulateCompareSelect() {
        const models = LLMAState.availableModels || [];
        const selB = document.getElementById('llma-model-select-b');
        if (!selB || models.length === 0) return;
        const byProvider = {};
        for (const m of models) {
            const prov = m.provider || 'unknown';
            (byProvider[prov] = byProvider[prov] || []).push(m);
        }
        const optionFor = (m) => {
            const dev = (m.metadata && m.metadata.device) ? m.metadata.device : '';
            const base = llmaEscapeHtml(m.name || m.id);
            const label = dev ? `${base} · ${dev}` : base;
            return `<option value="${llmaEscapeHtml(m.id)}" data-device="${llmaEscapeHtml(dev || 'cloud')}">${label}</option>`;
        };
        const provKeys = Object.keys(byProvider);
        const html = provKeys.length === 1
            ? models.map(optionFor).join('')
            : provKeys.map(p => `<optgroup label="${llmaEscapeHtml(p)}">${byProvider[p].map(optionFor).join('')}</optgroup>`).join('');
        selB.innerHTML = html;
        // Default lane B to a DIFFERENT model than lane A when possible (comparison is pointless otherwise).
        let target = LLMAState.compareModelB;
        if (!target || !models.some(m => m.id === target)) {
            const other = models.find(m => m.id !== LLMAState.currentModel);
            target = (other || models[0]).id;
        }
        selB.value = target;
        LLMAState.compareModelB = selB.value;
        // Upgrade lane B to the same searchable select2 dropdown as lane A (re-init-safe).
        if (typeof llmaInitModelSelect2 === 'function') llmaInitModelSelect2(selB);
    }

    /** Called after the model list (re)loads, so lane B stays populated and current. */
    function llmaCompareOnModelsLoaded() {
        if (LLMAState.compareMode) llmaPopulateCompareSelect();
    }

    // ---- Sending / streaming ----------------------------------------------------

    /** Stream one prompt to two models over a single multiplexed socket. `payload` is the fully-built
     *  send payload from llmaSendMessage (threadId, message, userMessageId, params, media). */
    function llmaStreamCompare(payload, userMsgId) {
        const laneModels = [LLMAState.currentModel, LLMAState.compareModelB];
        // Resolve each lane's device/backend, honoring any per-lane override from the header dropdown.
        const lanes = laneModels.map((model, i) => {
            const info = llmaLaneDeviceInfo(i, model);
            return { model, device: info.device, backendId: info.backendId };
        });
        // Per-lane client message ids + state nodes (siblings of the user message, shared groupId).
        lanes.forEach((L, i) => {
            L.lane = i;
            L.msgId = llmaGenerateId();
            L.node = {
                id: L.msgId, role: 'assistant', content: '',
                timestamp: new Date().toISOString(), toolCalls: [],
                parentId: userMsgId, groupId: userMsgId, lane: i,
                meta: { model: L.model, device: L.device, backendId: L.backendId },
            };
            (LLMAState.allNodes = LLMAState.allNodes || []).push(L.node);
        });
        // Lane 0 is the default active leaf (the server agrees) so the thread has a definite path.
        LLMAState.messages.push(lanes[0].node);
        LLMAState.activeLeafId = lanes[0].msgId;

        // Build the columns and grab each lane's bubble.
        const cols = llmaAppendCompareGroupToDOM(userMsgId, lanes);
        cols.forEach((c, i) => { lanes[i].bubble = c.bubble; lanes[i].col = c.col; });

        // Wire the WS payload: models[] replaces the single model field.
        const wsPayload = Object.assign({}, payload);
        delete wsPayload.model;
        wsPayload.models = lanes.map(L => ({ model: L.model, device: L.device, backendId: L.backendId, assistantMessageId: L.msgId }));

        llmaSetStreaming(true);
        LLMAState._streamingLanes = lanes;
        const startTime = performance.now();
        let doneCount = 0;

        // One controller per lane handles its own column's incremental render.
        const ctls = lanes.map(L => llmaMakeLaneController(L, startTime));

        const finalizeGroup = () => {
            LLMAState._activeSocket = null;
            LLMAState._streamingLanes = null;
            llmaSetStreaming(false);
            if (typeof llmaRebuildAssetsForThread === 'function') llmaRebuildAssetsForThread();
            if (typeof llmaSaveActiveThread === 'function') llmaSaveActiveThread();
            if (typeof llmaUpdateContextBar === 'function') llmaUpdateContextBar();
            if (typeof llmaUpdatePanelStats === 'function') llmaUpdatePanelStats();
            if (typeof llmaRefreshExactTotalTokens === 'function') llmaRefreshExactTotalTokens();
        };

        try {
            LLMAState._activeSocket = makeWSRequest('LLMAssistantSendMessageWS', wsPayload, data => {
                // Every compare event is lane-tagged; route to the right column. Default to lane 0 for safety.
                const lane = (typeof data.lane === 'number' && data.lane >= 0 && data.lane < ctls.length) ? data.lane : 0;
                const ctl = ctls[lane];
                if (!ctl || ctl.isDone()) {
                    if (data.error) ctl && ctl.fail(data.error);
                    return;
                }
                if (data.error) { ctl.fail(data.error); if (++doneCount >= ctls.length) finalizeGroup(); return; }
                if (data.status) { ctl.status(data); return; }
                if (data.chunk)  { ctl.chunk(data.chunk); }
                if (data.iteration !== undefined) ctl.newIteration();
                if (data.tool_call)   ctl.toolCall(data.tool_call);
                if (data.tool_result) ctl.toolResult(data.tool_result);
                if (data.done) { ctl.done(data); if (++doneCount >= ctls.length) finalizeGroup(); }
            }, 0, err => {
                // Socket-level failure kills every still-open lane.
                ctls.forEach(c => { if (!c.isDone()) c.fail(err || 'WebSocket error'); });
                LLMAState._activeSocket = null;
                LLMAState._streamingLanes = null;
                llmaSetStreaming(false);
            });
        } catch (ex) {
            ctls.forEach(c => { if (!c.isDone()) c.fail(ex && ex.message ? ex.message : String(ex)); });
            llmaSetStreaming(false);
        }
    }

    /** Builds the incremental-render controller for one lane's column. Mirrors the single-stream render
     *  machinery (throttled markdown, tool bubbles, final asset-swapped render) scoped to one bubble. */
    function llmaMakeLaneController(L, startTime) {
        const bubble = L.bubble;
        let firstChunk = true;
        let done = false;
        const renderer = llmaMakeStreamRenderer(() => bubble);

        return {
            isDone: () => done,
            status(data) {
                if (data.status === 'loading_model') llmaShowLoadStatusInBubble(bubble, `Loading model ${data.model || ''}…`);
                else if (data.status === 'model_ready') llmaShowLoadStatusInBubble(bubble, null);
            },
            chunk(text) {
                if (firstChunk) {
                    firstChunk = false;
                    bubble && bubble.querySelector('.llma-typing')?.remove();
                    llmaShowLoadStatusInBubble(bubble, null);
                }
                renderer.appendChunk(text);
                L.node.content = renderer.getRawText();
                if (typeof llmaScrollToBottom === 'function') llmaScrollToBottom();
            },
            newIteration() { renderer.breakSegment(); },
            toolCall(tc) {
                renderer.breakSegment();
                L.node.toolCalls = L.node.toolCalls || [];
                L.node.toolCalls.push({ id: tc.id, name: tc.name, arguments: tc.arguments, result: null });
                if (bubble && typeof llmaRenderToolCall === 'function') llmaRenderToolCall(bubble, tc);
            },
            toolResult(tr) {
                const entry = (L.node.toolCalls || []).find(t => t.id === tr.id);
                if (entry) entry.result = tr.result;
                if (bubble && typeof llmaRenderToolResult === 'function') llmaRenderToolResult(bubble, tr);
            },
            done(data) {
                if (done) return;
                done = true;
                renderer.cancelPendingRender();
                const elapsed = ((performance.now() - startTime) / 1000).toFixed(1);
                const rawText = renderer.getRawText();
                const fullRaw = data.full_text || rawText;
                const cleanFull = (typeof llmaStripToolTags === 'function') ? llmaStripToolTags(fullRaw) : fullRaw;
                L.node.content = cleanFull;
                L.node.rawContent = fullRaw;
                L.node.meta = Object.assign({}, L.node.meta, { genTime: elapsed, model: L.model, device: L.device });
                if (data.truncated) L.node.meta.truncated = true;
                if (data.reason) L.node.meta.reason = data.reason;
                if (data.stopReason) L.node.meta.stopReason = data.stopReason;
                const tc = renderer.ensureTextContainer();
                if (tc) {
                    tc.innerHTML = (typeof llmaRenderAssistantContent === 'function')
                        ? llmaRenderAssistantContent(llmaStripToolTags(rawText), L.msgId)
                        : llmaRenderMarkdown(rawText);
                    if (typeof llmaPostRenderMermaid === 'function') llmaPostRenderMermaid(tc);
                }
                bubble && bubble.querySelector('.llma-typing')?.remove();
                const metaEl = L.col && L.col.querySelector('.llma-msg-meta');
                if (metaEl) {
                    const devLabel = (L.device && L.device !== 'cloud') ? `${L.device} · ` : '';
                    metaEl.textContent = `${devLabel}${elapsed}s${data.truncated ? ' · truncated' : ''}${data.stopReason === 'length' ? ' · cut off (max tokens)' : ''}`;
                    metaEl.classList.toggle('llma-msg-meta-truncated', data.stopReason === 'length');
                }
            },
            fail(err) {
                if (done) return;
                done = true;
                renderer.cancelPendingRender();
                bubble && bubble.querySelector('.llma-typing')?.remove();
                if (typeof llmaShowErrorInBubble === 'function') llmaShowErrorInBubble(bubble, err);
            },
        };
    }

    /** Persist-on-stop for compare lanes: keep whatever each lane streamed. Returns true if it handled a
     *  compare turn (so the single-stream stop path is skipped). */
    function llmaCompareHandleStop() {
        const lanes = LLMAState._streamingLanes;
        if (!lanes || !lanes.length) return false;
        for (const L of lanes) {
            if (L.node && L.bubble) {
                L.bubble.querySelector('.llma-typing')?.remove();
            }
        }
        LLMAState._streamingLanes = null;
        if (typeof llmaSaveActiveThread === 'function') llmaSaveActiveThread();
        return true;
    }

    // ---- DOM: live columns ------------------------------------------------------

    /** Appends a compare row (the column shell) to the message list and returns per-lane refs. */
    function llmaAppendCompareGroupToDOM(userMsgId, lanes) {
        const container = document.getElementById('llma-messages');
        if (!container) return [];
        const row = document.createElement('div');
        row.className = 'llma-compare-row';
        row.dataset.groupId = userMsgId;
        const refs = [];
        for (const L of lanes) {
            const col = document.createElement('div');
            col.className = 'llma-compare-col';
            col.dataset.msgId = L.msgId;
            col.dataset.lane = L.lane;
            col.innerHTML =
                `<div class="llma-compare-head">` +
                    llmaLaneHeadHtml(L.model, L.device, L.lane) +
                `</div>` +
                `<div class="llma-msg-body">` +
                    `<div class="llma-msg-bubble ai-bubble"><div class="llma-typing"><span></span><span></span><span></span></div></div>` +
                    `<div class="llma-msg-meta"></div>` +
                `</div>`;
            const body = col.querySelector('.llma-msg-body');
            body.appendChild(llmaBuildCompareActions(L.msgId));
            row.appendChild(col);
            refs.push({ lane: L.lane, col, bubble: col.querySelector('.llma-msg-bubble'), msgId: L.msgId });
        }
        container.appendChild(row);
        if (typeof llmaScrollToBottom === 'function') llmaScrollToBottom();
        return refs;
    }

    /** Per-column action bar: Copy · Use as Prompt · Keep this one · Del. Reuses the shared
     *  llmaCreateActionBtn helper (same .llma-msg-action-btn element the normal message actions use). */
    function llmaBuildCompareActions(msgId) {
        const actions = document.createElement('div');
        actions.className = 'llma-msg-actions';
        actions.appendChild(llmaCreateActionBtn('Copy', () => { if (typeof llmaCopyMessage === 'function') llmaCopyMessage(msgId); }));
        actions.appendChild(llmaCreateActionBtn('Use as Prompt', () => {
            const m = (LLMAState.allNodes || []).find(x => x.id === msgId);
            if (m && typeof llmaSendToPromptBox === 'function') llmaSendToPromptBox(m.content);
        }));
        actions.appendChild(llmaCreateActionBtn('Keep this one', () => llmaKeepLane(msgId)));
        actions.appendChild(llmaCreateActionBtn('Del', () => { if (typeof llmaDeleteMessage === 'function') llmaDeleteMessage(msgId); }));
        return actions;
    }

    /** Converge: make this lane's reply the active path and exit compare mode. The comparison turn stays
     *  visible as columns in history; new messages continue single-model from here. */
    function llmaKeepLane(msgId) {
        llmaToggleCompareMode(false);
        if (typeof llmaSetActiveLeaf === 'function') {
            llmaSetActiveLeaf(msgId, { focusInput: true });
        }
        const m = (LLMAState.allNodes || []).find(x => x.id === msgId);
        const model = m && m.meta && m.meta.model;
        if (model) {
            LLMAState.currentModel = model;
            const selA = document.getElementById('llma-model-select');
            if (selA && [...selA.options].some(o => o.value === model)) {
                selA.value = model;
                // Refresh the select2 display to the kept model (native value set alone won't update it).
                if (typeof llmaInitModelSelect2 === 'function') llmaInitModelSelect2(selA);
            }
            llmaSetSessionState({ currentModel: model });
        }
        llmaShowToast(`Kept ${model ? llmaModelName(model) : 'this reply'} — continuing single-model.`, 'info');
    }

    // ---- DOM: history (thread load / branch switch / converge re-render) ---------

    /** All compare-group siblings (assistant replies sharing a groupId) under a user message, in lane order. */
    function llmaCompareSiblings(userMsgId) {
        const sibs = (LLMAState.allNodes || []).filter(n => n.groupId && n.groupId === userMsgId && n.role === 'assistant');
        return sibs.sort((a, b) => (a.lane ?? 0) - (b.lane ?? 0));
    }

    /** Renders a finished compare group (read-only columns) under an already-rendered user message. */
    function llmaRenderCompareGroupHistory(userMsgId) {
        const sibs = llmaCompareSiblings(userMsgId);
        if (sibs.length < 2) return null;
        const container = document.getElementById('llma-messages');
        if (!container) return null;
        const row = document.createElement('div');
        row.className = 'llma-compare-row';
        row.dataset.groupId = userMsgId;
        for (const node of sibs) {
            const model = (node.meta && node.meta.model) || '';
            const dev = (node.meta && node.meta.device) || '';
            const label = dev && dev !== 'cloud' ? `${llmaModelName(model)} · ${dev}` : llmaModelName(model);
            const col = document.createElement('div');
            col.className = 'llma-compare-col';
            col.dataset.msgId = node.id;
            col.dataset.lane = node.lane ?? 0;
            col.innerHTML =
                `<div class="llma-compare-head">` +
                    llmaLaneHeadHtml(model, dev, node.lane ?? 0) +
                `</div>` +
                `<div class="llma-msg-body">` +
                    `<div class="llma-msg-bubble ai-bubble"></div>` +
                    `<div class="llma-msg-meta"></div>` +
                `</div>`;
            const bubble = col.querySelector('.llma-msg-bubble');
            bubble.innerHTML = (typeof llmaRenderAssistantContent === 'function')
                ? llmaRenderAssistantContent(node.content || '', node.id)
                : llmaRenderMarkdown(node.content || '');
            if (typeof llmaPostRenderMermaid === 'function') llmaPostRenderMermaid(bubble);
            if (Array.isArray(node.toolCalls) && node.toolCalls.length && typeof llmaReplayToolCalls === 'function') {
                llmaReplayToolCalls(bubble, node.toolCalls);
            }
            const metaEl = col.querySelector('.llma-msg-meta');
            if (metaEl) {
                const cutOff = node.meta && node.meta.stopReason === 'length';
                metaEl.textContent = `${label}${node.meta && node.meta.genTime ? ` · ${node.meta.genTime}s` : ''}${cutOff ? ' · cut off (max tokens)' : ''}`;
                metaEl.classList.toggle('llma-msg-meta-truncated', !!cutOff);
            }
            col.querySelector('.llma-msg-body').appendChild(llmaBuildCompareActions(node.id));
            row.appendChild(col);
        }
        container.appendChild(row);
        return row;
    }

    // --- Public API ---
    window.llmaSetupCompare              = llmaSetupCompare;
    window.llmaToggleCompareMode         = llmaToggleCompareMode;
    window.llmaIsCompareActive           = llmaIsCompareActive;
    window.llmaStreamCompare             = llmaStreamCompare;
    window.llmaCompareHandleStop         = llmaCompareHandleStop;
    window.llmaCompareOnModelsLoaded     = llmaCompareOnModelsLoaded;
    window.llmaCompareSiblings           = llmaCompareSiblings;
    window.llmaRenderCompareGroupHistory = llmaRenderCompareGroupHistory;
    window.llmaRestoreCompareFromThread  = llmaRestoreCompareFromThread;
    window.llmaGetModelDevice            = llmaGetModelDevice;
})();

/* ================================================================
   LLM Assistant - chat/chat-toolcalls.js
   Inline tool-call / tool-result rendering, retry, and replay.
   ================================================================ */

(function () {
    'use strict';

    /** Strip <tool_call>/<tool_result> markup from raw model text for clean display/persistence. */
    function llmaStripToolTags(text) {
        if (!text) return '';
        return text
            .replace(/<tool_call>[\s\S]*?<\/tool_call>/g, '')
            .replace(/<tool_result\b[^>]*>[\s\S]*?<\/tool_result>/g, '')
            .trim();
    }

    /** "generate_image" → "Generate Image". File-private. */
    function llmaFormatToolName(name) {
        if (!name) return '';
        return name.replace(/_/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
    }

    /** Verb phrases per tool: [singular activity, plural noun used in the group summary]. */
    const LLMA_TOOL_PHRASES = {
        generate_image: ['Generated an image', 'generated {n} images'],
        web_search: ['Searched the web', 'searched the web {n} times'],
        file_read: ['Read a file', 'read {n} files'],
        file_write: ['Wrote a file', 'wrote {n} files'],
        shell_exec: ['Ran a command', 'ran {n} commands'],
        http_request: ['Made a request', 'made {n} requests'],
    };

    /** Short activity title for one tool call ("Ran a command", or "Used Foo Bar" for unknown tools). */
    function llmaToolActivity(name) {
        const p = LLMA_TOOL_PHRASES[name];
        return p ? translate(p[0]) : `${translate('Used')} ${llmaFormatToolName(name)}`;
    }

    /**
     * Find (or create) the collapsed activity group that the next tool call in `bubble` belongs to.
     * Consecutive tool calls (ignoring empty text segments between them) share one group; text in
     * between starts a new one.
     */
    function llmaToolGroupFor(bubble) {
        let last = bubble.lastElementChild;
        while (last && last.classList.contains('llma-msg-text-segment') && !last.textContent.trim()) {
            last = last.previousElementSibling;
        }
        if (last && last.classList.contains('llma-tool-group')) {
            return last;
        }
        const group = document.createElement('div');
        group.className = 'llma-tool-group collapsed';

        const head = document.createElement('button');
        head.type = 'button';
        head.className = 'llma-tool-group-head';
        head.setAttribute('aria-expanded', 'false');
        head.innerHTML = '<span class="llma-spinner" aria-hidden="true"></span>'
            + '<span class="llma-tool-group-summary"></span>'
            + '<span class="llma-tool-group-chev" aria-hidden="true">›</span>';
        head.addEventListener('click', () => {
            let collapsed = group.classList.toggle('collapsed');
            head.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
        });

        const body = document.createElement('div');
        body.className = 'llma-tool-group-body';
        group.append(head, body);
        bubble.appendChild(group);
        return group;
    }

    /** Refresh a group's summary line ("Ran 2 commands, read a file"), and running state. */
    function llmaUpdateToolGroup(group) {
        if (!group) {
            return;
        }
        let calls = Array.from(group.querySelectorAll('.llma-tool-call-bubble'));
        let counts = {};
        let order = [];
        for (let c of calls) {
            let n = c.getAttribute('data-tool-name') || '';
            if (!(n in counts)) {
                counts[n] = 0;
                order.push(n);
            }
            counts[n]++;
        }
        let parts = [];
        for (let n of order) {
            let p = LLMA_TOOL_PHRASES[n];
            if (!p) {
                parts.push(counts[n] == 1 ? `${translate('used')} ${llmaFormatToolName(n)}` : `${translate('used')} ${llmaFormatToolName(n)} ×${counts[n]}`);
            }
            else if (counts[n] == 1) {
                parts.push(translate(p[0]).charAt(0).toLowerCase() + translate(p[0]).slice(1));
            }
            else {
                parts.push(translate(p[1]).replace('{n}', counts[n]));
            }
        }
        let text = parts.join(', ');
        text = text.charAt(0).toUpperCase() + text.slice(1);
        let pending = group.querySelector('.llma-tool-call-bubble.pending');
        let failed = group.querySelector('.llma-tool-call-bubble.error');
        let summary = group.querySelector('.llma-tool-group-summary');
        summary.textContent = pending ? `${text}…` : text;
        // A lone step needs no row of its own: its details show straight under the summary line.
        group.classList.toggle('single', calls.length == 1);
        group.classList.toggle('running', !!pending);
        group.classList.toggle('has-error', !!failed && !pending);
    }

    /** Append a pending tool-call bubble (header + args, collapsible) under an assistant bubble. */
    function llmaRenderToolCall(bubble, call) {
        if (!bubble || !call) return;
        const wrap = document.createElement('div');
        wrap.className = 'llma-tool-call-bubble pending collapsed';
        wrap.setAttribute('data-tool-id', call.id || '');
        // Persist the tool name + args on the bubble so the retry button can re-issue the same call
        // without round-tripping through the message history (which the user might have edited).
        wrap.setAttribute('data-tool-name', call.name || '');
        try { wrap.setAttribute('data-tool-args', JSON.stringify(call.arguments ?? {})); }
        catch { wrap.setAttribute('data-tool-args', '{}'); }

        const header = document.createElement('div');
        header.className = 'llma-tool-call-header';

        const title = document.createElement('span');
        title.className = 'llma-tool-call-title';
        title.textContent = llmaToolActivity(call.name);
        header.appendChild(title);

        const status = document.createElement('span');
        status.className = 'llma-tool-call-status';
        // Spinner stays in the DOM next to the status text while the call is pending.
        // The tool-result handler swaps the bubble class from .pending to .success/.error,
        // which hides the spinner via a CSS rule keyed on the parent .pending state.
        status.innerHTML = `<span class="llma-spinner" aria-hidden="true"></span><span>${translate('running…')}</span>`;
        header.appendChild(status);

        const toggle = document.createElement('button');
        toggle.className = 'llma-tool-call-toggle';
        toggle.textContent = '▸'; // right-arrow (rows start collapsed)
        toggle.title = translate('Toggle details');
        toggle.setAttribute('aria-expanded', 'false');
        toggle.setAttribute('aria-label', translate('Toggle tool call details'));
        header.appendChild(toggle);

        const body = document.createElement('div');
        body.className = 'llma-tool-call-body';

        const argsLabel = document.createElement('div');
        argsLabel.className = 'llma-tool-call-label';
        argsLabel.textContent = translate('Arguments');
        body.appendChild(argsLabel);

        const argsPre = document.createElement('pre');
        argsPre.className = 'llma-tool-call-args';
        try {
            argsPre.textContent = JSON.stringify(call.arguments ?? {}, null, 2);
        } catch (_) {
            argsPre.textContent = String(call.arguments ?? '');
        }
        body.appendChild(argsPre);

        const resultSlot = document.createElement('div');
        resultSlot.className = 'llma-tool-result-slot';
        body.appendChild(resultSlot);

        header.addEventListener('click', () => {
            const collapsed = wrap.classList.toggle('collapsed');
            toggle.textContent = collapsed ? '▸' : '▾';
            toggle.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
        });

        wrap.appendChild(header);
        wrap.appendChild(body);
        const group = llmaToolGroupFor(bubble);
        group.querySelector('.llma-tool-group-body').appendChild(wrap);
        llmaUpdateToolGroup(group);
    }

    /** Insert (once) the inline preview card for a generated image right after its activity group. */
    function llmaShowToolImageCard(bubble, callBubble, toolResult, result) {
        const msgEl = bubble.closest?.('[data-msg-id]');
        const msgId = msgEl ? msgEl.getAttribute('data-msg-id') : null;
        const group = callBubble?.closest('.llma-tool-group');
        if (!msgId || !group || typeof llmaRenderAssetCardHtml != 'function') {
            return;
        }
        if (typeof llmaRebuildAssetsForThread == 'function') {
            llmaRebuildAssetsForThread();
        }
        const asset = (LLMAState.assets || []).find(a => a.id == `${msgId}-tool-${toolResult.id}`);
        if (!asset || group.querySelector(`[data-asset-id="${CSS.escape(asset.id)}"]`)) {
            return;
        }
        // Cards live after the group (one per image); keep them in call order.
        let anchor = group;
        while (anchor.nextElementSibling?.classList.contains('llma-tool-image-card')) {
            anchor = anchor.nextElementSibling;
        }
        const holder = document.createElement('div');
        holder.className = 'llma-tool-image-card';
        holder.innerHTML = llmaRenderAssetCardHtml(asset, { inline: true });
        anchor.after(holder);
    }

    /** Fill the result slot of a tool-call bubble with a tool-specific preview (or JSON fallback). */
    function llmaRenderToolResult(bubble, toolResult) {
        if (!bubble || !toolResult) return;
        const callBubble = bubble.querySelector(`.llma-tool-call-bubble[data-tool-id="${toolResult.id}"]`);
        const result = toolResult.result || {};
        const success = result.success !== false && !result.error;

        const target = callBubble || bubble;
        if (callBubble) {
            callBubble.classList.remove('pending');
            callBubble.classList.add(success ? 'success' : 'error');
            const status = callBubble.querySelector('.llma-tool-call-status');
            if (status) status.textContent = success ? translate('done') : translate('error');
            if (!success) {
                // Surface failures: open the step and its group so the error isn't hidden.
                callBubble.classList.remove('collapsed');
                callBubble.closest('.llma-tool-group')?.classList.remove('collapsed');
            }
        }

        const slot = callBubble
            ? callBubble.querySelector('.llma-tool-result-slot')
            : (() => {
                const s = document.createElement('div');
                s.className = 'llma-tool-result-slot standalone';
                target.appendChild(s);
                return s;
              })();

        slot.innerHTML = '';
        const resultWrap = document.createElement('div');
        resultWrap.className = `llma-tool-result-bubble ${success ? 'success' : 'error'}`;

        const resultLabel = document.createElement('div');
        resultLabel.className = 'llma-tool-call-label';
        resultLabel.textContent = success ? translate('Result') : translate('Error');
        resultWrap.appendChild(resultLabel);

        // Tool-specific previews
        const name = toolResult.name || '';
        if (success && name === 'generate_image' && result.imageUrl) {
            // The image itself shows as an inline preview card under the activity line, so it stays
            // visible while the group is collapsed; the step keeps just the prompt.
            llmaShowToolImageCard(bubble, callBubble, toolResult, result);
            if (result.prompt) {
                const caption = document.createElement('div');
                caption.className = 'llma-tool-result-caption';
                caption.textContent = result.prompt;
                resultWrap.appendChild(caption);
            }
        } else if (success && name === 'web_search' && Array.isArray(result.results)) {
            const list = document.createElement('ul');
            list.className = 'llma-tool-result-search';
            for (const item of result.results) {
                const li = document.createElement('li');
                const a = document.createElement('a');
                a.href = item.url || '#';
                a.target = '_blank';
                a.rel = 'noopener noreferrer';
                a.textContent = item.title || item.url || translate('(untitled)');
                li.appendChild(a);
                if (item.snippet) {
                    const sn = document.createElement('div');
                    sn.className = 'llma-tool-result-snippet';
                    sn.textContent = item.snippet;
                    li.appendChild(sn);
                }
                list.appendChild(li);
            }
            resultWrap.appendChild(list);
        } else if (success && name === 'file_read' && typeof result.content === 'string') {
            const info = document.createElement('div');
            info.className = 'llma-tool-result-fileinfo';
            info.textContent = `${result.path || ''} (${result.bytesRead ?? result.size ?? '?'} ${translate('bytes')}${result.truncated ? ', ' + translate('truncated') : ''})`;
            resultWrap.appendChild(info);
            const pre = document.createElement('pre');
            pre.className = 'llma-tool-result-filecontent';
            pre.textContent = result.content;
            resultWrap.appendChild(pre);
        } else if (success && name === 'file_write') {
            const info = document.createElement('div');
            info.className = 'llma-tool-result-fileinfo';
            info.textContent = `${result.path || ''} (${result.bytesWritten ?? '?'} ${translate('bytes')})`;
            resultWrap.appendChild(info);

            if (result.url) {
                const linkWrap = document.createElement('div');
                linkWrap.className = 'llma-tool-result-filelink';
                const a = document.createElement('a');
                a.href = result.url;
                a.target = '_blank';
                a.rel = 'noopener noreferrer';
                a.textContent = result.url;
                a.addEventListener('click', (e) => {
                    // Left-click opens the in-tab artifact viewer; allow normal browser behavior
                    // for ctrl/cmd-click, middle-click, etc.
                    if (e.button != 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) {
                        return;
                    }
                    if (typeof llmaOpenAsset === 'function') {
                        e.preventDefault();
                        llmaRebuildAssetsForThread?.();
                        const msgEl = bubble.closest?.('[data-msg-id]');
                        const msgId = msgEl ? msgEl.getAttribute('data-msg-id') : null;
                        const assetId = msgId ? `${msgId}-tool-${toolResult.id}` : null;
                        if (assetId) {
                            llmaOpenAsset(assetId);
                        }
                    }
                });
                linkWrap.appendChild(a);
                resultWrap.appendChild(linkWrap);
            }
        } else if (success && name === 'http_request') {
            const info = document.createElement('div');
            info.className = 'llma-tool-result-fileinfo';
            const statusClass = result.ok ? 'ok' : 'err';
            info.innerHTML = `<span class="llma-http-status ${statusClass}">${result.status} ${llmaEscapeHtml(result.statusText || '')}</span> <span class="llma-http-method">${llmaEscapeHtml(result.method || '')}</span> ${llmaEscapeHtml(result.url || '')}${result.truncated ? ' (' + translate('truncated') + ')' : ''}`;
            resultWrap.appendChild(info);
            if (typeof result.body === 'string' && result.body.length) {
                const pre = document.createElement('pre');
                pre.className = 'llma-tool-result-filecontent';
                pre.textContent = result.body.length > 4000 ? result.body.slice(0, 4000) + '\n…' : result.body;
                resultWrap.appendChild(pre);
            }
        } else if (name === 'shell_exec') {
            const info = document.createElement('div');
            info.className = 'llma-tool-result-fileinfo';
            const exitTxt = result.killed ? translate('killed (timeout)') : `${translate('exit')} ${result.exitCode}`;
            info.textContent = `$ ${result.command || ''}  [${exitTxt}${result.truncated ? ', ' + translate('truncated') : ''}]`;
            resultWrap.appendChild(info);
            if (typeof result.stdout === 'string' && result.stdout.length) {
                const pre = document.createElement('pre');
                pre.className = 'llma-tool-result-filecontent';
                pre.textContent = result.stdout;
                resultWrap.appendChild(pre);
            }
            if (typeof result.stderr === 'string' && result.stderr.length) {
                const label = document.createElement('div');
                label.className = 'llma-tool-call-label';
                label.textContent = translate('stderr');
                resultWrap.appendChild(label);
                const pre = document.createElement('pre');
                pre.className = 'llma-tool-result-filecontent';
                pre.textContent = result.stderr;
                resultWrap.appendChild(pre);
            }
        } else if (!success && typeof result.error == 'string' && result.error) {
            // Failures: lead with the message; the raw payload is rarely more useful.
            const errText = document.createElement('div');
            errText.className = 'llma-tool-result-errtext';
            errText.textContent = result.error;
            resultWrap.appendChild(errText);
        } else {
            // Generic JSON fallback
            const pre = document.createElement('pre');
            pre.className = 'llma-tool-result-json';
            try {
                pre.textContent = JSON.stringify(result, null, 2);
            } catch (_) {
                pre.textContent = String(result);
            }
            resultWrap.appendChild(pre);
        }

        // Retry affordance — only when the call failed AND the wrapper has the original args.
        // Re-runs via LLMAssistantExecuteTool (the standalone tool runner) and shows the new
        // result in place. Does NOT amend the conversation history server-side — purely a manual
        // "did the tool start working?" check the user can fire without re-prompting the LLM.
        if (!success && callBubble) {
            const retryBtn = document.createElement('button');
            retryBtn.className = 'basic-button llma-tool-retry-btn';
            retryBtn.type = 'button';
            retryBtn.textContent = translate('Retry');
            retryBtn.title = translate('Re-run this tool with the same arguments. Does not affect the chat history.');
            retryBtn.addEventListener('click', () => llmaRetryToolCall(callBubble, retryBtn));
            resultWrap.appendChild(retryBtn);
        }

        slot.appendChild(resultWrap);
        llmaUpdateToolGroup(callBubble?.closest('.llma-tool-group'));
    }

    /**
     * Re-run a tool call standalone. Doesn't mutate the saved thread — the original tool result
     * stays in the LLM's view of history. Purely a user-facing "did the tool start working?" check
     * after, eg, fixing a config / restoring network access. File-private.
     */
    async function llmaRetryToolCall(callBubble, button) {
        const toolName = callBubble.getAttribute('data-tool-name');
        let args = {};
        try { args = JSON.parse(callBubble.getAttribute('data-tool-args') || '{}'); }
        catch { /* leave empty — server will error and we'll display that */ }
        if (button) { button.disabled = true; button.textContent = translate('Retrying…'); }
        const result = await llmaUserAction(
            () => llmaRequest('LLMAssistantExecuteTool', { toolId: toolName, arguments: JSON.stringify(args) }),
            translate('Retry failed')
        );
        if (button) { button.disabled = false; button.textContent = translate('Retry'); }
        if (!result) return;
        // Surface success/failure as a toast — the original error bubble stays put (it's part of
        // the persisted transcript) but the user gets confirmation of the new attempt's outcome.
        const innerResult = result.result || result;
        const ok = innerResult?.success !== false && !innerResult?.error;
        if (ok) {
            llmaShowToast(`${toolName} ${translate('succeeded on retry.')}`, 'info');
        } else {
            llmaShowToast(`${toolName} ${translate('still failing:')} ${innerResult?.error || 'unknown error'}`, 'error');
        }
    }

    /** Replay stored tool calls + results for an assistant message (used on thread reload). */
    function llmaReplayToolCalls(bubble, toolCalls) {
        if (!bubble || !Array.isArray(toolCalls)) return;
        for (const tc of toolCalls) {
            llmaRenderToolCall(bubble, { id: tc.id, name: tc.name, arguments: tc.arguments });
            if (tc.result !== null && tc.result !== undefined) {
                llmaRenderToolResult(bubble, { id: tc.id, name: tc.name, result: tc.result });
            }
        }
        // History keeps the reply text and the calls separately, so replay can't know where they
        // interleaved. Activity happens before the answer: put the groups ahead of the text.
        const moving = [];
        for (const g of bubble.querySelectorAll(':scope > .llma-tool-group')) {
            moving.push(g);
            let next = g.nextElementSibling;
            while (next && next.classList.contains('llma-tool-image-card')) {
                moving.push(next);
                next = next.nextElementSibling;
            }
        }
        for (let i = moving.length - 1; i >= 0; i--) {
            bubble.insertBefore(moving[i], bubble.firstChild);
        }
    }

    // --- Public API (called by sibling files + other chat/ modules) ---
    window.llmaStripToolTags    = llmaStripToolTags;
    window.llmaRenderToolCall   = llmaRenderToolCall;
    window.llmaRenderToolResult = llmaRenderToolResult;
    window.llmaReplayToolCalls  = llmaReplayToolCalls;
})();

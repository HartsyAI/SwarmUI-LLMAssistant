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

    /** Append a pending tool-call bubble (header + args, collapsible) under an assistant bubble. */
    function llmaRenderToolCall(bubble, call) {
        if (!bubble || !call) return;
        const wrap = document.createElement('div');
        wrap.className = 'llma-tool-call-bubble pending';
        wrap.setAttribute('data-tool-id', call.id || '');
        // Persist the tool name + args on the bubble so the retry button can re-issue the same call
        // without round-tripping through the message history (which the user might have edited).
        wrap.setAttribute('data-tool-name', call.name || '');
        try { wrap.setAttribute('data-tool-args', JSON.stringify(call.arguments ?? {})); }
        catch { wrap.setAttribute('data-tool-args', '{}'); }

        const header = document.createElement('div');
        header.className = 'llma-tool-call-header';

        const icon = document.createElement('span');
        icon.className = 'llma-tool-call-icon';
        icon.textContent = '⚙'; // gear
        header.appendChild(icon);

        const title = document.createElement('span');
        title.className = 'llma-tool-call-title';
        title.textContent = `Calling ${llmaFormatToolName(call.name)}`;
        header.appendChild(title);

        const status = document.createElement('span');
        status.className = 'llma-tool-call-status';
        // Spinner stays in the DOM next to the status text while the call is pending.
        // The tool-result handler swaps the bubble class from .pending to .success/.error,
        // which hides the spinner via a CSS rule keyed on the parent .pending state.
        status.innerHTML = '<span class="llma-spinner" aria-hidden="true"></span><span>running…</span>';
        header.appendChild(status);

        const toggle = document.createElement('button');
        toggle.className = 'llma-tool-call-toggle';
        toggle.textContent = '▾'; // down-arrow
        toggle.title = 'Toggle details';
        toggle.setAttribute('aria-expanded', 'true');
        toggle.setAttribute('aria-label', 'Toggle tool call details');
        header.appendChild(toggle);

        const body = document.createElement('div');
        body.className = 'llma-tool-call-body';

        const argsLabel = document.createElement('div');
        argsLabel.className = 'llma-tool-call-label';
        argsLabel.textContent = 'Arguments';
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

        toggle.addEventListener('click', () => {
            const collapsed = wrap.classList.toggle('collapsed');
            toggle.textContent = collapsed ? '▸' : '▾';
            toggle.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
        });

        wrap.appendChild(header);
        wrap.appendChild(body);
        bubble.appendChild(wrap);
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
            if (status) status.textContent = success ? 'done' : 'error';
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
        resultLabel.textContent = success ? 'Result' : 'Error';
        resultWrap.appendChild(resultLabel);

        // Tool-specific previews
        const name = toolResult.name || '';
        if (success && name === 'generate_image' && result.imageUrl) {
            const img = document.createElement('img');
            img.src = result.imageUrl;
            img.className = 'llma-tool-result-image';
            img.addEventListener('click', () => {
                if (typeof setCurrentImage === 'function') setCurrentImage(result.imageUrl);
            });
            resultWrap.appendChild(img);
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
                a.textContent = item.title || item.url || '(untitled)';
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
            info.textContent = `${result.path || ''} (${result.bytesRead ?? result.size ?? '?'} bytes${result.truncated ? ', truncated' : ''})`;
            resultWrap.appendChild(info);
            const pre = document.createElement('pre');
            pre.className = 'llma-tool-result-filecontent';
            pre.textContent = result.content;
            resultWrap.appendChild(pre);
        } else if (success && name === 'file_write') {
            const info = document.createElement('div');
            info.className = 'llma-tool-result-fileinfo';
            info.textContent = `${result.path || ''} (${result.bytesWritten ?? '?'} bytes)`;
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
            info.innerHTML = `<span class="llma-http-status ${statusClass}">${result.status} ${llmaEscapeHtml(result.statusText || '')}</span> <span class="llma-http-method">${llmaEscapeHtml(result.method || '')}</span> ${llmaEscapeHtml(result.url || '')}${result.truncated ? ' (truncated)' : ''}`;
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
            const exitTxt = result.killed ? `killed (timeout)` : `exit ${result.exitCode}`;
            info.textContent = `$ ${result.command || ''}  [${exitTxt}${result.truncated ? ', truncated' : ''}]`;
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
                label.textContent = 'stderr';
                resultWrap.appendChild(label);
                const pre = document.createElement('pre');
                pre.className = 'llma-tool-result-filecontent';
                pre.textContent = result.stderr;
                resultWrap.appendChild(pre);
            }
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
            retryBtn.textContent = 'Retry';
            retryBtn.title = 'Re-run this tool with the same arguments. Does not affect the chat history.';
            retryBtn.addEventListener('click', () => llmaRetryToolCall(callBubble, retryBtn));
            resultWrap.appendChild(retryBtn);
        }

        slot.appendChild(resultWrap);
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
        if (button) { button.disabled = true; button.textContent = 'Retrying…'; }
        const result = await llmaUserAction(
            () => llmaRequest('LLMAssistantExecuteTool', { toolId: toolName, arguments: JSON.stringify(args) }),
            'Retry failed'
        );
        if (button) { button.disabled = false; button.textContent = 'Retry'; }
        if (!result) return;
        // Surface success/failure as a toast — the original error bubble stays put (it's part of
        // the persisted transcript) but the user gets confirmation of the new attempt's outcome.
        const innerResult = result.result || result;
        const ok = innerResult?.success !== false && !innerResult?.error;
        if (ok) {
            llmaShowToast(`${toolName} succeeded on retry.`, 'info');
        } else {
            llmaShowToast(`${toolName} still failing: ${innerResult?.error || 'unknown error'}`, 'error');
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
    }

    // --- Public API (called by sibling files + other chat/ modules) ---
    window.llmaStripToolTags    = llmaStripToolTags;
    window.llmaRenderToolCall   = llmaRenderToolCall;
    window.llmaRenderToolResult = llmaRenderToolResult;
    window.llmaReplayToolCalls  = llmaReplayToolCalls;
})();

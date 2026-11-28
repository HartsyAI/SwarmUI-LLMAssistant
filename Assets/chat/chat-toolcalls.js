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

    /** Verb phrases per tool: [singular activity done, plural of it used in the group summary, the activity while it runs]. */
    const LLMA_TOOL_PHRASES = {
        generate_image: ['Generated an image', 'generated {n} images', 'Generating an image'],
        web_search: ['Searched the web', 'searched the web {n} times', 'Searching the web'],
        file_read: ['Read a file', 'read {n} files', 'Reading a file'],
        file_write: ['Wrote a file', 'wrote {n} files', 'Writing a file'],
        shell_exec: ['Ran a command', 'ran {n} commands', 'Running a command'],
        http_request: ['Made a request', 'made {n} requests', 'Making a request'],
        code_read: ['Read a file', 'read {n} files', 'Reading a file'],
        code_write: ['Wrote a file', 'wrote {n} files', 'Writing a file'],
        code_edit: ['Edited a file', 'edited {n} files', 'Editing a file'],
        code_glob: ['Listed files', 'listed files {n} times', 'Listing files'],
        code_grep: ['Searched the files', 'searched the files {n} times', 'Searching the files'],
        code_bash: ['Ran a command', 'ran {n} commands', 'Running a command'],
        code_todo: ['Updated the task list', 'updated the task list {n} times', 'Updating the task list'],
        session_start: ['Started a session', 'started {n} sessions', 'Starting a session'],
        session_send: ['Messaged a session', 'messaged sessions {n} times', 'Messaging a session'],
        session_list: ['Listed the sessions', 'listed the sessions {n} times', 'Listing the sessions'],
        session_read: ['Read a session', 'read sessions {n} times', 'Reading a session'],
        session_wait: ['Waited for a session', 'waited for sessions {n} times', 'Waiting for a session'],
        session_stop: ['Stopped a session', 'stopped {n} sessions', 'Stopping a session'],
        session_archive: ['Archived a session', 'archived {n} sessions', 'Archiving a session'],
    };

    /** What a call that did not succeed, or has not run to an end, says after its tool's name. */
    let LLMA_TOOL_OUTCOMES = { waiting: 'needs permission', declined: 'declined', failed: 'failed', stopped: 'unfinished at Stop' };

    /** Where a call's step stands: 'waiting' (for the user's permission), 'running', 'stopped', 'done', 'declined' or 'failed'. */
    function llmaToolState(callBubble) {
        if (callBubble.classList.contains('pending')) {
            return callBubble.classList.contains('waiting') ? 'waiting' : 'running';
        }
        if (callBubble.classList.contains('error')) {
            return callBubble.classList.contains('declined') ? 'declined' : 'failed';
        }
        if (callBubble.classList.contains('stopped')) {
            return 'stopped';
        }
        return 'done';
    }

    /** One or more calls of a tool in one state, as words: "Generating an image", "generated 2 images", "Generate Image needs permission". */
    function llmaToolPhrase(name, state, count) {
        let phrases = LLMA_TOOL_PHRASES[name];
        let times = count > 1 ? ` ×${count}` : '';
        if (LLMA_TOOL_OUTCOMES[state]) {
            return `${llmaFormatToolName(name)} ${translate(LLMA_TOOL_OUTCOMES[state])}${times}`;
        }
        if (!phrases) {
            return `${translate(state == 'running' ? 'Using' : 'Used')} ${llmaFormatToolName(name)}${times}`;
        }
        if (state == 'running') {
            return `${translate(phrases[2])}${times}`;
        }
        return count > 1 ? translate(phrases[1]).replace('{n}', count) : translate(phrases[0]);
    }

    /** A call's own title ("Ran a command", or "Used Foo Bar" for unknown tools), saying where it stands. */
    function llmaToolActivity(name, state) {
        return llmaToolPhrase(name, state, 1);
    }

    /**
     * Find (or create) the collapsed activity group that the next tool call in `bubble` belongs to.
     * Consecutive tool calls (ignoring empty text segments between them) share one group; text in
     * between starts a new one.
     */
    function llmaToolGroupFor(bubble) {
        let last = bubble.lastElementChild;
        while (last && (last.classList.contains('llma-tool-extra') || (last.classList.contains('llma-msg-text-segment') && !last.textContent.trim()))) {
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

    /** Refresh a group's summary line ("Ran 2 commands, read a file") and its calls' titles, and its running state. Only a call that succeeded is told as done. */
    function llmaUpdateToolGroup(group) {
        if (!group) {
            return;
        }
        let calls = Array.from(group.querySelectorAll('.llma-tool-call-bubble'));
        let kinds = new Map();
        for (let c of calls) {
            let name = c.getAttribute('data-tool-name') || '';
            let state = llmaToolState(c);
            let key = `${name} ${state}`;
            if (!kinds.has(key)) {
                kinds.set(key, { name: name, state: state, count: 0 });
            }
            kinds.get(key).count++;
            c.querySelector('.llma-tool-call-title').textContent = llmaToolActivity(name, state);
        }
        let parts = Array.from(kinds.values(), (kind, i) => {
            let part = llmaToolPhrase(kind.name, kind.state, kind.count);
            // Later parts run on in the sentence; a part that starts with a tool's name keeps its capital.
            return i > 0 && !LLMA_TOOL_OUTCOMES[kind.state] ? part.charAt(0).toLowerCase() + part.slice(1) : part;
        });
        let text = parts.join(', ');
        text = text.charAt(0).toUpperCase() + text.slice(1);
        let running = calls.some(c => llmaToolState(c) == 'running');
        let pending = group.querySelector('.llma-tool-call-bubble.pending');
        let failed = group.querySelector('.llma-tool-call-bubble.error');
        let summary = group.querySelector('.llma-tool-group-summary');
        summary.textContent = running ? `${text}…` : text;
        // A lone step needs no row of its own: its details show straight under the summary line.
        group.classList.toggle('single', calls.length == 1);
        // A call waiting for the user is not at work: no spinner for it.
        group.classList.toggle('running', running);
        group.classList.toggle('has-error', !!failed && !pending);
    }

    /** The status of a call at work: a spinner and "running…". */
    function llmaToolRunningStatus(status) {
        status.innerHTML = `<span class="llma-spinner" aria-hidden="true"></span><span>${translate('running…')}</span>`;
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
        header.appendChild(title);

        const status = document.createElement('span');
        status.className = 'llma-tool-call-status';
        // Spinner stays in the DOM next to the status text while the call is pending.
        // The tool-result handler swaps the bubble class from .pending to .success/.error,
        // which hides the spinner via a CSS rule keyed on the parent .pending state.
        llmaToolRunningStatus(status);
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
        // It scrolls when long, so the keyboard must be able to reach it.
        argsPre.tabIndex = 0;
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

    /**
     * A tool call is waiting for the user's approval: open its step and show Allow once / Allow for this chat / Deny under its arguments.
     * The answer goes to the server on its own request; the turn then carries on (or the call is refused) and a normal result closes the step.
     * Built from core's own button class and Bootstrap's spacing utilities, so it needs no CSS of its own.
     */
    function llmaRenderToolApproval(bubble, req) {
        if (!bubble || !req) return;
        const callBubble = bubble.querySelector(`.llma-tool-call-bubble[data-tool-id="${CSS.escape(req.id || '')}"]`);
        if (!callBubble) return;
        callBubble.classList.remove('collapsed');
        callBubble.closest('.llma-tool-group')?.classList.remove('collapsed');
        const toggle = callBubble.querySelector('.llma-tool-call-toggle');
        if (toggle) {
            toggle.textContent = '▾';
            toggle.setAttribute('aria-expanded', 'true');
        }
        const status = callBubble.querySelector('.llma-tool-call-status');
        if (status) status.textContent = translate('needs approval');
        callBubble.querySelector('.llma-tool-approval')?.remove();
        callBubble.classList.add('waiting');
        llmaUpdateToolGroup(callBubble.closest('.llma-tool-group'));

        const row = document.createElement('div');
        row.className = 'llma-tool-approval d-flex flex-wrap align-items-center gap-2 mt-2';
        row.dataset.approvalId = req.approvalId || '';
        const question = document.createElement('span');
        question.textContent = `${translate('Allow')} ${llmaFormatToolName(req.name)}?`;
        row.appendChild(question);
        const choices = [
            { decision: 'allow', label: 'Allow once', primary: true },
            { decision: 'allow_chat', label: 'Allow for this chat', primary: false },
            { decision: 'deny', label: 'Deny', primary: false },
        ];
        for (const choice of choices) {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = `basic-button${choice.primary ? ' btn-primary' : ''}`;
            button.dataset.decision = choice.decision;
            button.textContent = translate(choice.label);
            button.addEventListener('click', () => llmaAnswerToolApproval(row, req.approvalId, choice.decision));
            row.appendChild(button);
        }
        callBubble.querySelector('.llma-tool-call-body')?.insertBefore(row, callBubble.querySelector('.llma-tool-result-slot'));
    }

    /** Sends the user's answer for a waiting tool call and replaces the buttons with what was chosen. */
    async function llmaAnswerToolApproval(row, approvalId, decision) {
        for (const button of row.querySelectorAll('button')) {
            button.disabled = true;
        }
        const reply = await llmaUserAction(
            () => llmaRequest('LLMAssistantResolveToolApproval', { approvalId: approvalId, decision: decision }),
            translate('Could not send your answer')
        );
        if (reply && reply.resolved) {
            row.textContent = decision == 'deny' ? translate('Declined.') : translate('Allowed.');
            // Allowed: the call is at work from here. A declined one gets its result straight away.
            let callBubble = row.closest('.llma-tool-call-bubble.pending.waiting');
            if (callBubble && decision != 'deny') {
                callBubble.classList.remove('waiting');
                llmaToolRunningStatus(callBubble.querySelector('.llma-tool-call-status'));
                llmaUpdateToolGroup(callBubble.closest('.llma-tool-group'));
            }
        }
        else {
            // The turn ended (Stop, or the tab was closed) before the answer arrived: nothing is waiting any more.
            row.textContent = translate('This request has already ended.');
        }
    }

    /** Place a card holder after `group`, behind any holders already there (keeps call order). */
    function llmaInsertToolExtra(group, holder) {
        holder.classList.add('llma-tool-extra');
        let anchor = group;
        while (anchor.nextElementSibling?.classList.contains('llma-tool-extra')) {
            anchor = anchor.nextElementSibling;
        }
        anchor.after(holder);
    }

    /** Only http(s) URLs are linkable: search results are untrusted web data, so a javascript: or data: URL must not become an href. */
    function llmaSafeHttpUrl(url) {
        try {
            const u = new URL(url);
            return (u.protocol == 'https:' || u.protocol == 'http:') ? u.href : '#';
        }
        catch {
            return '#';
        }
    }

    /** Hostname for display ("example.com"), or '' when the URL doesn't parse. */
    function llmaSourceDomain(url) {
        try {
            return new URL(url).hostname.replace(/^www\./, '');
        }
        catch {
            return '';
        }
    }

    /**
     * Insert (once) a compact "Sources" card under the activity group for a web search: one row per result
     * with a letter avatar (no favicon fetches: the extension makes no outbound requests), title and domain.
     * Shows the first few rows; the rest sit behind "Show all".
     */
    function llmaShowToolSourcesCard(callBubble, toolResult, results) {
        const group = callBubble?.closest('.llma-tool-group');
        if (!group || !results.length) {
            return;
        }
        const id = String(toolResult.id || '');
        if (group.parentElement?.querySelector(`.llma-sources[data-tool-id="${CSS.escape(id)}"]`)) {
            return;
        }
        results = results.filter(r => r && typeof r == 'object');
        if (!results.length) {
            return;
        }
        const SHOWN = 4;
        const card = document.createElement('div');
        card.className = 'llma-sources';
        card.setAttribute('data-tool-id', id);
        const head = document.createElement('div');
        head.className = 'llma-sources-head';
        head.textContent = `${results.length} ${results.length == 1 ? translate('source') : translate('sources')}`;
        card.appendChild(head);
        results.forEach((item, i) => {
            const domain = llmaSourceDomain(item.url);
            const row = document.createElement('a');
            row.className = 'llma-source-row' + (i >= SHOWN ? ' extra' : '');
            row.href = llmaSafeHttpUrl(item.url);
            row.target = '_blank';
            row.rel = 'noopener noreferrer';
            const hue = Array.from(domain).reduce((h, c) => (h * 31 + c.charCodeAt(0)) % 360, 0);
            const avatar = document.createElement('span');
            avatar.className = 'llma-source-avatar';
            avatar.style.background = `hsl(${hue} 45% 38%)`;
            avatar.textContent = (domain || '?').charAt(0).toUpperCase();
            const text = document.createElement('span');
            text.className = 'llma-source-text';
            const title = document.createElement('span');
            title.className = 'llma-source-title';
            title.textContent = item.title || item.url || translate('(untitled)');
            const dom = document.createElement('span');
            dom.className = 'llma-source-domain';
            dom.textContent = domain;
            text.append(title, dom);
            row.append(avatar, text);
            card.appendChild(row);
        });
        if (results.length > SHOWN) {
            const more = document.createElement('button');
            more.type = 'button';
            more.className = 'llma-sources-more';
            more.textContent = `${translate('Show all')} ${results.length}`;
            more.addEventListener('click', () => {
                const open = card.classList.toggle('expanded');
                more.textContent = open ? translate('Show fewer') : `${translate('Show all')} ${results.length}`;
            });
            card.appendChild(more);
        }
        const holder = document.createElement('div');
        holder.appendChild(card);
        llmaInsertToolExtra(group, holder);
    }

    /** Insert (once) the inline preview card for a generated image right after its activity group. */
    function llmaShowToolImageCard(bubble, callBubble, toolResult, result) {
        const msgEl = bubble.closest?.('[data-msg-id]');
        const msgId = msgEl ? msgEl.getAttribute('data-msg-id') : null;
        const group = callBubble?.closest('.llma-tool-group');
        if (!msgId || !group || typeof llmaRenderAssetCardHtml != 'function') {
            return;
        }
        // A batch result has one card per image: the first asset keeps the plain id, the others a -2, -3 suffix (see llmaExtractAssetsFromToolCalls).
        const count = Array.isArray(result?.imageUrls) && result.imageUrls.length > 1 ? result.imageUrls.length : 1;
        for (let k = 0; k < count; k++) {
            const assetId = k == 0 ? `${msgId}-tool-${toolResult.id}` : `${msgId}-tool-${toolResult.id}-${k + 1}`;
            const findAsset = () => (LLMAState.assets || []).find(a => a.id == assetId);
            let asset = findAsset();
            if (!asset && typeof llmaRebuildAssetsForThread == 'function') {
                llmaRebuildAssetsForThread(); // only on a miss: a full rebuild per result is O(n) each
                asset = findAsset();
            }
            if (!asset || bubble.querySelector(`.llma-tool-extra [data-asset-id="${CSS.escape(asset.id)}"]`)) {
                continue;
            }
            const holder = document.createElement('div');
            holder.innerHTML = llmaRenderAssetCardHtml(asset, { inline: true });
            llmaInsertToolExtra(group, holder);
        }
        if (result?.rewriteStatus == 'fallback' && !bubble.querySelector(`[data-rewrite-notice="${CSS.escape(toolResult.id)}"]`)) {
            llmaShowRewriteNotice(group, toolResult.id, result.rewriteWarnings);
        }
        llmaHideRepeatedImages(bubble);
    }

    /**
     * An image address in one spelling, to tell whether two images are the same file.
     * @param {string} url - An address, relative or whole, encoded or not.
     * @returns {string} The whole address, decoded; the address as given when it cannot be read.
     */
    function llmaImageAddress(url) {
        try {
            return decodeURI(new URL(url, document.baseURI).href);
        }
        catch {
            return url;
        }
    }

    /**
     * Hides an image written into a reply's text when the same reply shows it as a card: a model often repeats the image it just made, which
     * then showed twice. Any other image in the text is left alone.
     * @param {?HTMLElement} bubble - The reply's bubble.
     */
    function llmaHideRepeatedImages(bubble) {
        if (!bubble) {
            return;
        }
        let carded = new Set(Array.from(bubble.querySelectorAll('.llma-tool-extra .llma-asset-card img'), img => llmaImageAddress(img.getAttribute('src'))));
        if (carded.size == 0) {
            return;
        }
        for (let img of bubble.querySelectorAll('img')) {
            if (img.closest('.llma-asset-card, .llma-tool-group') || !carded.has(llmaImageAddress(img.getAttribute('src')))) {
                continue;
            }
            // A link or paragraph holding nothing else goes with the image, so no gap is left.
            let hidden = img;
            while (hidden.parentElement.matches('a, p') && hidden.parentElement.textContent.trim() == '' && hidden.parentElement.querySelectorAll('img').length == 1) {
                hidden = hidden.parentElement;
            }
            hidden.classList.add('d-none');
        }
    }

    /**
     * Says under a call's image cards that its prompt was not enhanced, and why. The tool result holds both, but its step is closed.
     * @param {HTMLElement} group - The activity group the call is in.
     * @param {string} toolId - The call's id, so the notice is drawn once.
     * @param {Array<string>|undefined} warnings - The result's `rewriteWarnings`.
     */
    function llmaShowRewriteNotice(group, toolId, warnings) {
        let holder = createDiv(null, 'alert alert-warning small py-1 px-2 my-1 llma-tool-rewrite-notice');
        holder.setAttribute('data-rewrite-notice', toolId);
        holder.appendChild(createDiv(null, 'fw-bold', escapeHtml(translate('The prompt was not enhanced.'))));
        if (Array.isArray(warnings) && warnings.length > 0) {
            holder.appendChild(createDiv(null, null, escapeHtml(warnings.join(' '))));
        }
        llmaInsertToolExtra(group, holder);
    }

    /** Fill the result slot of a tool-call bubble with a tool-specific preview (or JSON fallback). */
    function llmaRenderToolResult(bubble, toolResult) {
        if (!bubble || !toolResult) return;
        const callBubble = bubble.querySelector(`.llma-tool-call-bubble[data-tool-id="${toolResult.id}"]`);
        const result = toolResult.result || {};
        const success = result.success !== false && !result.error;

        const target = callBubble || bubble;
        if (callBubble) {
            callBubble.classList.remove('pending', 'waiting');
            callBubble.classList.add(success ? 'success' : 'error');
            callBubble.classList.toggle('declined', !success && !!result.denied);
            callBubble.querySelector('.llma-tool-approval')?.remove();
            const status = callBubble.querySelector('.llma-tool-call-status');
            if (status) status.textContent = success ? translate('done') : (result.denied ? translate('declined') : translate('error'));
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
            llmaShowToolSourcesCard(callBubble, toolResult, result.results);
            const list = document.createElement('ul');
            list.className = 'llma-tool-result-search';
            for (const item of result.results) {
                const li = document.createElement('li');
                const a = document.createElement('a');
                a.href = llmaSafeHttpUrl(item.url);
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
        } else if (success && name === 'code_edit') {
            let info = document.createElement('div');
            info.className = 'llma-tool-result-fileinfo';
            info.textContent = `${result.path || ''} (${result.replacements ?? 1} ${translate(result.replacements == 1 ? 'change' : 'changes')})`;
            resultWrap.appendChild(info);
            if (typeof result.diff == 'string' && result.diff.length) {
                resultWrap.appendChild(llmaRenderDiff(result.diff));
            }
        } else if (success && name === 'code_write') {
            let info = document.createElement('div');
            info.className = 'llma-tool-result-fileinfo';
            info.textContent = `${result.path || ''} (${result.created ? translate('created') : translate('replaced')}, ${result.bytes ?? '?'} ${translate('bytes')})`;
            resultWrap.appendChild(info);
        } else if (success && name === 'code_read' && typeof result.content === 'string') {
            let info = document.createElement('div');
            info.className = 'llma-tool-result-fileinfo';
            info.textContent = `${result.path || ''} (${translate('lines')} ${result.startLine}-${result.endLine} / ${result.totalLines}${result.truncated ? ', ' + translate('truncated') : ''})`;
            resultWrap.appendChild(info);
            let pre = document.createElement('pre');
            pre.className = 'llma-tool-result-filecontent';
            pre.textContent = result.content.length > 4000 ? result.content.slice(0, 4000) + '\n…' : result.content;
            resultWrap.appendChild(pre);
        } else if (success && name === 'code_glob' && Array.isArray(result.files)) {
            let pre = document.createElement('pre');
            pre.className = 'llma-tool-result-filecontent';
            pre.textContent = result.files.length ? result.files.join('\n') + (result.truncated ? '\n…' : '') : translate('No files match.');
            resultWrap.appendChild(pre);
        } else if (success && name === 'code_grep' && Array.isArray(result.matches)) {
            let pre = document.createElement('pre');
            pre.className = 'llma-tool-result-filecontent';
            pre.textContent = result.matches.length ? result.matches.map(m => `${m.file}:${m.line}: ${m.text}`).join('\n') + (result.truncated ? '\n…' : '') : translate('No matches.');
            resultWrap.appendChild(pre);
        } else if (success && name === 'code_todo' && Array.isArray(result.todos)) {
            let marks = { completed: '[x]', in_progress: '[~]', pending: '[ ]' };
            let pre = document.createElement('pre');
            pre.className = 'llma-tool-result-filecontent';
            pre.textContent = result.todos.map(t => `${marks[t.status] || '[ ]'} ${t.content}`).join('\n');
            resultWrap.appendChild(pre);
        } else if (success && name.startsWith('session_') && typeof llmaSessions != 'undefined') {
            llmaSessions.renderResultCard(resultWrap, name, result);
        } else if ((name === 'shell_exec' || name === 'code_bash') && (result.exitCode != null || result.killed)) {
            const info = document.createElement('div');
            info.className = 'llma-tool-result-fileinfo';
            const exitTxt = result.killed ? translate('killed (timeout)') : `${translate('exit')} ${result.exitCode}`;
            let command = result.command || '';
            if (!command && callBubble) {
                // code_bash does not echo the command: take it from the call this result belongs to.
                try {
                    command = JSON.parse(callBubble.getAttribute('data-tool-args') || '{}').command || '';
                }
                catch (ex) {
                    command = '';
                }
            }
            info.textContent = `$ ${command}  [${exitTxt}${result.truncated ? ', ' + translate('truncated') : ''}]`;
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
            pre.tabIndex = 0;
            try {
                pre.textContent = JSON.stringify(result, null, 2);
            } catch (_) {
                pre.textContent = String(result);
            }
            resultWrap.appendChild(pre);
        }

        // Retry affordance — only when the call failed AND the wrapper has the original args. Not for a call the user refused, nor for one the
        // turn ended without a result of its own (it may have been waiting for permission): Retry runs the tool without asking.
        // Re-runs via LLMAssistantExecuteTool (the standalone tool runner) and shows the new
        // result in place. Does NOT amend the conversation history server-side — purely a manual
        // "did the tool start working?" check the user can fire without re-prompting the LLM.
        if (!success && callBubble && !result.denied && !result.unfinished && !name.startsWith('code_')) {
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

    /** A diff (the lines a code_edit result carries) as coloured rows: removed lines red, added lines green. */
    function llmaRenderDiff(diff) {
        let wrap = document.createElement('pre');
        wrap.className = 'llma-tool-result-filecontent llma-diff';
        for (let line of diff.split('\n')) {
            let row = document.createElement('div');
            row.textContent = line;
            if (line.startsWith('+')) {
                row.className = 'llma-diff-add';
            }
            else if (line.startsWith('-')) {
                row.className = 'llma-diff-del';
            }
            else if (line.startsWith('@@')) {
                row.className = 'llma-diff-hunk';
            }
            wrap.appendChild(row);
        }
        return wrap;
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

    /**
     * Settles the rows of the calls a stopped reply left without a result; the Stop closes the socket before the server can send one. A call
     * still waiting for permission never runs, so it reads as the failure the server saves for it. One already under way may yet finish (an
     * image being made does), so its row says only that it had not at the Stop: the chat read again later shows what became of it.
     * @param {?HTMLElement} bubble - The reply's bubble.
     * @param {object} message - The stopped message.
     */
    function llmaSettleToolCalls(bubble, message) {
        for (let call of message?.toolCalls || []) {
            let row = call.result == null ? bubble?.querySelector(`.llma-tool-call-bubble.pending[data-tool-id="${CSS.escape(call.id || '')}"]`) : null;
            if (!row) {
                continue;
            }
            if (row.classList.contains('waiting')) {
                call.result = { success: false, unfinished: true, error: translate('The turn was stopped before this tool finished.') };
                llmaRenderToolResult(bubble, { id: call.id, name: call.name, result: call.result });
                continue;
            }
            row.classList.remove('pending');
            row.classList.add('stopped');
            row.querySelector('.llma-tool-call-status').textContent = translate('stopped');
            llmaUpdateToolGroup(row.closest('.llma-tool-group'));
        }
    }

    /** Draws one stored call, with its result when it has one. */
    function llmaRenderStoredCall(bubble, tc) {
        llmaRenderToolCall(bubble, { id: tc.id, name: tc.name, arguments: tc.arguments });
        if (tc.result != null) {
            llmaRenderToolResult(bubble, { id: tc.id, name: tc.name, result: tc.result });
        }
    }

    /**
     * A stored reply round by round, as the model wrote it: [{text, calls}], each round's calls following its text. Null when the saved rounds
     * cannot give the order: none saved (an older or imported chat), a call that is in none of them, or text that is no longer the message's (an edit).
     * @param {object} message - A stored assistant message.
     * @returns {?Array<{text: string, calls: Array<object>}>} The rounds.
     */
    function llmaReplyRounds(message) {
        let rounds = message?.rounds;
        let calls = message?.toolCalls || [];
        if (!Array.isArray(rounds) || rounds.length == 0 || calls.length == 0) {
            return null;
        }
        if (llmaStripToolTags(rounds.map(round => round?.text || '').join('')) != llmaStripToolTags(message.content || '')) {
            return null;
        }
        // Ids are matched in order and each call used once: a provider may repeat an id from one round to the next.
        let left = [...calls];
        let ordered = rounds.map(round => ({
            text: llmaStripToolTags(round?.text || ''),
            calls: (round?.toolCallIds || []).flatMap(id => {
                let at = left.findIndex(call => call.id == id);
                return at < 0 ? [] : left.splice(at, 1);
            })
        }));
        return left.length == 0 ? ordered : null;
    }

    /**
     * Draws a stored assistant message that made tool calls into its bubble: in the order it streamed when the saved rounds give it, else the
     * calls ahead of the text.
     * @param {HTMLElement} bubble - The message's bubble, already holding its text.
     * @param {object} message - The stored message.
     */
    function llmaReplayReply(bubble, message) {
        let rounds = llmaReplyRounds(message);
        if (!rounds) {
            llmaReplayToolCalls(bubble, message.toolCalls);
            return;
        }
        bubble.innerHTML = '';
        // Asset cards are numbered through the whole message, so each round carries on from the one before.
        let assets = 0;
        for (let round of rounds) {
            if (round.text) {
                let segment = createDiv(null, 'llma-msg-text-segment');
                segment.innerHTML = llmaRenderAssistantContent(round.text, message.id, assets);
                assets += llmaExtractAssetsFromMarkdown(round.text, message.id).assets.length;
                bubble.appendChild(segment);
                llmaPostRenderMermaid(segment);
            }
            for (let tc of round.calls) {
                llmaRenderStoredCall(bubble, tc);
            }
        }
        // A round's text is drawn after the cards of the calls before it.
        llmaHideRepeatedImages(bubble);
    }

    /** Replay stored tool calls + results for an assistant message whose order is not known (an older chat). */
    function llmaReplayToolCalls(bubble, toolCalls) {
        if (!bubble || !Array.isArray(toolCalls)) return;
        for (const tc of toolCalls) {
            llmaRenderStoredCall(bubble, tc);
        }
        // Without the rounds, replay can't know where the text and the calls interleaved.
        // Activity happens before the answer: put the groups ahead of the text.
        const moving = [];
        for (const g of bubble.querySelectorAll(':scope > .llma-tool-group')) {
            moving.push(g);
            let next = g.nextElementSibling;
            while (next && next.classList.contains('llma-tool-extra')) {
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
    window.llmaRenderToolApproval = llmaRenderToolApproval;
    window.llmaRenderDiff       = llmaRenderDiff;
    window.llmaReplayReply      = llmaReplayReply;
    window.llmaSettleToolCalls  = llmaSettleToolCalls;
    window.llmaReplyRounds      = llmaReplyRounds;
    window.llmaHideRepeatedImages = llmaHideRepeatedImages;
})();

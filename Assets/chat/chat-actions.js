/* ================================================================
   LLM Assistant - chat/chat-actions.js
   Per-message actions: the action bar under a message, copy (text and
   Markdown), branch from here, what a delete takes with it, and the row
   under a branch that stops short (Retry / Show later messages).
   Edit, regenerate and delete themselves are in chat-composer.js.
   ================================================================ */

'use strict';

/** Matches a fenced code block or a code span in Markdown: what is inside is text to keep, whatever it looks like. Its two groups are the fence and the span's backticks. */
const LLMA_CODE_PATTERN = '(?:^|(?<=\\n))[ \\t]*(`{3,}|~{3,})[\\s\\S]*?(?:\\n[ \\t]*\\1[`~]*[ \\t]*(?=\\n|$)|$)|(`+)(?:(?!\\n[ \\t]*\\n)[\\s\\S])*?\\2';

/** The actions a user takes on one message of the chat. */
class LLMAMessageActions {
    constructor() {
        /** Set while a delete is being asked about or carried out, so a second click does not ask twice. */
        this.deleting = false;
    }

    /** The node with this id on any branch of the open chat, or null. */
    node(msgId) {
        return (LLMAState.allNodes || []).find(n => n.id == msgId) || LLMAState.messages.find(m => m.id == msgId) || null;
    }

    /** Builds the action bar for a message: a toolbar, so it is one Tab stop and the arrow keys move along it (llma-a11y.js). */
    buildBar(msgId, role) {
        let bar = this.emptyBar();
        let add = (icon, label, onClick, mutates) => {
            let button = llmaCreateIconBtn(icon, translate(label), onClick);
            if (mutates) {
                button.dataset.llmaMutates = 'true';
                button.setAttribute('aria-disabled', this.busy() ? 'true' : 'false');
            }
            bar.appendChild(button);
        };
        add('copy', 'Copy', () => llmaCopyMessage(msgId));
        if (role == 'assistant') {
            add('markdown', 'Copy as Markdown', () => this.copyMarkdown(msgId));
            add('regen', 'Regenerate', () => llmaRegenerateMessage(msgId), true);
        }
        add('fork', 'Branch from here', () => llmaForkFromMessage(msgId), true);
        if (role == 'assistant') {
            add('prompt', 'Use as Prompt', () => {
                let msg = this.node(msgId);
                if (msg) {
                    llmaSendToPromptBox(msg.content);
                }
            });
        }
        else {
            add('edit', 'Edit', () => llmaStartEdit(msgId), true);
        }
        add('trash', 'Delete', () => llmaDeleteMessage(msgId), true);
        return bar;
    }

    /** An action bar with no buttons yet. */
    emptyBar() {
        let bar = document.createElement('div');
        bar.className = 'llma-msg-actions';
        bar.setAttribute('role', 'toolbar');
        bar.setAttribute('aria-label', translate('Message actions'));
        return bar;
    }

    /** Whether the open chat is a session working in the background (a call waiting for approval included): the server is changing its tree. */
    working() {
        return llmaSessions.isActive(LLMAState.threads.find(t => t.id == LLMAState.activeThreadId)?.sessionStatus);
    }

    /** Whether the open chat may be changing on the server, so nothing here may change it: a reply is streaming, or it works in the background. */
    busy() {
        return LLMAState.isGenerating || this.working();
    }

    /** True, after saying why, when an action that changes the chat cannot run now. `whileStreaming` is what to say while a reply streams here. */
    refuseIfBusy(whileStreaming) {
        if (LLMAState.isGenerating) {
            llmaShowToast(whileStreaming, 'info');
            return true;
        }
        if (this.working()) {
            llmaShowToast('This chat is working in the background. Stop it first.', 'info');
            return true;
        }
        return false;
    }

    /** The chat as the server stores it now. Throws when it cannot be read. */
    async fetchThread(threadId) {
        let result = await llmaRequest('LLMAssistantGetThread', { threadId: threadId });
        if (!result?.success || !result.thread) {
            throw new Error(result?.error || 'Chat not found');
        }
        return typeof result.thread == 'string' ? JSON.parse(result.thread) : result.thread;
    }

    /** A message's Markdown source. A reply's reasoning is not part of what it said, so it is left out (a think tag quoted in code stays). */
    markdownOf(node) {
        let content = node?.content || '';
        if (node?.role != 'assistant') {
            return content;
        }
        let reasoningOrCode = new RegExp(`<think>[\\s\\S]*?(?:</think>|$)|${LLMA_CODE_PATTERN}`, 'gi');
        return content.replace(reasoningOrCode, found => found.toLowerCase().startsWith('<think>') ? '' : found).trim();
    }

    /** A message as plain text: a user's message as typed, a reply with its Markdown marks gone. */
    plainTextOf(node) {
        let markdown = this.markdownOf(node);
        if (node?.role != 'assistant' || !window.marked || !window.DOMPurify) {
            return markdown;
        }
        // Math is set aside first: the Markdown parser would read the underscores in it as emphasis. Dollar signs in code are not math.
        let math = [];
        let codeOrMath = new RegExp(`${LLMA_CODE_PATTERN}|\\$\\$(?:(?!\`\`\`)[\\s\\S])+?\\$\\$|\\$(?!\\$)[^\\n\`]+?\\$`, 'g');
        let source = markdown.replace(codeOrMath, found => {
            if (!found.startsWith('$')) {
                return found;
            }
            math.push(found);
            return `%%LLMA_COPY_${math.length - 1}%%`;
        });
        // Rendered off screen rather than detached, because only a laid-out element gives line breaks in innerText.
        let holder = createDiv(null, 'llma-copy-render');
        // The same allowlist as the chat itself: this element is on the page for a moment, so a style in it would load what it names.
        holder.innerHTML = window.DOMPurify.sanitize(window.marked.parse(source), LLMA_SANITIZE_CONFIG);
        for (let item of holder.querySelectorAll('li')) {
            let list = item.parentElement;
            let marker = list.tagName == 'OL' ? `${Array.from(list.children).indexOf(item) + (list.start || 1)}. ` : '- ';
            (item.querySelector(':scope > p') || item).prepend(marker);
        }
        document.body.appendChild(holder);
        let text = holder.innerText;
        holder.remove();
        return text.replace(/%%LLMA_COPY_(\d+)%%/g, (_, index) => math[index]).replace(/\n{3,}/g, '\n\n').trim();
    }

    /** Puts text on the clipboard and says whether that worked. */
    async copy(text) {
        if (await llmaWriteClipboard(text)) {
            llmaShowToast('Copied!', 'success');
        }
        else {
            llmaShowToast('Could not copy to the clipboard', 'error');
        }
    }

    /** Copies a message's Markdown source. */
    copyMarkdown(msgId) {
        let node = this.node(msgId);
        if (node) {
            this.copy(this.markdownOf(node));
        }
    }

    /** Marks a reply the server never saved (the request failed or was stopped before anything arrived), so no later action asks the server about it. */
    markUnsaved(node) {
        if (node) {
            node.unsaved = true;
        }
    }

    /** The ids of this message and of every message under it, among these. */
    subtreeIds(msgId, nodes) {
        let found = new Set([msgId]);
        let queue = [msgId];
        while (queue.length > 0) {
            let parent = queue.pop();
            for (let child of nodes.filter(n => llmaParentId(n) == parent)) {
                if (!found.has(child.id)) {
                    found.add(child.id);
                    queue.push(child.id);
                }
            }
        }
        return found;
    }

    /** How many messages of a stored chat hang under this one, on every branch: deleting it deletes them too. */
    countBelow(msgId, nodes) {
        return this.subtreeIds(msgId, nodes).size - 1;
    }

    /**
     * The stored chat to delete a message from, once nothing is still being written there. A reply the page gave up on (its connection dropped) goes on being written
     * when its turn runs no tools, and would come back after the delete. So when the page has the message, or one under it, that the chat does not store, the server
     * is told to stop and what it then saves is waited for. `stopped` says a turn was ended.
     */
    async settledThread(threadId, msgId) {
        let shown = Array.from(this.subtreeIds(msgId, LLMAState.allNodes || []));
        let thread = await this.fetchThread(threadId);
        let missing = () => shown.some(id => !thread.messages.some(m => m.id == id));
        if (!missing()) {
            return { thread: thread, stopped: false };
        }
        let reply = await llmaRequest('LLMAssistantStopGeneration', { threadId: threadId });
        // Read again either way: a turn may have ended between the first read and the stop.
        thread = await this.fetchThread(threadId);
        // A stopped turn saves what it had a moment later. One with nothing to save never does, so the wait is short.
        for (let attempt = 0; reply?.cancelled && attempt < 8 && missing(); attempt++) {
            await new Promise(resolve => setTimeout(resolve, 300));
            thread = await this.fetchThread(threadId);
        }
        return { thread: thread, stopped: !!reply?.cancelled };
    }

    /** The question asked before a delete that takes other messages with it. */
    deleteQuestion(below) {
        let others = below == 1 ? translate('Delete this message and the 1 message under it?') : `${translate('Delete this message and every message under it?')} (${below})`;
        return `${others} ${translate('This includes other branches.')} ${translate('This cannot be undone.')}`;
    }

    /** Greys out the actions that change the chat while a reply is streaming or the chat is working in the background. */
    syncBusy() {
        let busy = this.busy() ? 'true' : 'false';
        for (let button of document.querySelectorAll('#llma-messages [data-llma-mutates]')) {
            button.setAttribute('aria-disabled', busy);
        }
    }

    /**
     * Keeps the row under the last message right. It offers Retry when the chat shows a user message with no reply under it, and Show later messages when the
     * branch showing stops before its end (after Branch from here, or after deleting a branch), which nothing else on screen would lead back from.
     */
    syncTail() {
        this.syncBusy();
        let container = document.getElementById('llma-messages');
        if (!container) {
            return;
        }
        // The row is drawn again each time (also when the chat list is read), so a button in it that has the keyboard keeps it.
        let old = container.querySelector(':scope > .llma-msg-tail');
        let held = old?.contains(document.activeElement) ? document.activeElement.className : null;
        old?.remove();
        let last = LLMAState.messages[LLMAState.messages.length - 1];
        // A session at work in the background has its reply on the way: asking again would start a second one.
        if (this.busy() || !LLMAState.activeThreadId || !last || last.unsaved || llmaCompareSiblings(last.id).length >= 2) {
            return;
        }
        let canRetry = last.role == 'user' && last.kind != 'session_report';
        let canContinue = llmaChildrenOf(last.id).some(n => !n.unsaved);
        if (!canRetry && !canContinue) {
            return;
        }
        let row = createDiv(null, 'llma-msg-tail');
        if (canRetry) {
            let retry = llmaCreateActionBtn(translate('Retry'), () => llmaRegenerateMessage(last.id), 'basic-button llma-msg-retry');
            retry.type = 'button';
            retry.title = translate('Ask for a reply to this message');
            row.appendChild(retry);
        }
        if (canContinue) {
            let later = llmaCreateActionBtn(translate('Show later messages'), () => llmaSetActiveLeaf(llmaDescendToLeaf(last.id)), 'basic-button llma-msg-later');
            later.type = 'button';
            row.appendChild(later);
        }
        container.appendChild(row);
        if (held) {
            Array.from(row.children).find(b => b.className == held)?.focus({ preventScroll: true });
        }
    }
}

let llmaActions = new LLMAMessageActions();

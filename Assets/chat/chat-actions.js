/* ================================================================
   LLM Assistant - chat/chat-actions.js
   Per-message actions: the action bar under a message, copy (text and
   Markdown), branch from here, what a delete takes with it, and the row
   under a branch that stops short (Retry / Show later messages).
   Edit, regenerate and delete themselves are in chat-composer.js.
   ================================================================ */

'use strict';

/** The actions a user takes on one message of the chat. */
class LLMAMessageActions {
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
                button.setAttribute('aria-disabled', LLMAState.isGenerating ? 'true' : 'false');
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

    /** A message's Markdown source. A reply's reasoning is not part of what it said, so it is left out. */
    markdownOf(node) {
        let content = node?.content || '';
        if (node?.role != 'assistant') {
            return content;
        }
        return content.replace(/<think>[\s\S]*?(<\/think>|$)/gi, '').trim();
    }

    /** A message as plain text: a user's message as typed, a reply with its Markdown marks gone. */
    plainTextOf(node) {
        let markdown = this.markdownOf(node);
        if (node?.role != 'assistant' || !window.marked || !window.DOMPurify) {
            return markdown;
        }
        // Math is set aside first: the Markdown parser would read the underscores in it as emphasis.
        let math = [];
        let source = markdown.replace(/\$\$[\s\S]+?\$\$|\$(?!\$)[^\n]+?\$/g, found => {
            math.push(found);
            return `%%LLMA_COPY_${math.length - 1}%%`;
        });
        // Rendered off screen rather than detached, because only a laid-out element gives line breaks in innerText.
        let holder = createDiv(null, 'llma-copy-render');
        holder.innerHTML = window.DOMPurify.sanitize(window.marked.parse(source));
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
        try {
            if (navigator.clipboard) {
                await navigator.clipboard.writeText(text);
            }
            else {
                copyText(text);
            }
            llmaShowToast('Copied!', 'success');
        }
        catch (error) {
            console.error('[LLMAssistant] Copy failed:', error);
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

    /** Drops a never-saved reply from the page and draws the chat without it. */
    forget(node) {
        LLMAState.allNodes = (LLMAState.allNodes || []).filter(n => n.id != node.id);
        if (LLMAState.activeLeafId == node.id || LLMAState.messages.some(m => m.id == node.id)) {
            LLMAState.activeLeafId = node.parentId ?? null;
            LLMAState.messages = llmaComputeActivePath(LLMAState.allNodes, LLMAState.activeLeafId);
        }
        llmaRenderMessageHistory(LLMAState.messages);
        llmaUpdateContextBar();
    }

    /** How many messages hang under this one, on every branch: deleting it deletes them too. */
    countBelow(msgId) {
        let count = 0;
        let queue = [msgId];
        let seen = new Set(queue);
        while (queue.length > 0) {
            for (let child of llmaChildrenOf(queue.pop())) {
                if (!seen.has(child.id)) {
                    seen.add(child.id);
                    queue.push(child.id);
                    count++;
                }
            }
        }
        return count;
    }

    /** The question asked before a delete that takes other messages with it. */
    deleteQuestion(below) {
        let others = below == 1 ? translate('Delete this message and the 1 message under it?') : `${translate('Delete this message and every message under it?')} (${below})`;
        return `${others} ${translate('This includes other branches.')} ${translate('This cannot be undone.')}`;
    }

    /** Greys out the actions that change the chat while a reply is streaming. */
    syncBusy() {
        for (let button of document.querySelectorAll('#llma-messages [data-llma-mutates]')) {
            button.setAttribute('aria-disabled', LLMAState.isGenerating ? 'true' : 'false');
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
        container.querySelector(':scope > .llma-msg-tail')?.remove();
        let last = LLMAState.messages[LLMAState.messages.length - 1];
        if (LLMAState.isGenerating || !LLMAState.activeThreadId || !last || last.unsaved || llmaCompareSiblings(last.id).length >= 2) {
            return;
        }
        // A session at work in the background has its reply on the way: asking again would start a second one.
        let working = llmaSessions.isActive(LLMAState.threads.find(t => t.id == LLMAState.activeThreadId)?.sessionStatus);
        let canRetry = last.role == 'user' && last.kind != 'session_report' && !working;
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
    }
}

let llmaActions = new LLMAMessageActions();

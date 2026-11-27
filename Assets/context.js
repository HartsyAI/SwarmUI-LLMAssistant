/**
 * How full the conversation is, and shortening it when it gets too full.
 *
 * The context bar shows the tokens the model last saw against its window. The window is what the user set in Settings, else what the
 * model's listing says (`context_length`); a model that says nothing has no window, and the bar shows the count without a percentage.
 * When a chat nears the window the server replaces the start of it with a short summary (see the README, "Long chats"); this class
 * shows where that happened (a marker row after the last summarized message) and runs `/compact` by hand.
 */
class LLMAContext {

    /** The summary that applies to the chat now ({id, upToMessageId, messageCount, tokensBefore, summary}), or null. */
    activeCompaction = null;

    /** True while a summary is being written, so the composer cannot start a second one. */
    compacting = false;

    /** The context window, in tokens, for the model in use: the Settings override, else the model's own figure, else 0 (unknown). */
    windowTokens() {
        let override = parseInt(LLMAState.settings?.parameters?.contextWindow, 10) || 0;
        if (override > 0) {
            return override;
        }
        let model = (LLMAState.availableModels || []).find(m => m.id == LLMAState.currentModel);
        return parseInt(model?.context_length, 10) || 0;
    }

    /** Index in the chat's messages of the last one the active summary covers, or -1 when none applies to the branch showing. */
    anchorIndex() {
        if (!this.activeCompaction) {
            return -1;
        }
        return (LLMAState.messages || []).findIndex(m => m.id == this.activeCompaction.upToMessageId);
    }

    /** How many tokens the conversation holds, and whether the model reported that count (true) or it is estimated (false).
     *  The newest reply after the summary that carries usage is the best figure; the messages after it are estimated. */
    currentTokens() {
        let messages = LLMAState.messages || [];
        let anchor = this.anchorIndex();
        for (let i = messages.length - 1; i > anchor; i--) {
            let reported = messages[i].meta?.usage?.contextTokens;
            if (reported > 0) {
                // The reported figure holds the documents sent up to that reply; only what later ones add to the bounded total is new.
                let documents = llmaChatDocuments.tokens(messages) - llmaChatDocuments.tokens(messages.slice(0, i + 1));
                return { tokens: reported + llmaApproxTokens(messages.slice(i + 1)) + documents, reported: true };
            }
        }
        let exact = typeof LLMAState.exactTokenCount == 'number' && LLMAState.exactTokenCountForLen == messages.length && anchor < 0;
        if (exact) {
            return { tokens: LLMAState.exactTokenCount, reported: !!LLMAState.exactTokenCountIsExact };
        }
        let summaryTokens = anchor >= 0 ? Math.ceil((this.activeCompaction.summary || '').length / 4) : 0;
        // Files on summarized messages are still sent, so all of the chat's count.
        return { tokens: summaryTokens + llmaApproxTokens(messages.slice(anchor + 1)) + llmaChatDocuments.tokens(messages), reported: false };
    }

    /** Redraws the bar above the chat: message count, a fill against the window, and the token figures. */
    updateBar() {
        let bar = document.querySelector('.llma-context-bar');
        let label = document.getElementById('llma-ctx-label');
        let fill = document.getElementById('llma-ctx-fill');
        let tokens = document.getElementById('llma-ctx-tokens');
        // Nothing meaningful to show without a conversation.
        if (!LLMAState.activeThreadId || LLMAState.messages.length == 0) {
            if (bar) {
                bar.style.display = 'none';
            }
            return;
        }
        if (bar) {
            bar.style.display = '';
        }
        // A reply that was never saved (it failed or was stopped before anything arrived) is not counted: a reload would not show it.
        let count = LLMAState.messages.filter(m => !m.unsaved).length;
        let maxMessages = LLMAState.settings?.parameters?.maxContextMessages || 0;
        let used = this.currentTokens();
        let limit = this.windowTokens();
        let pct = 0;
        if (limit > 0) {
            pct = Math.min(100, used.tokens / limit * 100);
        }
        else if (maxMessages > 0) {
            pct = Math.min(100, count / maxMessages * 100);
        }
        if (label) {
            label.textContent = `${count} ${count != 1 ? translate('messages') : translate('message')}`;
        }
        if (fill) {
            fill.style.width = `${pct.toFixed(0)}%`;
        }
        if (tokens && LLMAState.showTokens) {
            let text = `${used.reported ? '' : '~'}${used.tokens.toLocaleString()}`;
            if (limit > 0) {
                text += ` / ${limit.toLocaleString()}`;
            }
            tokens.textContent = `${translate('Context')}: ${text} ${translate('tokens')}`;
            tokens.title = limit > 0
                ? translate('Tokens the conversation holds, out of what the model can hold.')
                : translate('Tokens the conversation holds. This model does not say how many it can hold; set it in Settings > Context.');
        }
    }

    /** Draws the marker after the last summarized message (replacing any drawn before). A summary that does not apply to the branch showing draws nothing. */
    renderMarker() {
        document.querySelectorAll('.llma-compaction-marker').forEach(el => el.remove());
        if (!this.activeCompaction || this.anchorIndex() < 0) {
            return;
        }
        let anchorEl = document.querySelector(`[data-msg-id="${CSS.escape(this.activeCompaction.upToMessageId)}"]`);
        if (!anchorEl) {
            return;
        }
        let c = this.activeCompaction;
        let marker = document.createElement('details');
        marker.className = 'llma-compaction-marker border-top border-bottom py-1 my-2 small text-secondary';
        let summary = document.createElement('summary');
        let saved = c.tokensBefore > 0 ? `, ${translate('was about')} ${c.tokensBefore.toLocaleString()} ${translate('tokens')}` : '';
        summary.textContent = `${translate('Earlier messages were summarized')} (${c.messageCount}${saved})`;
        let body = document.createElement('div');
        body.className = 'llma-compaction-text pt-1';
        body.textContent = c.summary;
        marker.append(summary, body);
        anchorEl.after(marker);
    }

    /** Takes the summary that applies from a chat the server sent ({activeCompaction}), and redraws the marker and bar. */
    setFromThreadResult(result) {
        this.activeCompaction = result?.activeCompaction || null;
        this.renderMarker();
        this.updateBar();
    }

    /** Handles the socket frames about compaction. Returns true when the frame was one, so the caller can stop looking at it. */
    onFrame(data) {
        if (data.compacting) {
            llmaShowToast(translate('Shortening the chat so the reply fits…'), 'info');
            return true;
        }
        if (data.compaction) {
            this.activeCompaction = data.compaction;
            this.renderMarker();
            this.updateBar();
            return true;
        }
        if (data.compactionFailed) {
            llmaShowToast(`${translate('Could not shorten the chat, so it was sent in full')}: ${data.compactionFailed}`, 'error');
            return true;
        }
        return false;
    }

    /** The /compact command: summarizes the start of the chat now. `instructions` (the text after /compact) asks the summary to dwell on something. */
    async compact(instructions = '') {
        if (!LLMAState.activeThreadId || LLMAState.messages.length == 0) {
            llmaShowToast(translate('There is nothing to compact yet.'), 'info');
            return;
        }
        if (this.compacting) {
            return;
        }
        this.compacting = true;
        llmaShowToast(translate('Writing a summary of the earlier messages…'), 'info');
        try {
            let result = await llmaRequest('LLMAssistantCompactThread', { threadId: LLMAState.activeThreadId, model: LLMAState.currentModel || '', instructions: instructions });
            if (!result?.success) {
                llmaShowToast(result?.error || translate('Compacting failed.'), 'error');
            }
            else if (!result.compacted) {
                llmaShowToast(translate('The chat is already as short as it can be.'), 'info');
            }
            else {
                this.activeCompaction = result.compaction;
                this.renderMarker();
                this.updateBar();
                llmaShowToast(translate('Earlier messages were summarized.'), 'success');
            }
        }
        catch (ex) {
            llmaShowToast(`${translate('Compacting failed.')} ${ex}`, 'error');
        }
        finally {
            this.compacting = false;
        }
    }

    /** If `message` is the /compact command, runs it and returns true. */
    tryCommand(message) {
        let match = /^\/compact(?:\s+([\s\S]*))?$/i.exec(message || '');
        if (!match) {
            return false;
        }
        this.compact((match[1] || '').trim());
        return true;
    }
}

llmaContext = new LLMAContext();

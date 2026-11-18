/**
 * Skills and commands as the chat sees them: the list the "/" picker offers, and turning "/name some text" into the message that is sent. A command is a template the user
 * runs; a skill is instructions the model can also load on its own. The server decides what a chat may use (and what a workspace may add), so this keeps only the last list
 * it was given and asks the server to expand a name, so nothing here can offer more than the chat can run.
 */
class LLMAPromptAssets {

    constructor() {
        /** The skills and commands the server last offered, each { kind, name, description, argumentHint, scope, userOnly }. */
        this.assets = [];
        /** The chat's workspace as the server described it ({ id, label, trusted, ignored }), or null. */
        this.workspace = null;
        /** The user's workspaces with whether each is trusted, for the settings tab. */
        this.workspaces = [];
        /** Whether the user may save shared skills and commands. */
        this.canWriteShared = false;
        /** The chat the list was loaded for ('' for none) and when, so it is looked at again when the chat changes or after a while. */
        this.loadedFor = null;
        this.loadedAt = 0;
        /** The request in flight, so callers that arrive meanwhile wait for it instead of starting another. */
        this.pending = null;
    }

    /** The id of the chat the list is for. */
    currentThread() {
        return LLMAState.activeThreadId || '';
    }

    /** Loads the list from the server (without a toast: the picker works with an empty list). */
    refresh() {
        if (this.pending) {
            return this.pending;
        }
        let threadId = this.currentThread();
        this.pending = (async () => {
            try {
                let reply = await llmaRequest('LLMAssistantGetPromptAssets', { threadId: threadId });
                this.assets = reply?.assets || [];
                this.workspace = reply?.workspace || null;
                this.workspaces = reply?.workspaces || [];
                this.canWriteShared = !!reply?.canWriteShared;
            }
            catch {
                this.assets = [];
                this.workspace = null;
            }
            this.loadedFor = threadId;
            this.loadedAt = Date.now();
            this.pending = null;
            if (typeof llmaPickerOnListChanged == 'function') {
                llmaPickerOnListChanged();
            }
        })();
        return this.pending;
    }

    /** Whether the list is for another chat than the current one, or old. */
    isStale() {
        return this.loadedFor != this.currentThread() || Date.now() - this.loadedAt > 10000;
    }

    /** Starts a refresh in the background when the list is stale. Returns what the picker can show now. */
    ensureFresh() {
        if (this.isStale()) {
            this.refresh();
        }
        return this.assets;
    }

    /** Looks a name up, a command before a skill of the same name. */
    find(name) {
        let lower = (name || '').toLowerCase();
        return this.assets.find(a => a.kind == 'command' && a.name == lower) || this.assets.find(a => a.kind == 'skill' && a.name == lower) || null;
    }

    /** The picker's items for these assets, in the picker's own shape. */
    pickerItems() {
        return this.ensureFresh().map(a => ({
            kind: a.kind,
            id: a.name,
            name: a.name,
            description: a.argumentHint ? `${a.argumentHint} ${a.description}` : a.description,
        }));
    }

    /** "/name rest" for a known command or skill: { asset, args }; null for anything else. The chat's list is loaded first when it is not yet. */
    async invocation(message) {
        let match = (message || '').match(/^\/([A-Za-z0-9][\w-]*)\b\s*([\s\S]*)$/);
        if (!match) {
            return null;
        }
        if (this.isStale()) {
            await this.refresh();
        }
        let asset = this.find(match[1]);
        return asset ? { asset: asset, args: match[2].trim() } : null;
    }

    /** The message text for running the asset with these arguments, or null (with a toast) when the server refuses. */
    async expand(invocation) {
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantExpandPromptAsset', {
            kind: invocation.asset.kind,
            name: invocation.asset.name,
            arguments: invocation.args,
            threadId: this.currentThread(),
        }), translate('Could not run the command'));
        return reply ? reply.text : null;
    }
}

let llmaPromptAssets = new LLMAPromptAssets();

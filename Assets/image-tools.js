/**
 * The image defaults in the General tab of the settings dialog: the default image model generate_image uses when nothing else names one, and
 * the most images one call may make. They live behind their own routes (LLMAssistantGetImageDefaults / LLMAssistantSetImageDefaults), not the
 * settings blob, because the limit is a server setting that needs llm_shared_write. The inputs are built by settings-forms.js; this file fills
 * them when the dialog opens and saves them with "Save & Close".
 */
class LLMAImageTools {

    constructor() {
        /** What the server last told us, to know what changed on save. */
        this.loaded = null;
    }

    /** Fills the two inputs from the server (and the model suggestions from the installed models). Safe to call each time the dialog opens. */
    async load() {
        let model = document.getElementById('llma-s-image-default-model');
        let max = document.getElementById('llma-s-image-batch-max');
        if (!model || !max) {
            return;
        }
        this.loaded = null;
        let defaults;
        try {
            defaults = await llmaRequest('LLMAssistantGetImageDefaults', {});
        }
        catch (e) {
            return;
        }
        this.loaded = defaults;
        max.value = defaults.maxImages;
        max.max = defaults.hardMaxImages;
        max.disabled = !defaults.canWriteShared;
        max.title = defaults.canWriteShared ? '' : translate('Only an admin can change this limit.');
        await this.fillModels(model, defaults);
    }

    /** Fills the dropdown with "none" and the installed models (the first 100 the server lists), keeping a saved model that is no longer installed so it is shown rather than silently dropped. A failed listing leaves just those. */
    async fillModels(select, defaults) {
        let names = [];
        let failed = false;
        try {
            // The server lists at most 100 at a time and says where the next page starts; up to 2,000 models are read.
            let offset = 0;
            for (let page = 0; page < 20; page++) {
                let reply = await llmaRequest('LLMAssistantListImageModels', { limit: 100, offset: offset });
                names.push(...(reply.models || []).map(m => m.name));
                if (reply.nextOffset == null) {
                    break;
                }
                offset = reply.nextOffset;
            }
        }
        catch (e) {
            failed = true;
        }
        let saved = defaults.personalDefaultModel || '';
        if (saved && !names.includes(saved)) {
            names.push(saved);
        }
        let none = defaults.sharedDefaultModel ? `${translate('(shared default)')}: ${defaults.sharedDefaultModel}` : translate('(none)');
        let note = failed ? `<option value="" disabled>${escapeHtml(translate('(the model list could not be loaded)'))}</option>` : '';
        select.innerHTML = `<option value="">${escapeHtml(none)}</option>` + note + names.map(n => `<option value="${escapeHtml(n)}">${escapeHtml(n)}</option>`).join('');
        select.value = saved;
        if (typeof autoSelectWidth == 'function') {
            autoSelectWidth(select);
        }
    }

    /** Saves what changed. Returns false when the server refused (an unknown model, or no right to change the limit), after telling the user. */
    async save() {
        let model = document.getElementById('llma-s-image-default-model');
        let max = document.getElementById('llma-s-image-batch-max');
        if (!model || !max || !this.loaded) {
            return true;
        }
        let body = {};
        let wanted = model.value.trim();
        if (wanted != (this.loaded.personalDefaultModel || '')) {
            body.defaultModel = wanted;
        }
        let count = parseInt(max.value, 10);
        if (!max.disabled && Number.isFinite(count) && count != this.loaded.maxImages) {
            body.maxImages = count;
        }
        if (Object.keys(body).length == 0) {
            return true;
        }
        let reply = await llmaUserAction(() => llmaRequest('LLMAssistantSetImageDefaults', body), translate('Could not save the image defaults'));
        if (!reply) {
            return false;
        }
        this.loaded = reply;
        model.value = reply.personalDefaultModel || '';
        if (typeof autoSelectWidth == 'function') {
            autoSelectWidth(model);
        }
        max.value = reply.maxImages;
        return true;
    }
}

let llmaImageTools = new LLMAImageTools();

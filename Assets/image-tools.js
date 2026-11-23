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
        model.value = defaults.personalDefaultModel || '';
        model.placeholder = defaults.sharedDefaultModel ? `${defaults.sharedDefaultModel} (${translate('shared default')})` : translate('none');
        max.value = defaults.maxImages;
        max.max = defaults.hardMaxImages;
        max.disabled = !defaults.canWriteShared;
        max.title = defaults.canWriteShared ? '' : translate('Only an admin can change this limit.');
        this.fillSuggestions(model);
    }

    /** Offers the installed models as suggestions while typing. A failure just means no suggestions. */
    async fillSuggestions(input) {
        let list = document.getElementById('llma-image-model-list');
        if (!list) {
            list = document.createElement('datalist');
            list.id = 'llma-image-model-list';
            input.insertAdjacentElement('afterend', list);
            input.setAttribute('list', list.id);
            input.setAttribute('autocomplete', 'off');
        }
        try {
            let reply = await llmaRequest('LLMAssistantListImageModels', { limit: 100 });
            list.innerHTML = (reply.models || []).map(m => `<option value="${escapeHtml(m.name)}">${escapeHtml(m.title || '')}</option>`).join('');
        }
        catch (e) {
            list.innerHTML = '';
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
        max.value = reply.maxImages;
        return true;
    }
}

let llmaImageTools = new LLMAImageTools();

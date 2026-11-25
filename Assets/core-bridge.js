/**
 * The one place the extension's front end touches SwarmUI core through something that is not a documented hook.
 * Core's JS offers callback lists for session-ready, backends-revised and parameter-build events (all used directly where needed),
 * but none for "an image was generated". Until it does (see docs/UPSTREAM-PROPOSALS.md), this wraps core's global once and fans
 * the call out to any number of listeners, each failure-isolated so a listener can never break the Generate tab. It also reads
 * which image model the Generate tab has picked.
 */
class LLMACoreBridge {

    constructor() {
        /** Listeners for generated images, each (image, metadata, batchId) => void. */
        this.imageListeners = [];
        /** Whether core's gotImageResult has been wrapped. */
        this.imageHooked = false;
    }

    /** Calls the listener whenever core shows a newly generated image. Safe to call early: it retries until core's function exists. */
    onImageGenerated(listener) {
        this.imageListeners.push(listener);
        this.hookImageResult();
    }

    /** The image model picked on the Generate tab, or empty. Core offers no accessor for it, so its dropdown is read. */
    selectedImageModel() {
        return document.getElementById('current_model')?.value || '';
    }

    /** Wraps core's global gotImageResult once. Returns whether the hook is in place. */
    hookImageResult() {
        if (this.imageHooked) {
            return true;
        }
        if (typeof window == 'undefined' || typeof window.gotImageResult != 'function') {
            return false;
        }
        let original = window.gotImageResult;
        let bridge = this;
        window.gotImageResult = function (image, metadata, batchId) {
            let result = original.apply(this, arguments);
            for (let listener of bridge.imageListeners) {
                try {
                    listener(image, metadata, batchId);
                }
                catch (e) {
                    console.warn('[LLMAssistant] image listener error:', e);
                }
            }
            return result;
        };
        this.imageHooked = true;
        return true;
    }
}

let llmaCoreBridge = new LLMACoreBridge();

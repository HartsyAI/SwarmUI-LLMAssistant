/** The only generation settings an approved call may carry. Anything else (another model, a refiner, a backend type, a second prompt)
 * could send the image somewhere the backend check did not look at. */
const SAFE_PARAMS = ['model', 'negativeprompt', 'steps', 'width', 'height', 'seed', 'cfgscale', 'sampler', 'scheduler', 'images', 'exactbackendid'];

const plain = (name: unknown) => String(name ?? '').toLowerCase().replace(/\.safetensors$/, '');

export type WantedImage = { model: string; prompt: string; backend: string };

/** Why a generate_image call waiting for approval is not the one the run asked for, or null when it is.
 * The prompt must be the requested text exactly: SwarmUI reads tags in a prompt (<c>&lt;param[model]:...&gt;</c>, <c>&lt;preset:...&gt;</c>)
 * after the approval and before it picks a backend, so they could change both the model and the backend. The call must also pin the
 * image to one accepted backend by id. */
export function objection(args: any, wanted: WantedImage): string | null {
    let params = args?.params ?? {};
    if (typeof params !== 'object' || Array.isArray(params)) {
        return 'its params are not an object';
    }
    let entries = Object.entries<any>(params).map(([key, value]) => [key.toLowerCase().replace(/[^a-z]/g, ''), value] as [string, any]);
    let extra = Object.keys(args ?? {}).filter(key => !['prompt', 'model', 'params', 'images', 'aspect', 'rewrite'].includes(key));
    if (extra.length > 0) {
        return `it passes ${extra.join(', ')}`;
    }
    if (args?.prompt !== wanted.prompt) {
        return 'its prompt is not exactly the requested text';
    }
    if (String(args.rewrite ?? '').toLowerCase() === 'off') {
        return 'it switches the prompt rewrite off';
    }
    let models = [args.model, ...entries.filter(([key]) => key === 'model').map(([, value]) => value)].filter(m => m !== undefined);
    if (models.length === 0 || models.some(m => plain(m) !== plain(wanted.model))) {
        return `it does not name exactly the configured model (${models.join(', ') || 'none'})`;
    }
    let unsafe = entries.filter(([key]) => !SAFE_PARAMS.includes(key)).map(([key]) => key);
    if (unsafe.length > 0) {
        return `its params set ${unsafe.join(', ')}`;
    }
    if (entries.some(([key, value]) => key === 'negativeprompt' && String(value).includes('<'))) {
        return 'its negative prompt holds a tag';
    }
    let pins = entries.filter(([key]) => key === 'exactbackendid');
    if (pins.length === 0 || pins.some(([, value]) => String(value) !== wanted.backend)) {
        return `it does not pin image backend ${wanted.backend} with params.exactbackendid`;
    }
    if (entries.some(([key, value]) => (key === 'images' && Number(value) > 1)
        || (['width', 'height'].includes(key) && Number(value) > 1344) || (key === 'steps' && Number(value) > 40))) {
        return 'a value is over what the run allows';
    }
    return Number(args.images ?? 1) > 1 ? 'it asks for more than one image' : null;
}

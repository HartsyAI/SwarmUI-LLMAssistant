/** The only generation settings an approved call may carry. Anything else (another model, a refiner, a backend type) could send the
 * image somewhere the backend check did not look at. */
const SAFE_PARAMS = ['model', 'prompt', 'negativeprompt', 'steps', 'width', 'height', 'seed', 'cfgscale', 'sampler', 'scheduler', 'images', 'exactbackendid'];

const plain = (name: unknown) => String(name ?? '').toLowerCase().replace(/\.safetensors$/, '');

/** Why a generate_image call waiting for approval is not the one the run asked for, or null when it is. The call must pin the image to
 * an accepted backend by id, so it cannot be routed anywhere the backend check did not look at. */
export function objection(args: any, imageModel: string, backends: string[]): string | null {
    let params = args?.params ?? {};
    let entries = Object.entries<any>(params).map(([key, value]) => [key.toLowerCase().replace(/[^a-z]/g, ''), value] as [string, any]);
    let extra = Object.keys(args ?? {}).filter(key => !['prompt', 'model', 'params', 'images', 'aspect', 'rewrite'].includes(key));
    if (extra.length > 0) {
        return `it passes ${extra.join(', ')}`;
    }
    if (args.rewrite === 'off') {
        return 'it switches the prompt rewrite off';
    }
    let models = [args.model, ...entries.filter(([key]) => key === 'model').map(([, value]) => value)].filter(m => m !== undefined);
    if (models.length === 0 || models.some(m => plain(m) !== plain(imageModel))) {
        return `it does not name exactly the configured model (${models.join(', ') || 'none'})`;
    }
    let unsafe = entries.filter(([key]) => !SAFE_PARAMS.includes(key)).map(([key]) => key);
    if (unsafe.length > 0) {
        return `its params set ${unsafe.join(', ')}`;
    }
    let pins = entries.filter(([key]) => key === 'exactbackendid');
    if (pins.length === 0 || pins.some(([, value]) => !backends.includes(String(value)))) {
        return `it does not pin an accepted image backend (${backends.join(', ')}) with params.exactbackendid`;
    }
    if (entries.some(([key, value]) => (key === 'images' && Number(value) > 1)
        || (['width', 'height'].includes(key) && Number(value) > 1344) || (key === 'steps' && Number(value) > 40))) {
        return 'a value is over what the run allows';
    }
    return Number(args.images ?? 1) > 1 ? 'it asks for more than one image' : null;
}

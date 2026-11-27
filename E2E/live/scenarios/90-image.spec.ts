import { test, expect, config, MAX_ROUNDS, PROMPT_ALLOWANCE } from '../lib/live';
import { listBackends, listImageModels } from '../lib/guard';
import { pngSpread } from '../lib/png';
import { unloadOwnChatModel } from '../lib/server';
import { updateState } from '../lib/state';

/** The only generation settings an approved call may carry. Anything else (another model, a refiner, a backend type) could send the
 * image somewhere the backend check did not look at. */
const SAFE_PARAMS = ['model', 'prompt', 'negativeprompt', 'steps', 'width', 'height', 'seed', 'cfgscale', 'sampler', 'scheduler', 'images', 'exactbackendid'];

const plain = (name: unknown) => String(name ?? '').toLowerCase().replace(/\.safetensors$/, '');

/** Why a generate_image call waiting for approval is not the one the run asked for, or null when it is. */
function objection(args: any, backends: string[]): string | null {
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
    if (models.length === 0 || models.some(m => plain(m) !== plain(config.imageModel))) {
        return `it does not name exactly the configured model (${models.join(', ') || 'none'})`;
    }
    let unsafe = entries.filter(([key]) => !SAFE_PARAMS.includes(key)).map(([key]) => key);
    if (unsafe.length > 0) {
        return `its params set ${unsafe.join(', ')}`;
    }
    if (entries.some(([key, value]) => (key === 'exactbackendid' && !backends.includes(String(value))) || (key === 'images' && Number(value) > 1)
        || (['width', 'height'].includes(key) && Number(value) > 1344) || (key === 'steps' && Number(value) > 40))) {
        return 'a value is over what the run allows';
    }
    return Number(args.images ?? 1) > 1 ? 'it asks for more than one image' : null;
}

test('an image is generated through the assistant, with its prompt rewritten', async ({ live }) => {
    test.setTimeout(config.idleTimeoutMs + config.imageTimeoutMs + 60_000);
    if (!config.imageModel) {
        live.skip('LLMA_LIVE_IMAGE_MODEL is not set.');
    }
    if (!(await listImageModels(live.client)).includes(config.imageModel)) {
        live.skip(`SwarmUI lists no model "${config.imageModel}".`);
    }
    if (live.guard.image.length === 0) {
        live.skip('No local image backend is enabled.');
    }
    // A default preset of the image tool would add settings to the call that the approval cannot show.
    let toolConfig = (await live.client.ok('LLMAssistantGetToolConfig', { toolId: 'generate_image' })).config;
    if (toolConfig?.defaultPreset) {
        live.skip(`The image tool has a default preset ("${toolConfig.defaultPreset}"), whose settings the suite cannot check.`);
    }
    // Earlier scenarios leave the chat model in memory, which would count against the room the image needs. The turn loads it again.
    let unloaded = await unloadOwnChatModel(live.client);
    if (unloaded) {
        live.note(unloaded);
    }
    await live.whenIdle('image');
    let threadId = await live.createChat('image', { tools: true, autoEnhance: 'on' });
    await live.requireTool(threadId, 'generate_image');
    let paidBefore = (await listBackends(live.client)).filter(b => live.guard.bystanders.includes(b.id));
    let backend = live.guard.image[0];
    let request = { model: config.imageModel, images: 1, params: { steps: 12, width: 768, height: 768, exactbackendid: backend } };
    let allowed = 0;
    let refused: string[] = [];
    // The image, the chat model's rounds and the rewrite.
    live.reserve(1, 4000, 1);
    updateState(s => s.usedImageBackends = true);
    let result = await live.chatTurn(threadId,
        `Call the generate_image tool exactly once, with these arguments: ${JSON.stringify({ ...request, prompt: `a red lighthouse on a cliff at dawn, run ${live.runId}` })}. Then say in one sentence that it is done.`, {
            forceToolId: 'generate_image',
            requests: MAX_ROUNDS,
            estimate: MAX_ROUNDS * (PROMPT_ALLOWANCE.tools + config.maxReplyTokens),
            timeoutMs: config.imageTimeoutMs,
            approve: a => {
                let why = a.name !== 'generate_image' ? `it is ${a.name}` : allowed > 0 ? 'one image was already allowed' : objection(a.arguments, live.guard.image);
                if (why) {
                    refused.push(`${a.name} ${JSON.stringify(a.arguments)}: ${why}`);
                    return 'deny';
                }
                allowed++;
                return 'allow';
            }
        });
    let results = result.toolResults.filter(r => r.name === 'generate_image' && !r.result?.denied);
    let made = results.find(r => r.result?.success === true)?.result;
    // Recorded before anything can fail, so the image is deleted whatever else happens.
    let urls: string[] = made ? made.imageUrls ?? [made.imageUrl] : [];
    for (let url of urls) {
        let path = decodeURIComponent(url).replace(/^\/+/, '').replace(/^View\/[^/]+\//, '').replace(/^Output\//, '');
        updateState(s => s.created.images.push(path));
    }
    if (refused.length > 0) {
        live.note(`Denied: ${refused.join('; ')}`.slice(0, 600));
    }
    live.note(`Allowed ${allowed} call(s). Result: ${JSON.stringify(made ?? results[0]?.result ?? 'none').slice(0, 600)}`);
    expect(allowed, 'the model made the call that was asked for').toBe(1);
    live.expectFinished(result);
    expect(made, `an image: ${JSON.stringify(results).slice(0, 600)}`).toBeTruthy();
    expect(made.model).toBe(config.imageModel);
    let file = await live.client.getBytes(made.imageUrl);
    let spread = pngSpread(file);
    live.note(`Image: ${file.length} bytes, pixel spread ${spread ?? 'not measured'}.`);
    expect(file.length, 'an image file').toBeGreaterThan(10_000);
    if (spread !== null) {
        expect(spread, 'not a flat (black) frame').toBeGreaterThan(30);
    }
    for (let before of paidBefore) {
        let after = (await listBackends(live.client)).find(b => b.id === before.id)!;
        // A backend that was used since would have gone from never used to used, or have a shorter time since its last use.
        expect(after.everUsed && (!before.everUsed || after.secondsSinceUsed < before.secondsSinceUsed), `backend ${before.id} was not used`).toBe(false);
    }
    expect.soft(made.rewriteStatus, `the prompt was rewritten (style ${made.promptStyle ?? 'none'}; ${(made.rewriteWarnings ?? []).join(' ')})`).toBe('rewritten');
});

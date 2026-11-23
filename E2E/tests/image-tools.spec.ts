import { test as base, expect, send, lastAssistant, setScenarios, mockRequests, clearRequests, api, turn, toolResults } from './fixtures';
import type { Page } from '@playwright/test';

// The image workflow tools: list_image_models, list_loras, list_wildcards and list_presets (each called by the scripted chat model, and the same listings through their API routes), generate_image's model
// choice (an error that lists candidates, a validated `model` with "did you mean", the user's default image model) and its batch (`images`, held to a server limit, one rewrite for the whole batch), and the
// Settings control for the defaults. There is no image backend here, so no image is ever made: what is checked is everything up to the engine.
// Not covered: a real batch of images coming back (needs a GPU); the chat showing one card per image is checked by reading the result shape in a unit test only.

const MYSTERY = 'e2e-mystery.safetensors';
const IDEOGRAM = 'e2e-ideogram-4.safetensors';
const HINTED = 'e2e-hinted.safetensors';
const LORA = 'e2e-style-lora.safetensors';

const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await clean(llm);
        await use(llm);
        await clean(llm);
    }
});

/** Puts everything the specs here change back: the defaults, the automatic rewrite, the preset and the wildcards. */
async function clean(page: Page) {
    await clearRequests();
    await setScenarios([]);
    await api(page, 'LLMAssistantSetImageDefaults', { defaultModel: '' });
    await api(page, 'LLMAssistantSetImageDefaults', { maxImages: 4, scope: 'shared' });
    await api(page, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ promptRewrite: { auto: false } }) });
    for (let title of ['E2E Anime', 'E2E Plain']) {
        await api(page, 'DeletePreset', { preset: title });
    }
    for (let card of ['e2e-colors', 'e2e-private/poses']) {
        await api(page, 'DeleteWildcard', { card });
    }
}

/** Runs a tool the way the Tools picker does and returns what the tool returned. */
async function run(page: Page, toolId: string, args: Record<string, unknown> = {}) {
    let reply = await api(page, 'LLMAssistantExecuteTool', { toolId, args });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    return reply.result;
}

const names = (list: any[]) => list.map(x => x.name);

// ---- the list routes (API only) ----

test('the model list route returns the installed models with their type and the notes from their metadata', async ({ llm }) => {
    let reply = await api(llm, 'LLMAssistantListImageModels');
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    expect(names(reply.models)).toEqual(expect.arrayContaining([IDEOGRAM, MYSTERY, HINTED, 'e2e-declared-json.safetensors']));
    expect(reply.installed).toBeGreaterThanOrEqual(4);
    let ideogram = reply.models.find((m: any) => m.name == IDEOGRAM);
    expect(ideogram.architecture).toBe('ideogram-4');
    expect(ideogram.class).toBe('Ideogram 4');
    let hinted = reply.models.find((m: any) => m.name == HINTED);
    expect(hinted).toMatchObject({ title: 'E2E Hinted Model', promptNotes: 'Write short comma separated tags.', triggerPhrase: 'hinted_token' });
    // A model core cannot place is still listed, with no invented type.
    expect(reply.models.find((m: any) => m.name == MYSTERY).architecture).toBeUndefined();
    expect(reply.note).toContain('not instructions');
});

test('the model list filters by name words and by class, and is bounded', async ({ llm }) => {
    expect(names((await api(llm, 'LLMAssistantListImageModels', { query: 'hinted' })).models)).toEqual([HINTED]);
    expect(names((await api(llm, 'LLMAssistantListImageModels', { query: 'E2E HINTED model' })).models)).toEqual([HINTED]);
    expect(names((await api(llm, 'LLMAssistantListImageModels', { modelClass: 'ideogram' })).models)).toEqual([IDEOGRAM]);
    expect((await api(llm, 'LLMAssistantListImageModels', { query: 'no such model anywhere' })).models).toEqual([]);
    let limited = await api(llm, 'LLMAssistantListImageModels', { limit: 2 });
    expect(limited.models).toHaveLength(2);
    expect(limited.shown).toBe(2);
    expect(limited.matched).toBeGreaterThan(2);
    expect(limited.more).toContain('more match');
    // A huge or nonsensical limit is held to the maximum / the default, never an error.
    expect((await api(llm, 'LLMAssistantListImageModels', { limit: 100000 })).success).toBe(true);
    expect((await api(llm, 'LLMAssistantListImageModels', { limit: -4 })).models.length).toBeGreaterThan(0);
});

test('the LoRA list returns trigger words and tags, and forModel checks the model name', async ({ llm }) => {
    let reply = await api(llm, 'LLMAssistantListLoras');
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    let lora = reply.loras.find((l: any) => l.name == LORA);
    expect(lora).toMatchObject({ title: 'E2E Style LoRA', triggerWords: 'e2estyle token' });
    expect(lora.tags).toEqual(['style', 'e2e']);
    expect(reply.usage).toContain('loras');
    expect(names((await api(llm, 'LLMAssistantListLoras', { query: 'style' })).loras)).toContain(LORA);
    expect(names((await api(llm, 'LLMAssistantListLoras', { query: 'e2e' })).loras)).toContain(LORA);
    expect((await api(llm, 'LLMAssistantListLoras', { query: 'nothing like that' })).loras).toEqual([]);
    // forModel must be an installed model; a near miss gets suggestions, not a guess.
    let unknown = await api(llm, 'LLMAssistantListLoras', { forModel: 'e2e-ideogam-4' });
    expect(unknown.success).toBe(false);
    expect(unknown.error).toContain('Did you mean');
    expect(unknown.error).toContain(IDEOGRAM);
});

test('the wildcard list returns names only unless samples are asked for', async ({ llm }) => {
    for (let [card, options] of [['e2e-colors', 'secret red\nsecret blue\n# a comment\nsecret green'], ['e2e-private/poses', 'standing\nsitting']]) {
        let saved = await api(llm, 'EditWildcard', { card, options });
        expect(saved.success, JSON.stringify(saved)).toBe(true);
    }
    let plain = await api(llm, 'LLMAssistantListWildcards', { query: 'e2e' });
    expect(plain.success, JSON.stringify(plain)).toBe(true);
    expect(names(plain.wildcards)).toEqual(['e2e-colors', 'e2e-private/poses']);
    expect(plain.wildcards[0].use).toBe('__e2e-colors__');
    // Nothing from the files by default: no samples, and the text appears nowhere in the reply.
    expect(JSON.stringify(plain)).not.toContain('secret');
    expect(plain.wildcards.every((w: any) => w.samples === undefined)).toBe(true);
    let sampled = await api(llm, 'LLMAssistantListWildcards', { query: 'colors', includeSamples: true });
    expect(sampled.wildcards[0].samples).toEqual(['secret red', 'secret blue', 'secret green']);
    expect(sampled.note).toContain('not instructions');
    expect(names((await api(llm, 'LLMAssistantListWildcards', { query: 'poses', limit: 1 })).wildcards)).toEqual(['e2e-private/poses']);
});

test('the preset list returns the name and description of the user\'s presets', async ({ llm }) => {
    for (let [title, description, model] of [['E2E Anime', 'Vibrant anime style', IDEOGRAM], ['E2E Plain', '', MYSTERY]]) {
        let saved = await api(llm, 'AddNewPreset', { title, description, raw: { param_map: { model, steps: '20' } } });
        expect(saved.success ?? !saved.preset_fail, JSON.stringify(saved)).toBeTruthy();
    }
    let reply = await api(llm, 'LLMAssistantListPresets');
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    let anime = reply.presets.find((p: any) => p.name == 'E2E Anime');
    expect(anime.description).toBe('Vibrant anime style');
    expect(anime.summary).toContain(IDEOGRAM.replace('.safetensors', ''));
    expect(reply.presets.find((p: any) => p.name == 'E2E Plain').description).toBeUndefined();
    expect(names((await api(llm, 'LLMAssistantListPresets', { query: 'vibrant' })).presets)).toEqual(['E2E Anime']);
    expect((await api(llm, 'LLMAssistantListPresets', { limit: 1 })).presets).toHaveLength(1);
});

// ---- the defaults routes ----

test('the image defaults start empty with a limit of four, and a near-miss model name is matched or refused with suggestions', async ({ llm }) => {
    let start = await api(llm, 'LLMAssistantGetImageDefaults');
    expect(start).toMatchObject({ success: true, defaultModel: '', maxImages: 4, defaultMaxImages: 4, hardMaxImages: 16, canWriteShared: true });
    // An installed model is found by a short name and saved under its exact name.
    let saved = await api(llm, 'LLMAssistantSetImageDefaults', { defaultModel: 'mystery' });
    expect(saved).toMatchObject({ success: true, defaultModel: MYSTERY, personalDefaultModel: MYSTERY });
    // A name that is not installed is refused, with the closest names.
    let refused = await api(llm, 'LLMAssistantSetImageDefaults', { defaultModel: 'e2e-mistery.safetensors' });
    expect(refused.success).toBe(false);
    expect(refused.error).toContain('Did you mean');
    expect(refused.error).toContain(MYSTERY);
    expect((await api(llm, 'LLMAssistantGetImageDefaults')).defaultModel).toBe(MYSTERY);
    // An empty name clears it.
    expect(await api(llm, 'LLMAssistantSetImageDefaults', { defaultModel: '' })).toMatchObject({ success: true, defaultModel: '' });
});

test('the limit on images per call is a shared setting held between 1 and 16, and nothing is saved by a bad request', async ({ llm }) => {
    expect(await api(llm, 'LLMAssistantSetImageDefaults', { maxImages: 6, scope: 'shared' })).toMatchObject({ success: true, maxImages: 6 });
    for (let bad of [0, 17, 'lots', 2.5]) {
        let refused = await api(llm, 'LLMAssistantSetImageDefaults', { maxImages: bad });
        expect(refused.success, String(bad)).toBe(false);
        expect(refused.error).toContain('1 to 16');
    }
    expect((await api(llm, 'LLMAssistantGetImageDefaults')).maxImages).toBe(6);
    expect((await api(llm, 'LLMAssistantSetImageDefaults', {})).success).toBe(false);
    // The ordinary settings route cannot write them around the checks above.
    await api(llm, 'LLMAssistantSaveSettings', { settings: { imageTools: { maxImages: 16, defaultModel: 'nope' } } });
    expect(await api(llm, 'LLMAssistantGetImageDefaults')).toMatchObject({ maxImages: 6, defaultModel: '' });
});

test('the image routes need a session', async ({ llm }) => {
    for (let route of ['LLMAssistantListImageModels', 'LLMAssistantListLoras', 'LLMAssistantListWildcards', 'LLMAssistantListPresets', 'LLMAssistantGetImageDefaults', 'LLMAssistantSetImageDefaults']) {
        let reply = await llm.evaluate(async (r) => {
            let response = await fetch(`/API/${r}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
            return await response.json();
        }, route);
        expect(reply.success, route).not.toBe(true);
        expect(JSON.stringify(reply), route).toMatch(/session/i);
    }
});

// ---- each list tool, called by the chat model ----

test('the model calls list_image_models and gets the installed models', async ({ llm }) => {
    await setScenarios([turn('which models', [{ name: 'list_image_models', arguments: { query: 'hinted' } }], 'Listed.')]);
    await send(llm, 'which models do I have');
    await expect(lastAssistant(llm)).toContainText('Listed.');
    let [result] = await toolResults();
    expect(result.success).toBe(true);
    expect(names(result.models)).toEqual([HINTED]);
    expect(result.models[0].promptNotes).toBe('Write short comma separated tags.');
});

test('the model calls list_loras and gets trigger words and tags', async ({ llm }) => {
    await setScenarios([turn('which loras', [{ name: 'list_loras', arguments: { query: 'e2e' } }], 'Listed.')]);
    await send(llm, 'which loras do I have');
    await expect(lastAssistant(llm)).toContainText('Listed.');
    let [result] = await toolResults();
    expect(result.loras.find((l: any) => l.name == LORA)).toMatchObject({ triggerWords: 'e2estyle token', tags: ['style', 'e2e'] });
});

test('the model calls list_wildcards and gets names only', async ({ llm }) => {
    await api(llm, 'EditWildcard', { card: 'e2e-colors', options: 'secret red\nsecret blue' });
    await setScenarios([turn('which wildcards', [{ name: 'list_wildcards', arguments: { query: 'e2e-colors' } }], 'Listed.')]);
    await send(llm, 'which wildcards do I have');
    await expect(lastAssistant(llm)).toContainText('Listed.');
    let [result] = await toolResults();
    expect(names(result.wildcards)).toEqual(['e2e-colors']);
    // What the model was sent has no line of the file.
    expect(JSON.stringify(await mockRequests())).not.toContain('secret red');
});

test('the model calls list_presets and gets the user\'s presets', async ({ llm }) => {
    await api(llm, 'AddNewPreset', { title: 'E2E Anime', description: 'Vibrant anime style', raw: { param_map: { model: IDEOGRAM } } });
    await setScenarios([turn('which presets', [{ name: 'list_presets', arguments: {} }], 'Listed.')]);
    await send(llm, 'which presets do I have');
    await expect(lastAssistant(llm)).toContainText('Listed.');
    let [result] = await toolResults();
    expect(names(result.presets)).toContain('E2E Anime');
});

test('the list tools run without asking in Ask mode, because they only read', async ({ llm }) => {
    await llm.locator('label[for="llma-approval-ask"]').click();
    await setScenarios([turn('read only', [{ name: 'list_image_models', arguments: { limit: 1 } }, { name: 'list_presets', arguments: {} }], 'Both ran.')]);
    await send(llm, 'read only please');
    await expect(lastAssistant(llm)).toContainText('Both ran.');
    await expect(llm.locator('.llma-tool-approval')).toHaveCount(0);
});

// ---- generate_image: choosing a model ----

test('generate_image with no model says so and lists the installed models to choose from', async ({ llm }) => {
    await setScenarios([turn('no model', [{ name: 'generate_image', arguments: { prompt: 'a cat' } }], 'No model.')]);
    await send(llm, 'draw with no model');
    await expect(lastAssistant(llm)).toContainText('No model.');
    let [result] = await toolResults();
    expect(result.success).toBe(false);
    expect(result.error).toContain('No image model selected');
    for (let name of [IDEOGRAM, MYSTERY, HINTED]) {
        expect(result.error).toContain(name);
    }
    expect(result.error).toContain('list_presets');
    expect(result.error).toContain('default image model');
});

test('generate_image checks the model argument against what is installed', async ({ llm }) => {
    let typo = await run(llm, 'generate_image', { prompt: 'a cat', model: 'e2e-ideogam-4' });
    expect(typo.success).toBe(false);
    expect(typo.error).toContain('is not installed');
    expect(typo.error).toContain(`Did you mean: ${IDEOGRAM}`);
    let far = await run(llm, 'generate_image', { prompt: 'a cat', model: 'zzzzqqqq' });
    expect(far.success).toBe(false);
    expect(far.error).not.toContain('Did you mean');
    expect(far.error).toContain(MYSTERY);
    // `params.model` is checked the same way.
    let viaParams = await run(llm, 'generate_image', { prompt: 'a cat', params: { model: 'e2e-ideogam-4' } });
    expect(viaParams.error).toContain('Did you mean');
    // A name that fits several models is not guessed.
    let several = await run(llm, 'generate_image', { prompt: 'a cat', model: 'e2e' });
    expect(several.success).toBe(false);
    expect(several.error).toContain('fits several installed models');
});

test('generate_image takes a short or differently written name for an installed model and uses it', async ({ llm }) => {
    await setScenarios([{ match: 'short name cat', rounds: [{ text: 'A cat.' }] }]);
    await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ promptRewrite: { auto: true } }) });
    for (let [i, typed] of ['ideogram', 'E2E-IDEOGRAM-4', 'e2e-ideogram-4'].entries()) {
        await clearRequests();
        // A different prompt each time, so the rewrite cache does not answer the second and third.
        await run(llm, 'generate_image', { prompt: `short name cat ${i}`, model: typed });
        // The rewrite is for the model that was matched: Ideogram's own style (it asks twice when an answer is not JSON).
        let requests = await mockRequests();
        expect(requests.length, typed).toBeGreaterThanOrEqual(1);
        expect(String(requests[0].messages[0].content), typed).toContain('You write prompts for Ideogram 4');
    }
});

test('the user\'s default image model is used when nothing else names one, and anything else wins over it', async ({ llm }) => {
    await setScenarios([{ match: 'default cat', rounds: [{ text: 'A default cat.' }] }]);
    await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ promptRewrite: { auto: true } }) });
    expect((await api(llm, 'LLMAssistantSetImageDefaults', { defaultModel: IDEOGRAM })).success).toBe(true);
    // No model anywhere: the default is used (the engine then fails for want of a backend, after the rewrite for that model ran).
    let result = await run(llm, 'generate_image', { prompt: 'default cat' });
    expect(String(result.error ?? '')).not.toContain('No image model');
    let requests = await mockRequests();
    expect(requests.length).toBeGreaterThanOrEqual(1);
    expect(String(requests[0].messages[0].content)).toContain('You write prompts for Ideogram 4');
    // An explicit model wins over the default (the unplaceable model gets another style).
    await clearRequests();
    await run(llm, 'generate_image', { prompt: 'default cat', model: MYSTERY });
    expect(String((await mockRequests())[0].messages[0].content)).not.toContain('You write prompts for Ideogram 4');
});

test('the tool description names the user\'s default image model and tells the model to list models first', async ({ llm }) => {
    await api(llm, 'LLMAssistantSetImageDefaults', { defaultModel: HINTED });
    await setScenarios([turn('describe', [], 'Hello.')]);
    await send(llm, 'describe it');
    await expect(lastAssistant(llm)).toContainText('Hello.');
    let tool = (await mockRequests())[0].tools.find((t: any) => t.function?.name == 'generate_image').function;
    expect(tool.description).toContain(`default image model is \`${HINTED}\``);
    expect(tool.description).toContain('list_image_models');
    expect(tool.parameters.properties.images.maximum).toBe(4);
    expect(tool.parameters.properties.model.type).toBe('string');
});

// ---- generate_image: a batch ----

test('the tool schema carries the server\'s limit on images, and it follows the setting', async ({ llm }) => {
    await api(llm, 'LLMAssistantSetImageDefaults', { maxImages: 2, scope: 'shared' });
    await setScenarios([turn('schema', [], 'Seen.')]);
    await send(llm, 'schema please');
    await expect(lastAssistant(llm)).toContainText('Seen.');
    let tool = (await mockRequests())[0].tools.find((t: any) => t.function?.name == 'generate_image').function;
    expect(tool.parameters.properties.images.maximum).toBe(2);
    expect(tool.parameters.properties.images.description).toContain('at most 2');
});

test('images must be a whole number', async ({ llm }) => {
    let result = await run(llm, 'generate_image', { prompt: 'a cat', model: MYSTERY, images: 'many' });
    expect(result.success).toBe(false);
    expect(result.error).toContain('images must be a whole number');
});

test('a batch asks the chat model to rewrite the prompt once, not once per image', async ({ llm }) => {
    await setScenarios([{ match: 'batch cat', rounds: [{ text: 'A batch of cats.' }, { text: 'A batch of cats.' }, { text: 'A batch of cats.' }, { text: 'A batch of cats.' }] }]);
    await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ promptRewrite: { auto: true } }) });
    await run(llm, 'generate_image', { prompt: 'batch cat', model: MYSTERY, images: 4 });
    let requests = await mockRequests();
    expect(requests).toHaveLength(1);
    expect(String(requests[0].messages.find((m: any) => m.role === 'user').content)).toContain('batch cat');
    // More than the limit is cut to it, and still one rewrite.
    await clearRequests();
    await setScenarios([{ match: 'bigger batch cat', rounds: [{ text: 'A bigger batch.' }, { text: 'A bigger batch.' }] }]);
    await run(llm, 'generate_image', { prompt: 'bigger batch cat', model: MYSTERY, images: 50 });
    expect(await mockRequests()).toHaveLength(1);
});

// ---- the Settings control ----

test('the Settings control shows the saved defaults, saves them with Save & Close, and a bad model name keeps the dialog open', async ({ llm }) => {
    await llm.locator('#llma-settings-btn').click();
    await expect(llm.locator('#llma-settings-overlay')).toBeVisible();
    let model = llm.locator('#llma-s-image-default-model');
    let max = llm.locator('#llma-s-image-batch-max');
    await expect(model).toBeVisible();
    await expect(model).toHaveValue('');
    await expect(max).toHaveValue('4');
    // The installed models are offered as suggestions.
    await expect.poll(async () => llm.locator('#llma-image-model-list option').count()).toBeGreaterThanOrEqual(4);
    await model.fill('e2e-mistery');
    await llm.locator('#llma-settings-save').click();
    // Refused with suggestions: the dialog stays open so the name can be fixed, and nothing was saved.
    await expect(llm.locator('#llma-settings-overlay')).toBeVisible();
    expect((await api(llm, 'LLMAssistantGetImageDefaults')).defaultModel).toBe('');
    await model.fill('mystery');
    await max.fill('7');
    await llm.locator('#llma-settings-save').click();
    await expect(llm.locator('#llma-settings-overlay')).toBeHidden();
    expect(await api(llm, 'LLMAssistantGetImageDefaults')).toMatchObject({ defaultModel: MYSTERY, maxImages: 7 });
    // Reopened, the control shows what was saved (the exact name the model was matched to).
    await llm.locator('#llma-settings-btn').click();
    await expect(llm.locator('#llma-s-image-default-model')).toHaveValue(MYSTERY);
    await expect(llm.locator('#llma-s-image-batch-max')).toHaveValue('7');
});

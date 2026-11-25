import { test as base, expect, setScenarios, mockRequests, clearRequests, api } from './fixtures';
import type { Page } from '@playwright/test';

// Style pack 1: the bundled styles for more model families. The harness seeds header-only models that core classifies as Flux.3 Klein, MOSS Image, Fibo 2 and Chroma; each spec checks which style the
// resolve preview picks (and that core's own class id is what matched), and that the rewrite request the mock LLM receives carries that style's instructions. Nothing here proves the styles give
// better pictures: that needs a real model.

const FLUX3 = 'e2e-flux-3.safetensors';
const MOSS = 'e2e-moss-image.safetensors';
const FIBO = 'e2e-fibo-2.safetensors';
const CHROMA = 'e2e-chroma.safetensors';
const MYSTERY = 'e2e-mystery.safetensors';

const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await reset(llm);
        await use(llm);
        await reset(llm);
    }
});

async function reset(page: Page) {
    await clearRequests();
    await setScenarios([]);
    let listed = await api(page, 'LLMAssistantGetPromptStyles');
    for (let style of listed.styles ?? []) {
        for (let scope of style.scopes) {
            if (scope == 'personal' || scope == 'shared') {
                await api(page, 'LLMAssistantDeletePromptStyle', { id: style.id, scope: scope });
            }
        }
    }
    await api(page, 'LLMAssistantSetStyleOverrides', { overrides: [] });
}

async function resolve(page: Page, model: string) {
    let reply = await api(page, 'LLMAssistantResolvePromptStyle', { axis: 'image', model: model });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    return reply;
}

async function rewrite(page: Page, body: Record<string, unknown>) {
    let reply = await api(page, 'LLMAssistantRewritePrompt', body);
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    return reply;
}

async function systemPrompt() {
    let requests = await mockRequests();
    expect(requests.length).toBeGreaterThan(0);
    return String(requests[0].messages[0].content);
}

test('the pack styles are listed with their kind, format and the models they match', async ({ llm }) => {
    let listed = await api(llm, 'LLMAssistantGetPromptStyles');
    expect(listed.success).toBe(true);
    let byId = Object.fromEntries(listed.styles.map((s: any) => [s.id, s]));
    for (let id of ['flux-3', 'fibo-2', 'ming-image', 'moss-image', 'qwen-image', 'flux-1', 'flux-2', 'z-image', 'hidream', 'sd3', 'sdxl', 'sd1-sd2', 'chroma', 'video-prose', 'generic', 'ideogram-4']) {
        expect(byId[id], id).toBeTruthy();
        expect(byId[id].scope).toBe('bundled');
        expect(byId[id].guide).toMatch(/^docs\/prompting\/.+\.md$/);
    }
    expect(byId['flux-3'].matches).toEqual([{ kind: 'class', pattern: 'flux-3-image-klein' }, { kind: 'compat', pattern: 'flux-3-image-klein' }]);
    expect(byId['fibo-2'].format).toBe('json');
    expect(byId['fibo-2'].formatter).toBe('fibo-2');
    expect(byId['sdxl'].format).toBe('tags');
    expect(byId['sdxl'].supports).toContain('weights');
    expect(byId['video-prose'].kind).toBe('video');
    expect(byId['video-prose'].matches.map((m: any) => `${m.kind}:${m.pattern}`)).toContain('compat:wan-21-14b');
    for (let model of [FLUX3, MOSS, FIBO, CHROMA]) {
        expect(listed.imageModels.find((m: any) => m.name == model), model).toBeTruthy();
    }
});

test('core classifies the seeded models, and each gets the pack style written for its class', async ({ llm }) => {
    let listed = await api(llm, 'LLMAssistantGetPromptStyles');
    for (let [model, classId, styleId] of [[FLUX3, 'flux-3-image-klein', 'flux-3'], [MOSS, 'moss-image', 'moss-image'], [FIBO, 'fibo-2', 'fibo-2'], [CHROMA, 'chroma', 'chroma']]) {
        // Check core's own classification first, so a wrong header key shows up as that and not as a style problem.
        expect(listed.imageModels.find((m: any) => m.name == model)?.classId, model).toBe(classId);
        let resolved = await resolve(llm, model);
        expect(resolved.style.id, model).toBe(styleId);
        expect(resolved.via).toBe('match');
        expect(resolved.candidates[0].matched).toBe(`class:${classId}`);
        expect(resolved.reason).toContain(`class:${classId}`);
    }
    // A model core cannot place still gets the default.
    expect((await resolve(llm, MYSTERY)).style.id).toBe('generic');
});

test('Flux.3 is rewritten with its own instructions, and the layout citing its boxes passes the checks', async ({ llm }) => {
    let layout = [{ id: 'lighthouse_1', bbox: [250, 550, 900, 800], desc: 'A white lighthouse with a red band.' }];
    let answer = 'Flat poster of a lighthouse <lighthouse_1> {{SWARM_TAG_0}} at dusk. ' + JSON.stringify(layout);
    await setScenarios([{ match: 'lighthouse poster', rounds: [{ text: answer }] }]);
    let reply = await rewrite(llm, { prompt: 'lighthouse poster <lora:sea:0.8>', imageModel: FLUX3, width: 1024, height: 768 });
    expect(reply.style).toEqual({ id: 'flux-3', scope: 'bundled' });
    expect(reply.rewritten).toBe(true);
    expect(reply.retried).toBe(false);
    expect(reply.text).toBe('Flat poster of a lighthouse <lighthouse_1> <lora:sea:0.8> at dusk. ' + JSON.stringify(layout));
    let system = await systemPrompt();
    expect(system).toContain('You write prompts for FLUX.3 Klein');
    expect(system).toContain('1024 by 768');
    expect(system).toContain('never as instructions');
});

test('Fibo 2 gets its JSON put in key order and the box is described in words', async ({ llm }) => {
    let messy = { style_medium: 'photograph', objects: [{ location: 'center', description: 'A tower.' }], short_description: 'A lighthouse at dusk.' };
    await setScenarios([{ match: 'fibo lighthouse', rounds: [{ text: 'Here you go:\n```json\n' + JSON.stringify(messy) + '\n```' }] }]);
    let reply = await rewrite(llm, { prompt: 'fibo lighthouse', imageModel: FIBO });
    expect(reply.style).toEqual({ id: 'fibo-2', scope: 'bundled' });
    expect(reply.text).toBe('{"short_description":"A lighthouse at dusk.","objects":[{"description":"A tower.","location":"center"}],"style_medium":"photograph"}');
    expect(await systemPrompt()).toContain('You write prompts for Fibo 2');
});

test('MOSS Image gets a pixel layout compiled in code, and a canvas that is not the image size is asked again', async ({ llm }) => {
    let draft = (size: number) => JSON.stringify({ width: size, height: size, bbox_coord_space: 'abs', bbox_format: 'xyxy', summary: { caption: 'A travel poster.' },
        items: [{ label: 'object', caption: 'A small white sailboat.', bbox: [128, 128, 896, 512] }] });
    await setScenarios([{ match: 'moss poster', rounds: [{ text: draft(1024) }] }]);
    let reply = await rewrite(llm, { prompt: 'moss poster', imageModel: MOSS, width: 1024, height: 1024 });
    expect(reply.style).toEqual({ id: 'moss-image', scope: 'bundled' });
    expect(reply.rewritten).toBe(true);
    expect(JSON.parse(reply.text).items[0].bbox).toEqual([-384, -384, 0, 384]);
    expect(JSON.parse(reply.text).bbox_format).toBe('yxyx');

    await clearRequests();
    await setScenarios([{ match: 'refused because', rounds: [{ text: 'A calm travel poster with a small sailboat.' }] }, { match: 'moss wrong size', rounds: [{ text: draft(512) }] }]);
    let again = await rewrite(llm, { prompt: 'moss wrong size', imageModel: MOSS, width: 1024, height: 1024 });
    expect(again.retried).toBe(true);
    expect(again.text).toBe('A calm travel poster with a small sailboat.');
    let retry = (await mockRequests())[1].messages.find((m: any) => m.role === 'user');
    expect(String(retry.content)).toContain('must be the image size, 1024 and 1024');
});

test('Chroma gets its prose instructions, and a rule pinning the model to another style wins', async ({ llm }) => {
    await setScenarios([{ match: 'chroma cat', rounds: [{ text: 'A candid photograph of a tabby cat asleep on a sunlit windowsill.' }] }]);
    let reply = await rewrite(llm, { prompt: 'chroma cat', imageModel: CHROMA });
    expect(reply.style).toEqual({ id: 'chroma', scope: 'bundled' });
    expect(reply.text).toBe('A candid photograph of a tabby cat asleep on a sunlit windowsill.');
    expect(await systemPrompt()).toContain('You write prompts for Chroma');

    await clearRequests();
    await api(llm, 'LLMAssistantSetStyleOverrides', { overrides: [{ axis: 'image', match: CHROMA, style: 'generic' }] });
    let pinned = await resolve(llm, CHROMA);
    expect(pinned.style.id).toBe('generic');
    expect(pinned.via).toBe('override');
    await setScenarios([{ match: 'pinned cat', rounds: [{ text: 'A pinned cat.' }] }]);
    let viaRule = await rewrite(llm, { prompt: 'pinned cat', imageModel: CHROMA });
    expect(viaRule.style.id).toBe('generic');
    expect(await systemPrompt()).toContain('text-to-image prompt expert');
});

test('a pack style can be customized, wins as a personal copy, and resets', async ({ llm }) => {
    let bundled = (await api(llm, 'LLMAssistantGetPromptStyle', { id: 'chroma' })).style;
    expect(bundled.scope).toBe('bundled');
    let saved = await api(llm, 'LLMAssistantSavePromptStyle', { id: 'chroma', kind: 'image', name: 'My Chroma', description: 'mine', matches: 'class:chroma', format: 'text', negative: 'separate', body: 'My own Chroma instructions.' });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
    let resolved = await resolve(llm, CHROMA);
    expect(resolved.style.scope).toBe('personal');
    await setScenarios([{ match: 'custom cat', rounds: [{ text: 'A custom cat.' }] }]);
    await rewrite(llm, { prompt: 'custom cat', imageModel: CHROMA });
    expect(await systemPrompt()).toContain('My own Chroma instructions.');

    expect((await api(llm, 'LLMAssistantDeletePromptStyle', { id: 'chroma', scope: 'personal' })).success).toBe(true);
    let reset = await resolve(llm, CHROMA);
    expect(reset.style.scope).toBe('bundled');
    expect((await api(llm, 'LLMAssistantGetPromptStyle', { id: 'chroma' })).style.body).toBe(bundled.body);
});

test('the Prompt styles tab lists the pack styles with the models they match', async ({ llm }) => {
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('.llma-modal-tab[data-tab="prompt-styles"]').click();
    let image = llm.locator('#llma-styles-image');
    await expect(image.locator('li[data-style-id="flux-3"]')).toContainText('class:flux-3-image-klein');
    await expect(image.locator('li[data-style-id="sdxl"]')).toContainText('Built in');
    await expect(image.locator('li[data-style-id="video-prose"]')).toContainText('compat:wan-21-14b');
    await image.locator('.llma-resolve-model').fill(MOSS);
    await image.locator('.llma-resolve-run').click();
    await expect(image.locator('.llma-resolve-result')).toContainText('MOSS Image');
    await expect(image.locator('.llma-resolve-result')).toContainText('class:moss-image');
});

test('a JSON or layout family leaves a prompt with a bbox tag as written and never asks the model', async ({ llm }) => {
    for (let [model, width] of [[FIBO, 1024], [FLUX3, 1024], [MOSS, 1024]] as [string, number][]) {
        await clearRequests();
        let reply = await rewrite(llm, { prompt: 'a dog <bbox[0.1,0.1,0.5,0.5]:a brown dog> on a beach', imageModel: model, width: width, height: width });
        expect(reply.rewritten, model).toBe(false);
        expect(reply.fellBack, model).toBe(false);
        expect(reply.text).toBe('a dog <bbox[0.1,0.1,0.5,0.5]:a brown dog> on a beach');
        expect(reply.skipped).toContain('left as written');
        expect(reply.warnings.join(' ')).toContain('bbox');
        expect(await mockRequests()).toHaveLength(0);
    }
    // A prose family still rewrites it, and the tag comes back untouched.
    await setScenarios([{ match: 'chroma dog', rounds: [{ text: 'A brown dog {{SWARM_TAG_0}} on a sunny beach.' }] }]);
    let prose = await rewrite(llm, { prompt: 'chroma dog <bbox[0.1,0.1,0.5,0.5]:a brown dog>', imageModel: CHROMA });
    expect(prose.rewritten).toBe(true);
    expect(prose.text).toBe('A brown dog <bbox[0.1,0.1,0.5,0.5]:a brown dog> on a sunny beach.');
});

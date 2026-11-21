import { test as base, expect, setScenarios, mockRequests, clearRequests, api } from './fixtures';
import type { Page } from '@playwright/test';

// The generation hook: a generation's prompt is rewritten for its model right before core parses prompt tags, when the user's setting or the generation's own parameter says to. There is no image backend here, so the
// generation itself fails after the hook ran; what is observed is whether (and how often) the scripted chat model was asked.

const MYSTERY = 'e2e-mystery.safetensors';

const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await clearRequests();
        await setScenarios([]);
        await setAuto(llm, false);
        await use(llm);
        await setAuto(llm, false);
    }
});

async function setAuto(page: Page, on: boolean) {
    let reply = await api(page, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ promptRewrite: { auto: on } }) });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
}

async function paramId(page: Page, name: string): Promise<string> {
    let listed = await api(page, 'ListT2IParams');
    let found = (listed.list ?? []).find((p: any) => p.name == name);
    expect(found, `core lists a "${name}" parameter`).toBeTruthy();
    return found.id;
}

/** Starts a generation. There is no image backend, so it ends in an error after the hook has run; that error is expected. */
async function generate(page: Page, body: Record<string, unknown>) {
    await api(page, 'GenerateText2Image', { images: 1, model: MYSTERY, ...body });
}

test('the two parameters are listed, and the setting is off by default so a plain generation never calls the model', async ({ llm }) => {
    await paramId(llm, 'LLM Auto-Enhance Prompt');
    await paramId(llm, 'LLM Prompt Style');
    await generate(llm, { prompt: 'plain cat' });
    expect(await mockRequests()).toHaveLength(0);
});

test('a generation can turn the rewrite on for itself, and the model is given the style and the idea', async ({ llm }) => {
    await setScenarios([{ match: 'hooked cat', rounds: [{ text: 'A fluffy hooked cat, soft light.' }] }]);
    await generate(llm, { prompt: 'hooked cat', [await paramId(llm, 'LLM Auto-Enhance Prompt')]: 'on' });
    let requests = await mockRequests();
    expect(requests).toHaveLength(1);
    expect(String(requests[0].messages[0].content)).toContain('text-to-image prompt expert');
    expect(String(requests[0].messages.find((m: any) => m.role === 'user').content)).toBe('<idea>\nhooked cat\n</idea>');
});

test('the setting turns it on for every generation, and a generation can still turn it off', async ({ llm }) => {
    await setScenarios([{ match: 'setting cat', rounds: [{ text: 'A setting cat.' }] }]);
    await setAuto(llm, true);
    await generate(llm, { prompt: 'setting cat' });
    expect(await mockRequests()).toHaveLength(1);
    await clearRequests();
    await generate(llm, { prompt: 'other setting cat', [await paramId(llm, 'LLM Auto-Enhance Prompt')]: 'off' });
    expect(await mockRequests()).toHaveLength(0);
});

test('a request for several images makes one request to the model', async ({ llm }) => {
    await setScenarios([{ match: 'batch cat', rounds: [{ text: 'A batch cat.' }] }]);
    await generate(llm, { prompt: 'batch cat', images: 4, [await paramId(llm, 'LLM Auto-Enhance Prompt')]: 'on' });
    expect(await mockRequests()).toHaveLength(1);
});

test('a prompt that was just rewritten is not rewritten again when it is generated from', async ({ llm }) => {
    await setScenarios([{ match: 'twice cat', rounds: [{ text: 'A twice rewritten cat.' }] }]);
    let reply = await api(llm, 'LLMAssistantRewritePrompt', { prompt: 'twice cat', imageModel: MYSTERY });
    expect(reply.text).toBe('A twice rewritten cat.');
    expect(await mockRequests()).toHaveLength(1);
    await generate(llm, { prompt: reply.text, [await paramId(llm, 'LLM Auto-Enhance Prompt')]: 'on' });
    expect(await mockRequests()).toHaveLength(1);
});

test('a generation with no model, and core\'s own tag test, never call the model', async ({ llm }) => {
    await setAuto(llm, true);
    await api(llm, 'GenerateText2Image', { images: 1, prompt: 'no model cat' });
    let fill = await api(llm, 'TestPromptFill', { prompt: 'fill cat' });
    expect(fill.result).toBe('fill cat');
    expect(await mockRequests()).toHaveLength(0);
});

test('a failing model is asked and the generation request still comes back (the hook never throws)', async ({ llm }) => {
    await setScenarios([{ match: 'broken cat', rounds: [{ error: { status: 500, message: 'upstream down' } }] }]);
    await setAuto(llm, true);
    await api(llm, 'GenerateText2Image', { images: 1, model: MYSTERY, prompt: 'broken cat' });
    expect((await mockRequests()).length).toBeGreaterThan(0);
    // The route answered (with core's own no-backend error), so nothing the hook did escaped as an exception.
    let after = await api(llm, 'TestPromptFill', { prompt: 'still works' });
    expect(after.result).toBe('still works');
});

test('the automatic setting is a checkbox in Settings > Prompt styles and is saved to the user's settings', async ({ llm }) => {
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('.llma-modal-tab[data-tab="prompt-styles"]').click();
    let image = llm.locator('#llma-styles-image');
    let auto = image.locator('.llma-rewrite-auto');
    await expect(auto).not.toBeChecked();
    await auto.check();
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetSettings')).settings?.promptRewrite?.auto).toBe(true);
    await auto.uncheck();
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetSettings')).settings?.promptRewrite?.auto).toBe(false);
});

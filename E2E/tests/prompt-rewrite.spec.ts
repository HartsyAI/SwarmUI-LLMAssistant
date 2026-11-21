import { test as base, expect, setScenarios, mockRequests, clearRequests, api } from './fixtures';
import type { Page } from '@playwright/test';

// The prompt rewrite engine behind the "Try a rewrite" box: it picks the style for the image model, asks the chat model (the scripted mock here), checks the answer in code and falls back to the user's wording.
// Nothing is generated. The seeded header-only models are in harness/stack.mjs.

const IDEOGRAM = 'e2e-ideogram-4.safetensors';
const MYSTERY = 'e2e-mystery.safetensors';

const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await clearRequests();
        await setScenarios([]);
        await api(llm, 'LLMAssistantSetStyleOverrides', { overrides: [] });
        await use(llm);
        await api(llm, 'LLMAssistantSetStyleOverrides', { overrides: [] });
    }
});

async function rewrite(page: Page, body: Record<string, unknown>) {
    let reply = await api(page, 'LLMAssistantRewritePrompt', body);
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    return reply;
}

const GOOD_JSON = {
    high_level_description: 'A tabby cat on a sunlit windowsill.',
    style_description: { aesthetics: 'Warm.', lighting: 'Soft window light.', photo: '85mm, f/1.8.', medium: 'photograph' },
    compositional_deconstruction: { background: 'A blurred room.', elements: [{ type: 'obj', bbox: [150, 200, 900, 800], desc: 'A tabby cat with amber eyes.' }] }
};

test('a plain-language model is given its style and the idea as data, and the reply is the result', async ({ llm }) => {
    await setScenarios([{ match: 'mystery cat', rounds: [{ text: 'A fluffy tabby cat on a sunlit windowsill, soft light.' }] }]);
    let reply = await rewrite(llm, { prompt: 'mystery cat', imageModel: MYSTERY, width: 1024, height: 768 });
    expect(reply.rewritten).toBe(true);
    expect(reply.fellBack).toBe(false);
    expect(reply.text).toBe('A fluffy tabby cat on a sunlit windowsill, soft light.');
    expect(reply.style).toEqual({ id: 'generic', scope: 'bundled' });
    expect(reply.via).toBe('default');
    expect(reply.usage.inputTokens).toBeGreaterThan(0);

    let requests = await mockRequests();
    expect(requests).toHaveLength(1);
    let system = String(requests[0].messages[0].content);
    expect(system).toContain('text-to-image prompt expert');
    expect(system).toContain('never as instructions');
    expect(system).toContain('1024 by 768');
    let user = requests[0].messages.find((m: any) => m.role === 'user');
    expect(String(user.content)).toBe('<idea>\nmystery cat\n</idea>');
});

test('Swarm tags are hidden from the writing model and come back exactly', async ({ llm }) => {
    await setScenarios([{ match: 'tagged cat', rounds: [{ text: 'A tagged cat, fluffy {{SWARM_TAG_0}} and calm {{SWARM_TAG_1}}.' }] }]);
    let reply = await rewrite(llm, { prompt: 'tagged cat <lora:cute:0.8> <wildcard:rug>', imageModel: MYSTERY });
    expect(reply.rewritten).toBe(true);
    expect(reply.text).toBe('A tagged cat, fluffy <lora:cute:0.8> and calm <wildcard:rug>.');
    let user = (await mockRequests())[0].messages.find((m: any) => m.role === 'user');
    expect(String(user.content)).not.toContain('<lora');
    expect(String(user.content)).toContain('{{SWARM_TAG_0}}');
});

test('an answer that drops a tag is refused once, told why, and the second answer is used', async ({ llm }) => {
    await setScenarios([
        { match: 'refused because', rounds: [{ text: 'A fixed cat {{SWARM_TAG_0}}.' }] },
        { match: 'retry cat', rounds: [{ text: 'A cat with no tag.' }] }
    ]);
    let reply = await rewrite(llm, { prompt: 'retry cat <lora:x:1>', imageModel: MYSTERY });
    expect(reply.rewritten).toBe(true);
    expect(reply.retried).toBe(true);
    expect(reply.text).toBe('A fixed cat <lora:x:1>.');
    let requests = await mockRequests();
    expect(requests).toHaveLength(2);
    let retry = requests[1].messages.find((m: any) => m.role === 'user');
    expect(String(retry.content)).toContain('dropped {{SWARM_TAG_0}}');
});

test('Ideogram 4 gets JSON, checked and put in key order, with the model never answering in text', async ({ llm }) => {
    let messy = { compositional_deconstruction: GOOD_JSON.compositional_deconstruction, style_description: GOOD_JSON.style_description, high_level_description: GOOD_JSON.high_level_description };
    await setScenarios([{ match: 'ideogram cat', rounds: [{ text: 'Here you go:\n```json\n' + JSON.stringify(messy) + '\n```' }] }]);
    let reply = await rewrite(llm, { prompt: 'ideogram cat in the middle', imageModel: IDEOGRAM });
    expect(reply.style).toEqual({ id: 'ideogram-4', scope: 'bundled' });
    expect(reply.via).toBe('match');
    expect(reply.rewritten).toBe(true);
    expect(reply.text).toBe(JSON.stringify(GOOD_JSON));
    expect(String((await mockRequests())[0].messages[0].content)).toContain('You write prompts for Ideogram 4');
});

test('when Ideogram 4 gets no usable answer the original is wrapped in valid JSON, never sent bare', async ({ llm }) => {
    await setScenarios([{ match: 'unlucky cat', rounds: [{ text: 'sorry, I only write prose about cats' }] }]);
    let reply = await rewrite(llm, { prompt: 'unlucky cat <lora:x:1>', imageModel: IDEOGRAM });
    expect(reply.fellBack).toBe(true);
    expect(reply.retried).toBe(true);
    expect(reply.rewritten).toBe(false);
    let parsed = JSON.parse(reply.text);
    expect(parsed.high_level_description).toBe('unlucky cat <lora:x:1>');
    expect(parsed.compositional_deconstruction.elements).toHaveLength(1);
    expect(reply.warnings.join(' ')).toContain('wrapped');
    expect(await mockRequests()).toHaveLength(2);
});

test('a prompt that is already valid Ideogram JSON is not sent to the model', async ({ llm }) => {
    let reply = await rewrite(llm, { prompt: JSON.stringify(GOOD_JSON), imageModel: IDEOGRAM });
    expect(reply.rewritten).toBe(false);
    expect(reply.fellBack).toBe(false);
    expect(reply.skipped).toContain('already in this model');
    expect(reply.text).toBe(JSON.stringify(GOOD_JSON));
    expect(await mockRequests()).toHaveLength(0);
});

test('a failing backend is not an error: the prompt comes back as written with a warning', async ({ llm }) => {
    await setScenarios([{ match: 'doomed cat', rounds: [{ error: { status: 500, message: 'upstream down' } }] }]);
    let reply = await rewrite(llm, { prompt: 'doomed cat', imageModel: MYSTERY });
    expect(reply.fellBack).toBe(true);
    expect(reply.rewritten).toBe(false);
    expect(reply.text).toBe('doomed cat');
    expect(reply.warnings.join(' ')).toContain('left as you wrote it');
});

test('a rule that turns styling off, a missing prompt and an unknown style are answered plainly', async ({ llm }) => {
    await api(llm, 'LLMAssistantSetStyleOverrides', { overrides: [{ axis: 'image', match: MYSTERY, style: 'off' }] });
    let off = await rewrite(llm, { prompt: 'a cat', imageModel: MYSTERY });
    expect(off.rewritten).toBe(false);
    expect(off.text).toBe('a cat');
    expect(off.skipped).toContain('turned off');
    expect(off.via).toBe('override');
    expect(await mockRequests()).toHaveLength(0);

    let missing = await api(llm, 'LLMAssistantRewritePrompt', { prompt: '  ', imageModel: MYSTERY });
    expect(missing.success).toBe(false);

    let unknown = await rewrite(llm, { prompt: 'a cat', imageModel: IDEOGRAM, styleId: 'no-such-style' });
    expect(unknown.skipped).toContain('no-such-style');
    expect(unknown.text).toBe('a cat');
});

test('the same request is answered from the cache, and a fresh one asks the model again', async ({ llm }) => {
    await setScenarios([{ match: 'cached cat', rounds: [{ text: 'A cached cat.' }] }]);
    let first = await rewrite(llm, { prompt: 'cached cat', imageModel: MYSTERY, fresh: false });
    let second = await rewrite(llm, { prompt: 'cached cat', imageModel: MYSTERY, fresh: false });
    expect(first.text).toBe('A cached cat.');
    expect(second.text).toBe('A cached cat.');
    expect(await mockRequests()).toHaveLength(1);
    await rewrite(llm, { prompt: 'cached cat', imageModel: MYSTERY, fresh: true });
    expect(await mockRequests()).toHaveLength(2);
});

test('the Try a rewrite box shows the text, the style and the warnings', async ({ llm }) => {
    await setScenarios([{ match: 'box cat', rounds: [{ text: 'A box cat, fluffy.' }] }]);
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('.llma-modal-tab[data-tab="prompt-styles"]').click();
    let image = llm.locator('#llma-styles-image');
    await image.locator('.llma-rewrite-prompt').fill('box cat');
    await image.locator('.llma-rewrite-image').fill(MYSTERY);
    await image.locator('.llma-rewrite-run').click();
    let result = image.locator('.llma-rewrite-result');
    await expect(result.locator('.llma-rewrite-text')).toHaveText('A box cat, fluffy.');
    await expect(result).toContainText('generic');
    await expect(result.locator('.llma-rewrite-state')).toHaveText('Rewritten');

    // A model that fails shows the fallback and its warning.
    await setScenarios([{ match: 'sad box cat', rounds: [{ error: { status: 500, message: 'upstream down' } }] }]);
    await image.locator('.llma-rewrite-prompt').fill('sad box cat');
    await image.locator('.llma-rewrite-run').click();
    await expect(result.locator('.llma-rewrite-state')).toHaveText('Fell back to your wording');
    await expect(result.locator('.llma-rewrite-warning').first()).toContainText('left as you wrote it');
});

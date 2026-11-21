import { test as base, expect, setScenarios, mockRequests, clearRequests, api, send } from './fixtures';
import type { Page } from '@playwright/test';

// The chat-model addendum: a style of kind llm is added to the system prompt the model actually answering sees. The mock serves two model ids, so every assertion is on the system prompt it recorded for each
// (through /__requests). No style ships for any model, so the first thing pinned is that nothing changes without one.

const A = 'mock-model';
const B = 'mock-model-b';
const OPT_OUT = 'e2e-addendum-optout';
const VARIANT = 'e2e-addendum-variant';

const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await clean(llm);
        await use(llm);
        await clean(llm);
    }
});

/** Leaves none of the user's styles, rules, switches or test assistants behind, so one spec never leaks into the next. */
async function clean(page: Page) {
    let listed = await api(page, 'LLMAssistantGetPromptStyles');
    for (let style of listed.styles ?? []) {
        for (let scope of style.scopes) {
            if (scope == 'personal' || scope == 'shared') {
                await api(page, 'LLMAssistantDeletePromptStyle', { id: style.id, scope: scope });
            }
        }
    }
    await api(page, 'LLMAssistantSetStyleOverrides', { overrides: [] });
    await api(page, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ promptStyles: { llmAddenda: true, disabled: [] } }) });
    for (let id of [OPT_OUT, VARIANT]) {
        await api(page, 'LLMAssistantDeleteAssistant', { assistantId: id });
    }
    await clearRequests();
    await setScenarios([]);
}

async function saveStyle(page: Page, id: string, matches: string, body: string, extra: Record<string, unknown> = {}) {
    let saved = await api(page, 'LLMAssistantSavePromptStyle', { id: id, kind: 'llm', name: id, matches: matches, body: body, ...extra });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
}

/** Runs one headless turn on a model and returns the system prompt that model was sent. */
async function systemPromptFor(page: Page, model: string, extra: Record<string, unknown> = {}): Promise<string> {
    await clearRequests();
    let message = `question for ${model} ${Math.random().toString(36).slice(2)}`;
    await setScenarios([{ match: message, rounds: [{ text: 'Answered.' }] }]);
    let reply = await api(page, 'LLMAssistantRunTurn', { message: message, model: model, ...extra });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    let requests = (await mockRequests()).filter(r => r.model == model);
    expect(requests.length).toBeGreaterThan(0);
    return requests[0].messages.find((m: any) => m.role === 'system').content;
}

async function saveAssistant(page: Page, id: string, extra: Record<string, unknown>) {
    let saved = await api(page, 'LLMAssistantSaveAssistant', { assistant: { id: id, name: id, icon: 'chat', instructions: {}, ...extra }, scope: 'personal' });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
}

test('with no style written, the system prompt is exactly what it was, and the preview says nothing is added', async ({ llm }) => {
    let plain = await systemPromptFor(llm, A);
    expect(plain.length).toBeGreaterThan(0);
    let resolved = await api(llm, 'LLMAssistantResolvePromptStyle', { axis: 'llm', model: A });
    expect(resolved.style).toBeNull();
    expect(resolved.addendum.applied).toBe(false);
    // A style for a different model changes nothing either.
    await saveStyle(llm, 'other-model', 'exact:not-this-one', 'Not for you.');
    expect(await systemPromptFor(llm, A)).toBe(plain);
});

test('a style for a model is added after the instruction, exactly once, and the same bytes every turn', async ({ llm }) => {
    let plain = await systemPromptFor(llm, A);
    await saveStyle(llm, 'a-only', `exact:${A}`, 'Answer in one short sentence.\r\nUse the tools sparingly.\r\n');
    let first = await systemPromptFor(llm, A);
    expect(first).toBe(`${plain.replace(/[\r\n]+$/, '')}\n\nAnswer in one short sentence.\nUse the tools sparingly.`);
    for (let i = 0; i < 2; i++) {
        expect(await systemPromptFor(llm, A)).toBe(first);
    }
    // The skills index, project instructions and session note, when there are any, come after it: the addendum sits directly after the instruction.
    expect(first.indexOf('Answer in one short sentence.')).toBeGreaterThan(plain.replace(/[\r\n]+$/, '').length - 1);
});

test('two models each get their own addendum, and a model with none gets none', async ({ llm }) => {
    let plain = await systemPromptFor(llm, A);
    await saveStyle(llm, 'for-a', `exact:${A}`, 'NOTE FOR MODEL A.');
    await saveStyle(llm, 'for-b', `exact:${B}`, 'NOTE FOR MODEL B.');
    let forA = await systemPromptFor(llm, A);
    let forB = await systemPromptFor(llm, B);
    expect(forA).toContain('NOTE FOR MODEL A.');
    expect(forA).not.toContain('NOTE FOR MODEL B.');
    expect(forB).toContain('NOTE FOR MODEL B.');
    expect(forB).not.toContain('NOTE FOR MODEL A.');
    // Switching the model changes the next request only: A is unchanged afterwards.
    expect(await systemPromptFor(llm, A)).toBe(forA);
    await api(llm, 'LLMAssistantDeletePromptStyle', { id: 'for-b', scope: 'personal' });
    expect(await systemPromptFor(llm, B)).toBe(plain);
});

test('each lane of a compare turn is told about its own model', async ({ llm }) => {
    await saveStyle(llm, 'for-a', `exact:${A}`, 'LANE A ADDENDUM.');
    await saveStyle(llm, 'for-b', `exact:${B}`, 'LANE B ADDENDUM.');
    await setScenarios([
        { match: 'compare me', model: A, rounds: [{ text: 'lane A says hi' }] },
        { match: 'compare me', model: B, rounds: [{ text: 'lane B says hi' }] }
    ]);
    await clearRequests();
    await llm.locator('#llma-compare-toggle').click();
    await llm.locator('#llma-model-select-b').selectOption(B);
    await send(llm, 'compare me');
    await expect(llm.locator('body')).toContainText('lane A says hi');
    await expect(llm.locator('body')).toContainText('lane B says hi');
    let requests = await mockRequests();
    let system = (model: string) => requests.find(r => r.model == model).messages.find((m: any) => m.role === 'system').content;
    expect(system(A)).toContain('LANE A ADDENDUM.');
    expect(system(A)).not.toContain('LANE B ADDENDUM.');
    expect(system(B)).toContain('LANE B ADDENDUM.');
    expect(system(B)).not.toContain('LANE A ADDENDUM.');
});

test('a chat in the interface gets the addendum of the model chosen in the composer', async ({ llm }) => {
    await saveStyle(llm, 'for-a', `exact:${A}`, 'COMPOSER ADDENDUM.');
    await setScenarios([{ match: 'typed hello', rounds: [{ text: 'Hi.' }] }]);
    await clearRequests();
    await send(llm, 'typed hello');
    await expect(llm.locator('.llma-msg-row.assistant').last()).toContainText('Hi.');
    let requests = await mockRequests();
    expect(requests[0].messages.find((m: any) => m.role === 'system').content).toContain('COMPOSER ADDENDUM.');
});

test('the master switch, a style\'s own switch, a rule, and an assistant\'s opt-out each stop it', async ({ llm }) => {
    let plain = await systemPromptFor(llm, A);
    await saveStyle(llm, 'for-a', `exact:${A}`, 'SWITCHED NOTE.');
    let on = await systemPromptFor(llm, A);
    expect(on).toContain('SWITCHED NOTE.');

    // Master switch.
    await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ promptStyles: { llmAddenda: false } }) });
    expect(await systemPromptFor(llm, A)).toBe(plain);
    expect((await api(llm, 'LLMAssistantResolvePromptStyle', { axis: 'llm', model: A })).addendum.reason).toContain('switched off');
    await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ promptStyles: { llmAddenda: true } }) });
    expect(await systemPromptFor(llm, A)).toBe(on);

    // The style's own switch.
    await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ promptStyles: { disabled: ['for-a'] } }) });
    expect(await systemPromptFor(llm, A)).toBe(plain);
    await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ promptStyles: { disabled: [] } }) });
    expect(await systemPromptFor(llm, A)).toBe(on);

    // A rule that sets the model to no style.
    await api(llm, 'LLMAssistantSetStyleOverrides', { overrides: [{ axis: 'llm', match: A, style: 'off' }] });
    expect(await systemPromptFor(llm, A)).toBe(plain);
    await api(llm, 'LLMAssistantSetStyleOverrides', { overrides: [] });

    // An assistant that opts out; the default assistant is untouched.
    await saveAssistant(llm, OPT_OUT, { promptAddendum: false });
    expect(await systemPromptFor(llm, A, { assistantId: OPT_OUT })).not.toContain('SWITCHED NOTE.');
    expect(await systemPromptFor(llm, A)).toBe(on);
    expect((await api(llm, 'LLMAssistantResolvePromptStyle', { axis: 'llm', model: A, assistantId: OPT_OUT })).addendum.reason).toContain('assistant');
});

test('a more specific instruction variant of the assistant is used alone, and a rule still overrides it', async ({ llm }) => {
    await saveStyle(llm, 'by-glob', 'glob:mock-*', 'GLOB ADDENDUM.');
    // This assistant wrote its own instruction for exactly mock-model-b (exact, more specific than the style's glob); for mock-model it has only the default text.
    await saveAssistant(llm, VARIANT, { instructions: { chat: { default: 'Default text.', variants: [{ match: B, matchKind: 'exact', text: 'Text written for B.' }] } } });
    let forA = await systemPromptFor(llm, A, { assistantId: VARIANT });
    expect(forA).toContain('Default text.');
    expect(forA).toContain('GLOB ADDENDUM.');
    let forB = await systemPromptFor(llm, B, { assistantId: VARIANT });
    expect(forB).toContain('Text written for B.');
    expect(forB).not.toContain('GLOB ADDENDUM.');
    let preview = await api(llm, 'LLMAssistantResolvePromptStyle', { axis: 'llm', model: B, assistantId: VARIANT });
    expect(preview.addendum.applied).toBe(false);
    expect(preview.addendum.reason).toContain('more specific');
    // A rule the user made for the model always applies.
    await api(llm, 'LLMAssistantSetStyleOverrides', { overrides: [{ axis: 'llm', match: B, style: 'by-glob' }] });
    expect(await systemPromptFor(llm, B, { assistantId: VARIANT })).toContain('GLOB ADDENDUM.');
});

test('a style limited to tools is added only when the turn offers tools', async ({ llm }) => {
    await saveStyle(llm, 'tools-only', `exact:${A}`, 'TOOL RULES.', { tasks: 'tools' });
    expect(await systemPromptFor(llm, A)).toContain('TOOL RULES.');
    await saveAssistant(llm, OPT_OUT, { toolsEnabled: false });
    expect(await systemPromptFor(llm, A, { assistantId: OPT_OUT })).not.toContain('TOOL RULES.');
});

test('the dry run reports the addendum and its tokens, which are inside the prompt estimate', async ({ llm }) => {
    let plain = await api(llm, 'LLMAssistantRunTurn', { message: 'just looking', model: A, dryRun: true });
    expect(plain.success, JSON.stringify(plain)).toBe(true);
    expect(plain.promptAddendum).toMatchObject({ applied: false, chars: 0, estimatedTokens: 0 });
    let body = 'Tighter tool-call rules for a small model. '.repeat(20).trim();
    await saveStyle(llm, 'for-a', `exact:${A}`, body);
    let withStyle = await api(llm, 'LLMAssistantRunTurn', { message: 'just looking', model: A, dryRun: true });
    expect(withStyle.promptAddendum).toMatchObject({ applied: true, styleId: 'for-a', scope: 'personal', via: 'match', chars: body.length + 2, estimatedTokens: Math.ceil((body.length + 2) / 4) });
    expect(withStyle.systemPromptChars - plain.systemPromptChars).toBe(body.length + 2);
    expect(withStyle.estimatedPromptTokens).toBeGreaterThan(plain.estimatedPromptTokens);
    // Nothing was called by a dry run.
    expect(await mockRequests()).toHaveLength(0);
});

test('the preview in Settings > Prompt styles shows the winning style, why, and the cost', async ({ llm }) => {
    await saveStyle(llm, 'for-a', 'glob:mock-*', 'Preview text for the chat model.');
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('.llma-modal-tab[data-tab="prompt-styles"]').click();
    let box = llm.locator('#llma-styles-llm');
    await expect(box.locator('li[data-style-id="for-a"]')).toContainText('glob:mock-*');
    await box.locator('.llma-resolve-model').fill(A);
    await box.locator('.llma-resolve-run').click();
    let result = box.locator('.llma-resolve-result');
    await expect(result).toContainText('for-a');
    await expect(result).toContainText('glob:mock-*');
    await expect(result.locator('.llma-addendum-state')).toHaveText('Added to the system prompt');
    await expect(result).toContainText('tokens');
    // A model no style matches.
    await box.locator('.llma-resolve-model').fill('gpt-unmatched');
    await box.locator('.llma-resolve-run').click();
    await expect(result.locator('.llma-addendum-state')).toHaveText('Nothing is added to the system prompt');
});

test('the master switch and a style\'s switch in Settings are saved and take effect', async ({ llm }) => {
    await saveStyle(llm, 'for-a', `exact:${A}`, 'UI SWITCH NOTE.');
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('.llma-modal-tab[data-tab="prompt-styles"]').click();
    let box = llm.locator('#llma-styles-llm');
    let master = box.locator('.llma-addenda-master');
    await expect(master).toBeChecked();
    let row = box.locator('li[data-style-id="for-a"]');
    let styleSwitch = row.locator('.llma-style-enabled');
    await expect(styleSwitch).toBeChecked();

    await styleSwitch.uncheck();
    await expect.poll(async () => (await api(llm, 'LLMAssistantResolvePromptStyle', { axis: 'llm', model: A })).addendum.applied).toBe(false);
    await box.locator('.llma-resolve-model').fill(A);
    await box.locator('.llma-resolve-run').click();
    await expect(box.locator('.llma-resolve-result .llma-addendum-state')).toHaveText('Nothing is added to the system prompt');
    await box.locator('li[data-style-id="for-a"] .llma-style-enabled').check();
    await expect.poll(async () => (await api(llm, 'LLMAssistantResolvePromptStyle', { axis: 'llm', model: A })).addendum.applied).toBe(true);

    await box.locator('.llma-addenda-master').uncheck();
    await expect.poll(async () => (await api(llm, 'LLMAssistantResolvePromptStyle', { axis: 'llm', model: A })).addendum.reason).toContain('switched off');
    await box.locator('.llma-addenda-master').check();
    await expect.poll(async () => (await api(llm, 'LLMAssistantResolvePromptStyle', { axis: 'llm', model: A })).addendum.applied).toBe(true);
});

test('an assistant can opt out in its editor, and the saved choice is kept', async ({ llm }) => {
    await saveAssistant(llm, OPT_OUT, { promptAddendum: true });
    expect((await api(llm, 'LLMAssistantGetAssistant', { assistantId: OPT_OUT })).assistant.promptAddendum).toBe(true);
    await saveAssistant(llm, OPT_OUT, { promptAddendum: false });
    expect((await api(llm, 'LLMAssistantGetAssistant', { assistantId: OPT_OUT })).assistant.promptAddendum).toBe(false);
    await llm.locator('#llma-settings-btn').click();
    await expect(llm.locator('#llma-asst-prompt-addendum')).toHaveCount(1);
});

test('the chat-model section fits a phone width without scrolling sideways', async ({ llm }) => {
    // The page uses core's theme and Bootstrap classes only (no colors of its own), so this checks layout; the screenshot is kept for a look.
    await saveStyle(llm, 'a-fairly-long-style-id-for-layout', 'glob:some-very-long-model-name-pattern-*', 'Text.', { description: 'A long description that has to wrap on a narrow screen without pushing the page wider than it is.' });
    await llm.setViewportSize({ width: 375, height: 800 });
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('.llma-modal-tab[data-tab="prompt-styles"]').click();
    let box = llm.locator('#llma-styles-llm');
    await expect(box.locator('.llma-addenda-master')).toBeAttached();
    await expect(box.locator('li[data-style-id]').first()).toBeAttached();
    await box.locator('.llma-addenda-master').scrollIntoViewIfNeeded();
    let overflow = await llm.evaluate(() => {
        let panel = document.getElementById('llma-settings-prompt-styles') as HTMLElement;
        let right = panel.getBoundingClientRect().right;
        let worst = Math.max(...[...panel.querySelectorAll('li, button, input, select')].map(el => el.getBoundingClientRect().right));
        return { scroll: panel.scrollWidth - panel.clientWidth, past: worst - right };
    });
    expect(overflow.scroll).toBeLessThanOrEqual(1);
    expect(overflow.past).toBeLessThanOrEqual(1);
    await llm.screenshot({ path: 'test-results/llm-addendum-phone.png' });
    await llm.setViewportSize({ width: 1280, height: 720 });
});

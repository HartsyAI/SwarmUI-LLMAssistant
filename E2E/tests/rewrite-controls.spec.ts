import { test as base, expect, setScenarios, mockRequests, clearRequests, api, send, lastAssistant, reopen, turn } from './fixtures';
import type { Page } from '@playwright/test';

// Rewrite controls: which switch decides whether a generation's prompt is rewritten (per call, per chat, per assistant, the model's rule, the entry point, the main switch), the composer's popover and preview,
// the assistant editor and the entry point switches in Settings. There is no image backend, so a generation fails after the generation hook ran; what is observed is whether the scripted chat model was asked for a
// rewrite. Every case uses its own prompt, so the rewrite cache never answers for the model.

const MYSTERY = 'e2e-mystery.safetensors';

const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await clean(llm);
        await use(llm);
        await clean(llm);
    }
});

/** Puts back everything the specs here change: the switches, the model rules and the default assistant. */
async function clean(page: Page) {
    await setSwitches(page, false);
    expect((await api(page, 'LLMAssistantSetStyleOverrides', { overrides: [] })).success).toBe(true);
    await api(page, 'LLMAssistantDeleteAssistant', { assistantId: 'default', scope: 'personal' });
    await clearRequests();
    await setScenarios([]);
}

async function setSwitches(page: Page, auto: boolean, chat = 'default', generate = 'default') {
    let reply = await api(page, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ promptRewrite: { auto: auto, entryPoints: { chat: chat, generate: generate } } }) });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
}

async function setAssistant(page: Page, autoEnhance: boolean | null) {
    let current = (await api(page, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant;
    let saved = await api(page, 'LLMAssistantSaveAssistant', { assistant: { ...current, autoEnhance: autoEnhance }, scope: 'personal' });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
}

/** The requests that asked the model to rewrite a prompt (a chat turn and a title are requests too). */
async function rewrites(): Promise<any[]> {
    return (await mockRequests()).filter(r => (r.messages ?? []).some((m: any) => m.role === 'user' && String(m.content).startsWith('<idea>')));
}

/** Sends a chat message that makes the model call generate_image with a prompt of its own, and returns the rewrite requests that caused. */
async function draw(page: Page, label: string, args: Record<string, unknown> = {}): Promise<any[]> {
    await clearRequests();
    await setScenarios([
        { match: `idea-${label}`, rounds: [{ text: `A rewritten picture ${label}.` }] },
        turn(`draw ${label}`, [{ name: 'generate_image', arguments: { prompt: `idea-${label}`, model: MYSTERY, ...args } }], `Done ${label}.`),
    ]);
    await send(page, `draw ${label}`);
    await expect(lastAssistant(page)).toContainText(`Done ${label}.`);
    return await rewrites();
}

/** The same through the Generate tab's route. */
async function generate(page: Page, label: string, body: Record<string, unknown> = {}): Promise<any[]> {
    await clearRequests();
    await setScenarios([{ match: `idea-${label}`, rounds: [{ text: `A rewritten picture ${label}.` }] }]);
    await api(page, 'GenerateText2Image', { images: 1, model: MYSTERY, prompt: `idea-${label}`, ...body });
    return await rewrites();
}

async function activeThread(page: Page): Promise<any> {
    let id = await page.evaluate(() => (0, eval)('LLMAState').activeThreadId);
    return (await api(page, 'LLMAssistantGetThread', { threadId: id })).thread;
}

async function openPopover(page: Page) {
    await page.locator('#llma-enhance-btn').click();
    await expect(page.locator('#llma-enhance-popover')).toBeVisible();
}

async function chooseForChat(page: Page, value: 'default' | 'on' | 'off') {
    await openPopover(page);
    await page.locator(`label[for="llma-enhance-chat-${value}"]`).click();
    await expect(page.locator(`#llma-enhance-chat-${value}`)).toBeChecked();
    await page.keyboard.press('Escape');
    await expect(page.locator('#llma-enhance-popover')).toBeHidden();
}

test('everything off by default: neither the image tool nor the Generate tab asks for a rewrite', async ({ llm }) => {
    expect(await draw(llm, 'default-off')).toHaveLength(0);
    expect(await generate(llm, 'default-off-tab')).toHaveLength(0);
});

test('every off switch stops the image tool\'s rewrite while the switches below it say on', async ({ llm }) => {
    await setSwitches(llm, true);
    expect(await draw(llm, 'main-on')).toHaveLength(1);

    // The entry point.
    await setSwitches(llm, true, 'off');
    expect(await draw(llm, 'entry-off')).toHaveLength(0);

    // The model's rule of no style, above an entry point that says on.
    await setSwitches(llm, true, 'on');
    await api(llm, 'LLMAssistantSetStyleOverrides', { overrides: [{ axis: 'image', match: MYSTERY, style: 'off' }] });
    expect(await draw(llm, 'model-off')).toHaveLength(0);
    await api(llm, 'LLMAssistantSetStyleOverrides', { overrides: [] });
    expect(await draw(llm, 'model-back')).toHaveLength(1);

    // The assistant.
    await setAssistant(llm, false);
    expect(await draw(llm, 'assistant-off')).toHaveLength(0);

    // The chat.
    await setAssistant(llm, true);
    await chooseForChat(llm, 'off');
    expect(await draw(llm, 'chat-off')).toHaveLength(0);

    // The call, above a chat that says on.
    await chooseForChat(llm, 'on');
    expect(await draw(llm, 'chat-on')).toHaveLength(1);
    expect(await draw(llm, 'call-off', { rewrite: 'off' })).toHaveLength(0);
});

test('per chat beats per assistant, which beats the entry point and the main switch', async ({ llm }) => {
    // The assistant turns it on although the main switch and the chats switch are off.
    await setSwitches(llm, false, 'off');
    await setAssistant(llm, true);
    expect(await draw(llm, 'assistant-wins')).toHaveLength(1);
    // The chat turns it off again.
    await chooseForChat(llm, 'off');
    expect(await draw(llm, 'chat-wins')).toHaveLength(0);
    // And on over an assistant that says off.
    await setSwitches(llm, true);
    await setAssistant(llm, false);
    await chooseForChat(llm, 'default');
    expect(await draw(llm, 'assistant-beats-main')).toHaveLength(0);
    await chooseForChat(llm, 'on');
    expect(await draw(llm, 'chat-beats-assistant')).toHaveLength(1);
});

test('the chats and Generate tab switches are separate, and a chat or assistant switch does not reach the Generate tab', async ({ llm }) => {
    await setSwitches(llm, false, 'on', 'default');
    await setAssistant(llm, true);
    expect(await generate(llm, 'tab-follows-main')).toHaveLength(0);
    expect(await draw(llm, 'chat-entry-on')).toHaveLength(1);
    await setSwitches(llm, true, 'default', 'off');
    await setAssistant(llm, null);
    expect(await generate(llm, 'tab-off')).toHaveLength(0);
    expect(await draw(llm, 'chat-follows-main')).toHaveLength(1);
    await setSwitches(llm, false, 'default', 'on');
    expect(await generate(llm, 'tab-on')).toHaveLength(1);
    let param = (await api(llm, 'ListT2IParams')).list.find((p: any) => p.name == 'LLM Auto-Enhance Prompt').id;
    expect(await generate(llm, 'tab-call-off', { [param]: 'off' })).toHaveLength(0);
});

test('the chat switch is chosen before the first message, stored with the chat, and kept per chat across a reload', async ({ llm }) => {
    await expect(llm.locator('#llma-enhance-label')).toHaveText('Off');
    await chooseForChat(llm, 'on');
    await expect(llm.locator('#llma-enhance-label')).toHaveText('On');
    expect(await draw(llm, 'first-message')).toHaveLength(1);
    let first = await activeThread(llm);
    expect(first.autoEnhance).toBe(true);

    await llm.locator('#llma-new-thread-btn').click();
    let card = llm.locator('.llma-assistant-card').first();
    if (await card.isVisible()) {
        await card.click();
    }
    await expect(llm.locator('#llma-enhance-label')).toHaveText('Off');
    await openPopover(llm);
    await expect(llm.locator('#llma-enhance-chat-default')).toBeChecked();
    await llm.keyboard.press('Escape');
    expect(await draw(llm, 'second-chat')).toHaveLength(0);
    let second = await activeThread(llm);
    expect(second.id).not.toBe(first.id);
    expect(second.autoEnhance).toBeUndefined();

    await reopen(llm);
    await llm.evaluate(id => (0, eval)('llmaSwitchThread')(id), first.id);
    await expect(llm.locator('#llma-enhance-label')).toHaveText('On');
    await openPopover(llm);
    await expect(llm.locator('#llma-enhance-chat-on')).toBeChecked();
    await expect(llm.locator('#llma-enhance-status')).toContainText('this chat');
    await llm.locator('label[for="llma-enhance-chat-default"]').click();
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetThread', { threadId: first.id })).thread.autoEnhance ?? null).toBeNull();
});

test('the All chats switch in the popover is the chats switch in the settings', async ({ llm }) => {
    await openPopover(llm);
    await llm.locator('#llma-enhance-all').selectOption('on');
    await expect(llm.locator('#llma-enhance-label')).toHaveText('On');
    await expect(llm.locator('#llma-enhance-status')).toContainText('All chats');
    expect((await api(llm, 'LLMAssistantGetSettings')).settings.promptRewrite.entryPoints.chat).toBe('on');
});

test('the preview shows the original beside the rewrite, with the style, and generates nothing, even when switched off', async ({ llm }) => {
    await setScenarios([{ match: 'preview cat', rounds: [{ text: 'A luminous preview cat on a sill.' }] }]);
    await llm.locator('#llma-input').fill('preview cat');
    await openPopover(llm);
    await llm.locator('#llma-enhance-preview-btn').click();
    let dialog = llm.locator('#llma-rewrite-overlay');
    await expect(dialog).toBeVisible();
    await expect(llm.locator('#llma-rewrite-preview-prompt')).toHaveValue('preview cat');
    await llm.locator('#llma-rewrite-preview-model').fill(MYSTERY);
    await clearRequests();
    await llm.locator('#llma-rewrite-preview-run').click();
    await expect(dialog.locator('.llma-preview-rewritten')).toHaveText('A luminous preview cat on a sill.');
    await expect(dialog.locator('.llma-preview-original')).toHaveText('preview cat');
    await expect(llm.locator('#llma-rewrite-preview-result')).toContainText('generic');
    await expect(llm.locator('#llma-rewrite-preview-policy')).toContainText('Off');
    expect(await mockRequests()).toHaveLength(1);
    expect(await rewrites()).toHaveLength(1);
    await llm.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect(llm.locator('#llma-input')).toHaveValue('preview cat');
});

test('the preview shows the warnings of a rewrite that fell back', async ({ llm }) => {
    await setScenarios([{ match: 'failing preview', rounds: [{ error: { status: 500, message: 'upstream down' } }] }]);
    await llm.locator('#llma-input').fill('failing preview');
    await openPopover(llm);
    await llm.locator('#llma-enhance-preview-btn').click();
    await llm.locator('#llma-rewrite-preview-run').click();
    await expect(llm.locator('#llma-rewrite-preview-result .llma-rewrite-state')).toContainText('Fell back');
    await expect(llm.locator('#llma-rewrite-preview-result .llma-rewrite-warning').first()).toBeVisible();
});

test('the policy route says which level decides, and a chat switch is set and cleared through the API', async ({ llm }) => {
    let policy = await api(llm, 'LLMAssistantGetRewritePolicy', {});
    expect(policy).toMatchObject({ success: true, run: false, decidedBy: 'master', entryPoint: 'chat', settings: { auto: false, chat: 'default', generate: 'default' } });
    let thread = (await api(llm, 'LLMAssistantCreateThread', { assistantId: 'default', autoEnhance: 'on' })).thread;
    expect(thread.autoEnhance).toBe(true);
    policy = await api(llm, 'LLMAssistantGetRewritePolicy', { threadId: thread.id });
    expect(policy).toMatchObject({ run: true, decidedBy: 'chat' });
    policy = await api(llm, 'LLMAssistantGetRewritePolicy', { threadId: thread.id, autoEnhance: 'off' });
    expect(policy).toMatchObject({ run: false, decidedBy: 'call' });
    expect((await api(llm, 'LLMAssistantGetRewritePolicy', { threadId: thread.id, entryPoint: 'generate' }))).toMatchObject({ run: false, decidedBy: 'master' });
    expect((await api(llm, 'LLMAssistantSetThreadAutoEnhance', { threadId: thread.id, enabled: null })).autoEnhance).toBeNull();
    await setAssistant(llm, true);
    expect((await api(llm, 'LLMAssistantGetRewritePolicy', { threadId: thread.id }))).toMatchObject({ run: true, decidedBy: 'assistant' });
    await api(llm, 'LLMAssistantSetStyleOverrides', { overrides: [{ axis: 'image', match: MYSTERY, style: 'off' }] });
    await setAssistant(llm, null);
    expect((await api(llm, 'LLMAssistantGetRewritePolicy', { threadId: thread.id, imageModel: MYSTERY }))).toMatchObject({ run: false, decidedBy: 'model' });
    expect((await api(llm, 'LLMAssistantSetThreadAutoEnhance', { threadId: thread.id, enabled: 'yes' })).error).toContain('true, false or null');
    expect((await api(llm, 'LLMAssistantGetRewritePolicy', { threadId: 'nope' })).error).toContain('not found');
});

test('the assistant editor sets this assistant\'s switch and Default clears it', async ({ llm }) => {
    let own = async () => (await api(llm, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant.autoEnhance ?? null;
    let open = async () => {
        await llm.evaluate(async () => { await (0, eval)('llmaLoadAssistants')(); (0, eval)('llmaShowAssistantEditor')((0, eval)('LLMAState').assistants.find((a: any) => a.id == 'default')); });
    };
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('.llma-modal-tab[data-tab="assistants"]').click();
    await open();
    await expect(llm.locator('#llma-asst-auto-enhance')).toHaveValue('default');
    await llm.locator('#llma-asst-auto-enhance').selectOption('on');
    await llm.locator('#llma-asst-save').click();
    await expect.poll(own).toBe(true);
    await open();
    await expect(llm.locator('#llma-asst-auto-enhance')).toHaveValue('on');
    await llm.locator('#llma-asst-auto-enhance').selectOption('default');
    await llm.locator('#llma-asst-save').click();
    await expect.poll(own).toBeNull();
    await llm.locator('#llma-settings-close').click();
});

test('Settings > Prompt styles has a switch for chats and one for the Generate tab', async ({ llm }) => {
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('.llma-modal-tab[data-tab="prompt-styles"]').click();
    let box = llm.locator('#llma-styles-image');
    await box.locator('.llma-rewrite-entry[data-entry="chat"]').selectOption('on');
    await box.locator('.llma-rewrite-entry[data-entry="generate"]').selectOption('off');
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetSettings')).settings?.promptRewrite?.entryPoints).toEqual({ chat: 'on', generate: 'off' });
    // Saving the main switch afterwards keeps them.
    await box.locator('.llma-rewrite-auto').check();
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetSettings')).settings?.promptRewrite?.auto).toBe(true);
    expect((await api(llm, 'LLMAssistantGetSettings')).settings.promptRewrite.entryPoints).toEqual({ chat: 'on', generate: 'off' });
    await llm.locator('#llma-settings-close').click();
    await expect(llm.locator('#llma-enhance-label')).toHaveText('On');
});

test('the popover and the preview fit a phone width', async ({ llm }) => {
    await llm.setViewportSize({ width: 375, height: 740 });
    await openPopover(llm);
    let inside = async (selector: string) => {
        let box = (await llm.locator(selector).boundingBox())!;
        expect(box.x, selector).toBeGreaterThanOrEqual(0);
        expect(box.x + box.width, selector).toBeLessThanOrEqual(376);
        expect(box.y, selector).toBeGreaterThanOrEqual(0);
        expect(box.y + box.height, selector).toBeLessThanOrEqual(741);
    };
    await inside('#llma-enhance-popover');
    await llm.screenshot({ path: 'test-results/rewrite-popover-phone.png' });
    await llm.locator('#llma-enhance-preview-btn').click();
    await llm.locator('#llma-rewrite-preview-prompt').fill('phone cat');
    await llm.locator('#llma-rewrite-preview-run').click();
    await expect(llm.locator('.llma-preview-rewritten')).toBeVisible();
    await inside('.llma-rewrite-modal');
    await llm.screenshot({ path: 'test-results/rewrite-preview-phone.png' });
});

import { test, expect, send, lastAssistant, setScenarios, api, reopen, setMode, useDocumentCaps } from './fixtures';
import type { Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// One control for one job, nothing drawn off the screen, and a chat that reads the same after a reload as it did live.

const userRows = (page: Page) => page.locator('#llma-messages .llma-msg-row.user');
const replyRows = (page: Page) => page.locator('#llma-messages .llma-msg-row.assistant');
const tab = (page: Page, name: string) => page.locator(`.llma-modal-tab[data-tab="${name}"]`);

async function openSettings(page: Page, name = 'general') {
    await page.locator('#llma-settings-btn').click();
    await expect(page.locator('#llma-settings-overlay')).toBeVisible();
    await tab(page, name).click();
}

/** What a reply shows, in order: its reasoning blocks, tool groups and text. */
async function shape(page: Page): Promise<string[]> {
    return await page.locator('#llma-messages .llma-msg-row.assistant .llma-msg-bubble').last().evaluate(bubble =>
        Array.from(bubble.querySelectorAll('.llma-think, .llma-tool-group, .llma-msg-text-segment > p')).map(el => {
            if (el.classList.contains('llma-think')) {
                return `think: ${el.querySelector('.llma-think-body')!.textContent!.trim()}`;
            }
            if (el.classList.contains('llma-tool-group')) {
                return `tools: ${el.querySelector('.llma-tool-group-summary')!.textContent}`;
            }
            return `text: ${el.textContent!.trim()}`;
        }));
}

const ROUNDS = [
    { text: '<think>first I list</think>', toolCalls: [{ name: 'memory_read', arguments: {} }] },
    { text: '<think>then I read</think>\n\nLooking again.', toolCalls: [{ name: 'memory_read', arguments: {} }] },
    { text: '<think>now I answer</think>\n\nThe answer.' }
];

// ---- the settings dialog ----

for (let size of [{ width: 1366, height: 768 }, { width: 1440, height: 900 }, { width: 390, height: 844 }, { width: 844, height: 390 }]) {
    test(`the settings dialog fits a ${size.width}x${size.height} window, and its footer stays in view on long tabs`, async ({ llm }) => {
        await llm.setViewportSize(size);
        await openSettings(llm);
        for (let name of ['general', 'tools', 'companion']) {
            await tab(llm, name).click();
            await expect(llm.locator(name == 'companion' ? '#llma-settings-save' : '#llma-settings-reset')).toBeInViewport({ ratio: 1 });
            await expect(llm.locator('#llma-settings-close')).toBeInViewport({ ratio: 1 });
            await expect(tab(llm, name)).toBeInViewport({ ratio: 0.9 });
        }
        let box = (await llm.locator('.llma-settings-modal').boundingBox())!;
        expect(box.y).toBeGreaterThanOrEqual(0);
        expect(box.y + box.height).toBeLessThanOrEqual(size.height + 0.5);
        // The content is what scrolls.
        await tab(llm, 'general').click();
        expect(await llm.locator('.llma-settings-modal .llma-modal-body').evaluate(el => el.scrollHeight > el.clientHeight)).toBe(true);
    });
}

test('on a phone the settings tabs are one row that scrolls, and the tab showing is brought into view', async ({ llm }) => {
    await llm.setViewportSize({ width: 390, height: 844 });
    await openSettings(llm);
    let tops = await llm.locator('.llma-modal-tab').evaluateAll(tabs => [...new Set(tabs.map(t => Math.round(t.getBoundingClientRect().top)))]);
    expect(tops).toHaveLength(1);
    expect((await llm.locator('.llma-modal-tabs').boundingBox())!.height).toBeLessThan(70);
    // The tool cards are one column, not two with the second cut off.
    await tab(llm, 'tools').click();
    await expect(llm.locator('.llma-tool-list-item').nth(1)).toBeAttached();
    expect(await llm.locator('.llma-settings-modal .llma-modal-body').evaluate(el => el.scrollWidth - el.clientWidth)).toBeLessThanOrEqual(0);
    expect(await llm.locator('.llma-tool-list-item').evaluateAll(cards => new Set(cards.map(c => Math.round(c.getBoundingClientRect().left))).size)).toBe(1);
    await tab(llm, 'companion').click();
    await expect(tab(llm, 'companion')).toBeInViewport({ ratio: 1 });
    await llm.locator('#llma-settings-close').click();
    await llm.locator('#llma-settings-btn').click();
    await expect(tab(llm, 'companion')).toBeInViewport({ ratio: 1 });
});

// Who may write the shared settings resets those (their own stay); anyone else resets their own. Each shows where it changes something.
const SAVE_ON = ['general', 'companion'];
const RESET_ON = {
    shared: ['general', 'assistants', 'tools', 'hooks', 'mcp'],
    own: ['general', 'assistants', 'tools', 'hooks', 'skills', 'prompt-styles', 'usage', 'companion']
};
const TABS = ['general', 'assistants', 'tools', 'hooks', 'mcp', 'skills', 'prompt-styles', 'schedules', 'knowledge', 'memory', 'usage', 'companion'];

for (let layer of ['shared', 'own'] as const) {
    test(`each footer button shows only where it applies, resetting the ${layer} settings`, async ({ llm }) => {
        if (layer == 'own') {
            // Both lists say whether the user may write the shared settings.
            await llm.route(/API\/LLMAssistant(GetAssistants|GetTools)$/, async route => {
                let response = await route.fetch();
                await route.fulfill({ response, json: { ...await response.json(), canWriteShared: false } });
            });
            await reopen(llm);
        }
        await openSettings(llm);
        let save = llm.locator('#llma-settings-save');
        let reset = llm.locator('#llma-settings-reset');
        await expect(reset).toHaveText(layer == 'own' ? 'Reset My Defaults' : 'Reset Shared Defaults');
        for (let name of TABS) {
            await tab(llm, name).click();
            let saves = SAVE_ON.includes(name);
            let resets = RESET_ON[layer].includes(name);
            await expect(save, `Save & Close on ${name}`).toBeVisible({ visible: saves });
            await expect(reset, `Reset on ${name}`).toBeVisible({ visible: resets });
            await expect(llm.locator('.llma-settings-footer'), `footer on ${name}`).toBeVisible({ visible: saves || resets });
        }
    });
}

test('an unsaved change can still be saved from any tab', async ({ llm }) => {
    await openSettings(llm);
    await llm.locator('#llma-s-max-tokens').fill('1234');
    await tab(llm, 'memory').click();
    await expect(llm.locator('#llma-settings-save')).toBeVisible();
    await expect(llm.locator('#llma-settings-reset')).toBeHidden();
    await llm.locator('#llma-settings-save').click();
    await expect(llm.locator('#llma-settings-overlay')).toBeHidden();
    expect((await api(llm, 'LLMAssistantGetSettings')).settings.parameters.maxTokens).toBe(1234);
    // Drawn again, there is nothing to save there.
    await llm.locator('#llma-settings-btn').click();
    await expect(llm.locator('#llma-settings-memory')).toBeVisible();
    await expect(llm.locator('.llma-settings-footer')).toBeHidden();
    // An unsaved price does the same, until its own button saves it.
    await tab(llm, 'usage').click();
    await llm.locator('#llma-usage-currency').fill('GBP');
    await tab(llm, 'schedules').click();
    await expect(llm.locator('#llma-settings-save')).toBeVisible();
    await tab(llm, 'usage').click();
    await llm.locator('#llma-usage-save').click();
    await expect(llm.locator('#llma-usage-status')).toHaveText('Saved.');
    await expect(llm.locator('#llma-settings-save')).toBeHidden();
    // Put back for the specs that follow.
    await useDocumentCaps(llm, { maxTokens: 4096 });
    await api(llm, 'LLMAssistantSaveUsageSettings', { currency: 'USD', prices: {}, budget: { enabled: false, daily: 0, weekly: 0 } });
});

// ---- one model picker ----

test('a wide window picks the model in the top bar, and has no second picker by the message box', async ({ llm }) => {
    await expect(llm.locator('.llma-topbar .select2-container')).toBeVisible();
    await expect(llm.locator('#llma-model-chip')).toBeHidden();
});

test('a narrow window picks the model from the chip, whose list opens beside it', async ({ llm }) => {
    for (let size of [{ width: 390, height: 844 }, { width: 1000, height: 900 }]) {
        await llm.setViewportSize(size);
        let chip = llm.locator('#llma-model-chip');
        await expect(chip).toBeVisible();
        await expect(llm.locator('.llma-topbar .select2-container')).toHaveCount(0);
        let list = llm.locator('#llma-model-select-wrap');
        await expect(list).toBeHidden();
        await chip.click();
        await expect(list).toBeVisible();
        await expect(chip).toHaveAttribute('aria-expanded', 'true');
        await expect(list).toBeInViewport({ ratio: 1 });
        let at = (await chip.boundingBox())!;
        let box = (await list.boundingBox())!;
        // Just above the chip, over it.
        expect(at.y - (box.y + box.height)).toBeGreaterThanOrEqual(0);
        expect(at.y - (box.y + box.height)).toBeLessThan(12);
        expect(box.x).toBeLessThan(at.x + at.width);
        expect(box.x + box.width).toBeGreaterThan(at.x);
        await llm.keyboard.press('Escape');
        await expect(list).toBeHidden();
    }
    // With the panel beside it the message box is narrow and the chip's buttons take a second row: the list still opens whole.
    await llm.setViewportSize({ width: 1100, height: 768 });
    await llm.locator('#llma-asst-toggle').click();
    await expect(llm.locator('.llma-panel-name')).toBeVisible();
    await llm.locator('#llma-model-chip').click();
    await expect(llm.locator('#llma-model-select-wrap')).toBeInViewport({ ratio: 1 });
    await llm.keyboard.press('Escape');
    await llm.locator('#llma-asst-toggle').click();
    await llm.locator('#llma-model-chip').click();
    await llm.locator('#llma-model-select-wrap .select2-selection').click();
    await llm.locator('.select2-results__option', { hasText: 'mock-model-b' }).click();
    await expect(llm.locator('#llma-model-select-wrap')).toBeHidden();
    await expect(llm.locator('#llma-model-chip-text')).toHaveText('mock-model-b');
    // Wide again, the same selects are back in the top bar, holding the choice.
    await llm.setViewportSize({ width: 1440, height: 900 });
    await expect(llm.locator('.llma-topbar .select2-selection__rendered')).toContainText('mock-model-b');
    await llm.locator('#llma-model-select').selectOption('mock-model');
});

test('a placed model has one short device label', async ({ llm }) => {
    await llm.route('**/API/LLMAssistantGetModels', async route => {
        let response = await route.fetch();
        let body = await response.json();
        body.models[0].metadata = { ...body.models[0].metadata, device: 'cuda:0', placement: 'GPU cuda', placement_reason: 'it fits' };
        body.models[1].metadata = { ...body.models[1].metadata, device: 'cuda:0', placement: 'Split cuda:0 + cuda:1' };
        await route.fulfill({ response, json: body });
    });
    await reopen(llm);
    await expect(llm.locator('#llma-model-select option')).toHaveText(['mock-model · GPU 0', 'mock-model-b · GPU 0+1']);
    await expect(llm.locator('#llma-model-select option').first()).toHaveAttribute('title', 'GPU cuda: it fits');
    await llm.locator('#llma-compare-toggle').click();
    await expect(llm.locator('#llma-model-select-b option')).toHaveText(['mock-model · GPU 0', 'mock-model-b · GPU 0+1']);
    await llm.locator('#llma-compare-toggle').click();
    let labels = await llm.evaluate(() => ['cuda', 'cuda:1', 'cpu', 'cloud', 'Offload on cuda', 'cuda:0+1', 'vulkan', 'vulkan:0', 'metal', 'Split cuda:0 + vulkan:1'].map(d => (0, eval)('llmaDeviceLabel')(d)));
    expect(labels).toEqual(['GPU 0', 'GPU 1', 'CPU', 'cloud', 'GPU 0 offload', 'GPU 0+1', 'Vulkan GPU', 'Vulkan GPU 0', 'Metal GPU', 'GPU 0 + Vulkan GPU 1']);
});

test('in the assistant gallery on a narrow screen the picker is in the top bar, and its buttons say what they do', async ({ llm }) => {
    await llm.setViewportSize({ width: 390, height: 844 });
    await llm.evaluate(() => (0, eval)('llmaShowWelcome')());
    let chip = llm.locator('.llma-topbar #llma-model-chip');
    await expect(chip).toBeVisible();
    await chip.click();
    for (let [button, words] of [['#llma-compare-toggle', 'Compare two models'], ['#llma-model-refresh', 'Refresh model list'], ['#llma-model-unload', 'Unload model (free VRAM)']]) {
        await expect(llm.locator(button)).toBeInViewport({ ratio: 1 });
        await expect(llm.locator(`${button} .llma-model-btn-label`)).toHaveText(words);
        await expect(llm.locator(`${button} .llma-model-btn-label`)).toBeVisible();
    }
    await expect(llm.locator('#llma-model-select-wrap')).toBeInViewport({ ratio: 1 });
    await llm.locator('#llma-model-refresh').click();
    await expect(llm.locator('.llma-toast', { hasText: 'Model list refreshed' })).toBeVisible({ timeout: 30_000 });
    // A chat brings the message box back, and the chip stands by it again.
    await llm.evaluate(() => (0, eval)('llmaStartChatWithAssistant')('default', { persist: false }));
    await expect(llm.locator('.llma-input-actions #llma-model-chip')).toBeVisible();
    await expect(llm.locator('.llma-topbar #llma-model-chip')).toHaveCount(0);
    // And a wide window has the selects in the top bar, their buttons as icons, also with the gallery showing.
    await llm.evaluate(() => (0, eval)('llmaShowWelcome')());
    await llm.setViewportSize({ width: 1440, height: 900 });
    await expect(llm.locator('.llma-topbar .select2-container')).toBeVisible();
    await expect(llm.locator('#llma-model-chip')).toBeHidden();
    await expect(llm.locator('#llma-model-refresh .llma-model-btn-label')).toBeHidden();
});

test('being asked to pick a model flashes the picker that shows', async ({ llm }) => {
    await expect(llm.locator('.llma-topbar .select2-container')).toBeVisible();
    await llm.evaluate(() => (0, eval)('llmaFlashModelSelect')());
    await expect(llm.locator('.llma-topbar .select2-container').first()).toHaveClass(/llma-flash/);
    await expect(llm.locator('.llma-topbar .select2-selection').first()).toHaveCSS('animation-name', 'llma-flash-pulse');
    await expect(llm.locator('.llma-topbar .select2-selection').first()).toBeFocused();
    await expect(llm.locator('.llma-topbar .select2-container').first()).not.toHaveClass(/llma-flash/);
    // Where the bar is narrow the chip is what shows.
    await llm.setViewportSize({ width: 390, height: 844 });
    await expect(llm.locator('#llma-model-chip')).toBeVisible();
    await llm.evaluate(() => (0, eval)('llmaFlashModelSelect')());
    await expect(llm.locator('#llma-model-chip')).toHaveCSS('animation-name', 'llma-flash-pulse');
    await expect(llm.locator('#llma-model-chip')).toBeFocused();
});

// ---- compare ----

test('Stop in compare leaves a finished lane as drawn', async ({ llm }) => {
    let file = 'Here.\n\n```python\n' + Array.from({ length: 20 }, (_, i) => `print(${i})`).join('\n') + '\n```\n';
    let slow = Array.from({ length: 200 }, (_, i) => `word${i}`).join(' ');
    await setScenarios([{ match: 'one file', model: 'mock-model-b', rounds: [{ text: slow, chunkDelayMs: 200 }] }, { match: 'one file', rounds: [{ text: file }] }]);
    await llm.locator('#llma-compare-toggle').click();
    await send(llm, 'one file');
    let columns = llm.locator('.llma-compare-col');
    await expect(columns.first().locator('.llma-asset-card')).toHaveCount(1, { timeout: 20_000 });
    await expect(columns.nth(1)).toContainText('word1');
    await llm.locator('#llma-stop-btn').click();
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    // Checked straight away: the chat is read again a moment later, which would draw the card anew.
    expect(await columns.first().locator('.llma-asset-card').count()).toBe(1);
    await llm.locator('#llma-compare-toggle').click();
});

test('Stop in compare keeps why the other lane failed', async ({ llm }) => {
    await setScenarios([{ match: 'one fails', model: 'mock-model-b', rounds: [{ delayMs: 15000, text: 'late' }] }, { match: 'one fails', rounds: [{ error: { status: 500, message: 'LANE-A-FAILED' } }] }]);
    await llm.locator('#llma-compare-toggle').click();
    await send(llm, 'one fails');
    await expect(llm.locator('.llma-compare-col .llma-msg-error')).toContainText('LANE-A-FAILED', { timeout: 20_000 });
    await llm.locator('#llma-stop-btn').click();
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await reopen(llm);
    await expect(llm.locator('.llma-msg-tail')).toContainText('LANE-A-FAILED');
});

// ---- the compare switch ----

test('compare mode shows that it is on, also after a popover closes, and leaves room for the chat title', async ({ llm }) => {
    await setScenarios([{ regex: '.*', rounds: [{ text: 'a reply' }] }]);
    let toggle = llm.locator('#llma-compare-toggle');
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
    let off = await toggle.evaluate(el => getComputedStyle(el).backgroundColor);
    await send(llm, 'a chat with a title that is long enough to be cut off somewhere in the top bar');
    await expect(lastAssistant(llm)).toContainText('a reply');
    await llm.locator('#llma-asst-toggle').click();
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'true');
    await llm.locator('#llma-params-btn').click();
    await llm.locator('#llma-params-btn').click();
    await llm.mouse.move(0, 0);
    expect(await toggle.evaluate(el => getComputedStyle(el).backgroundColor)).not.toBe(off);
    for (let width of [1440, 1480, 1600]) {
        await llm.setViewportSize({ width: width, height: 900 });
        await expect.poll(async () => (await llm.locator('#llma-thread-title').boundingBox())!.width).toBeGreaterThanOrEqual(120);
        // The title has its room to itself: the model selects start after it ends.
        let title = (await llm.locator('#llma-thread-title').boundingBox())!;
        let selects = (await llm.locator('#llma-model-select-wrap').boundingBox())!;
        expect(title.x + title.width).toBeLessThanOrEqual(selects.x + 0.5);
        for (let box of await llm.locator('#llma-model-select-wrap > *:visible').evaluateAll(all => all.map(el => el.getBoundingClientRect().toJSON()))) {
            expect(box.width == 0 || box.x >= title.x + title.width - 0.5).toBe(true);
        }
        let bar = (await llm.locator('.llma-topbar').boundingBox())!;
        let last = (await llm.locator('#llma-asst-toggle').boundingBox())!;
        expect(last.x + last.width).toBeLessThanOrEqual(bar.x + bar.width);
    }
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
});

// ---- the assistant panel ----

test('the panel has no counters of its own, and its own close button only where it covers the top bar', async ({ llm }) => {
    await setScenarios([{ regex: '.*', rounds: [{ text: 'a reply' }] }]);
    await send(llm, 'count me');
    await expect(lastAssistant(llm)).toContainText('a reply');
    await llm.locator('#llma-asst-toggle').click();
    await expect(llm.locator('.llma-panel-name')).toBeVisible();
    await expect(llm.locator('.llma-panel-stats')).toHaveCount(0);
    await expect(llm.locator('#llma-panel-collapse')).toBeHidden();
    // The two controls that stay both close it.
    await llm.locator('#llma-asst-toggle').click();
    await expect(llm.locator('#llma-container')).toHaveClass(/panel-collapsed/);
    for (let size of [{ width: 900, height: 700 }, { width: 390, height: 844 }]) {
        await llm.setViewportSize(size);
        await llm.locator('#llma-asst-toggle').click();
        await expect(llm.locator('#llma-panel-collapse')).toBeVisible();
        await llm.locator('#llma-panel-collapse').click();
        await expect(llm.locator('#llma-asst-panel')).toBeHidden();
    }
});

// ---- replies, live and after a reload ----

test('a stored reply shows reasoning, tool rows and text in the order they came', async ({ llm }) => {
    await setScenarios([{ match: 'in order', rounds: ROUNDS }]);
    await send(llm, 'in order');
    await expect(lastAssistant(llm)).toContainText('The answer.');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    let live = await shape(llm);
    expect(live.map(part => part.split(':')[0])).toEqual(['think', 'tools', 'think', 'text', 'tools', 'think', 'text']);
    await reopen(llm);
    await expect(lastAssistant(llm)).toContainText('The answer.');
    expect(await shape(llm)).toEqual(live);
});

test('a reply whose order cannot be told (older, or edited) shows its calls first, then its text', async ({ llm }) => {
    await setScenarios([{ match: 'old one', rounds: ROUNDS }]);
    await send(llm, 'old one');
    await expect(lastAssistant(llm)).toContainText('The answer.');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    let threadId = (await api(llm, 'LLMAssistantGetThreads')).threads[0].id;
    let reply = (await api(llm, 'LLMAssistantGetThread', { threadId })).thread.messages[1];
    let known = await llm.evaluate(message => {
        let rounds = (0, eval)('llmaReplyRounds');
        // An older chat saved no rounds, and one saved before a call was recorded in them cannot place it.
        return [!!rounds(message), rounds({ ...message, rounds: undefined }), rounds({ ...message, rounds: [{ text: message.content, toolCallIds: [] }] })];
    }, reply);
    expect(known).toEqual([true, null, null]);
    expect((await api(llm, 'LLMAssistantEditMessage', { threadId, messageId: reply.id, content: 'Rewritten by hand.' })).success).toBe(true);
    await reopen(llm);
    await expect(lastAssistant(llm)).toContainText('Rewritten by hand.');
    await expect(lastAssistant(llm)).not.toContainText('The answer.');
    await expect(lastAssistant(llm).locator('.llma-tool-call-bubble')).toHaveCount(2);
});

test('a reasoning block that has ended is not "Thinking..." any more, while the reply goes on', async ({ llm }) => {
    await setScenarios([{ match: 'ponder', rounds: [{ text: '<think>weighing it</think>', toolCalls: [{ name: 'memory_read', arguments: {} }] }, { delayMs: 6000, text: 'Weighed.' }] }]);
    await send(llm, 'ponder');
    await expect(lastAssistant(llm).locator('.llma-tool-call-bubble.success')).toHaveCount(1);
    await expect(llm.locator('#llma-stop-btn')).toBeVisible();
    await expect(lastAssistant(llm).locator('.llma-think-label')).toHaveText('Reasoning');
    await expect(lastAssistant(llm).locator('.llma-think-live')).toHaveCount(0);
    await expect(lastAssistant(llm)).toContainText('Weighed.');
});

test('reasoning cut off by Stop is not left "Thinking..."', async ({ llm }) => {
    let long = Array.from({ length: 200 }, (_, i) => `thought${i}`).join(' ');
    await setScenarios([{ match: 'muse', rounds: [{ text: `<think>${long}</think>done`, chunkDelayMs: 100 }] }]);
    await send(llm, 'muse');
    await expect(lastAssistant(llm).locator('.llma-think-live')).toHaveCount(1);
    await llm.locator('#llma-stop-btn').click();
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await expect(llm.locator('.llma-think-live')).toHaveCount(0);
    await expect(lastAssistant(llm).locator('.llma-think-label')).toHaveText('Reasoning');
});

test('a call waiting for permission says so, and a refused one is not told as done', async ({ llm }) => {
    await setScenarios([{ match: 'paint', rounds: [{ toolCalls: [{ name: 'generate_image', arguments: { prompt: 'a fox' } }] }, { text: 'Fine, no picture.' }] }]);
    await setMode(llm, 'ask');
    await send(llm, 'paint');
    let summary = llm.locator('.llma-tool-group-summary');
    await expect(llm.locator('.llma-tool-approval')).toBeVisible();
    await expect(summary).toHaveText('Generate Image needs permission');
    await expect(llm.locator('.llma-tool-group-head .llma-spinner')).toBeHidden();
    // The reply is still running: nothing to do with it yet.
    let row = replyRows(llm).last();
    await row.hover();
    await expect(row.locator('.llma-msg-actions')).toBeHidden();
    // The question before it keeps its actions.
    await userRows(llm).last().hover();
    await expect(userRows(llm).last().locator('.llma-msg-actions')).toBeVisible();
    await llm.locator('.llma-tool-approval button[data-decision="deny"]').click();
    await expect(lastAssistant(llm)).toContainText('no picture');
    await expect(summary).toHaveText('Generate Image declined');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await replyRows(llm).last().hover();
    await expect(replyRows(llm).last().locator('.llma-msg-actions')).toBeVisible();
    await reopen(llm);
    await expect(summary).toHaveText('Generate Image declined');
    await setMode(llm, 'auto');
});

test('Stop settles a call that was waiting, as it reads after a reload', async ({ llm }) => {
    await setScenarios([{ match: 'stop me', rounds: [{ toolCalls: [{ name: 'file_write', arguments: { path: `ui-stop-${Date.now()}.txt`, content: 'tea', overwrite: true } }] }, { text: 'Never.' }] }]);
    await setMode(llm, 'ask');
    await send(llm, 'stop me');
    let summary = llm.locator('.llma-tool-group-summary');
    await expect(summary).toHaveText('File Write needs permission');
    await llm.locator('#llma-stop-btn').click();
    // At once, not only after the chat is read again.
    expect(await summary.textContent()).toBe('File Write failed');
    await expect(llm.locator('.llma-tool-call-bubble.pending')).toHaveCount(0);
    await expect(llm.locator('.llma-tool-approval')).toHaveCount(0);
    // Nor is the reply still shown as on its way.
    await expect(llm.locator('.llma-typing')).toHaveCount(0);
    await llm.waitForTimeout(2000);
    await reopen(llm);
    await expect(summary).toHaveText('File Write failed');
    await setMode(llm, 'auto');
});

test('Stop does not call a tool that was under way failed, nor leave it running', async ({ llm }) => {
    // No tool here runs long enough to be stopped on cue, so the row is drawn and settled as the Stop does it.
    let told = await llm.evaluate(() => {
        let bubble = document.createElement('div');
        let call = { id: 'under-way', name: 'generate_image', arguments: {}, result: null };
        (0, eval)('llmaRenderToolCall')(bubble, call);
        (0, eval)('llmaSettleToolCalls')(bubble, { toolCalls: [call] });
        let row = bubble.querySelector('.llma-tool-call-bubble')!;
        return {
            summary: bubble.querySelector('.llma-tool-group-summary')!.textContent, status: row.querySelector('.llma-tool-call-status')!.textContent, result: call.result,
            marks: ['pending', 'error', 'success'].filter(c => row.classList.contains(c)), spinners: row.querySelectorAll('.llma-spinner').length,
            running: !!bubble.querySelector('.llma-tool-group.running')
        };
    });
    expect(told).toEqual({ summary: 'Generate Image unfinished at Stop', status: 'stopped', result: null, marks: [], spinners: 0, running: false });
});

test('an allowed call is told as running, then done', async ({ llm }) => {
    await setScenarios([{ match: 'note it', rounds: [{ toolCalls: [{ name: 'file_write', arguments: { path: `ui-cleanup-${Date.now()}.txt`, content: 'tea', overwrite: true } }] }, { text: 'Noted.' }] }]);
    await setMode(llm, 'ask');
    await send(llm, 'note it');
    let summary = llm.locator('.llma-tool-group-summary');
    await expect(summary).toHaveText('File Write needs permission');
    await llm.locator('.llma-tool-approval button[data-decision="allow"]').click();
    await expect(lastAssistant(llm)).toContainText('Noted.');
    await expect(summary).toHaveText('Wrote a file');
    await setMode(llm, 'auto');
});

// ---- failures ----

test('after a reload a failed reply still says what happened, and the message count does not change', async ({ llm }) => {
    let refusal = 'Not enough free host RAM to safely load model.gguf: it needs 8,403 MB and 3,401 MB are free (cuda:0).';
    await setScenarios([{ match: 'too big', rounds: [{ error: { status: 500, message: refusal } }] }]);
    await send(llm, 'too big');
    let error = llm.locator('.llma-msg-error');
    await expect(error).toContainText('Not enough system memory (RAM)', { timeout: 20_000 });
    await expect(error).toContainText(refusal);
    await expect(error).not.toContainText('VRAM');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await expect(llm.locator('#llma-ctx-label')).toHaveText('1 message');
    // Live, the failed reply says it: the row under it does not say it twice.
    await expect(llm.locator('.llma-msg-retry')).toBeVisible();
    await expect(error).toHaveCount(1);
    await expect(llm.locator('.llma-msg-tail-note')).toHaveCount(0);
    await reopen(llm);
    await expect(llm.locator('#llma-ctx-label')).toHaveText('1 message');
    await expect(llm.locator('.llma-msg-tail .llma-msg-error')).toContainText(refusal);
    await expect(llm.locator('.llma-msg-retry')).toBeVisible();
    // Another browser has not seen it fail: it still says there is no reply.
    await llm.evaluate(() => localStorage.removeItem('llma.replyFailures'));
    await reopen(llm);
    await expect(llm.locator('.llma-msg-tail-note')).toHaveText('This message has no reply.');
    // Once a reply arrives, nothing is left to explain.
    await setScenarios([{ match: 'too big', rounds: [{ text: 'It fits now.' }] }]);
    await llm.locator('.llma-msg-retry').click();
    await expect(lastAssistant(llm)).toContainText('It fits now.');
    await expect(llm.locator('.llma-msg-tail')).toHaveCount(0);
    await expect(llm.locator('#llma-ctx-label')).toHaveText('2 messages');
});

test('a failure below what is showing is followed into view, with the backend\'s words and Retry', async ({ llm }) => {
    await llm.setViewportSize({ width: 390, height: 844 });
    let refusal = 'Not enough free host RAM to safely load model.gguf: it needs about 8,403 MB and 3,401 MB are free. Close other programs or pick a smaller model.';
    await setScenarios([{ match: 'no room', rounds: [{ error: { status: 500, message: refusal } }] }]);
    for (let turn of [1, 2]) {
        await send(llm, `no room ${turn}`);
        await expect(userRows(llm)).toHaveCount(turn);
        await expect(llm.locator('#llma-send-btn')).toBeVisible({ timeout: 20_000 });
        await expect(llm.locator('.llma-msg-retry')).toBeInViewport({ ratio: 1 });
        await expect(llm.locator('.llma-msg-error-detail').last()).toBeInViewport({ ratio: 1 });
    }
});

test('only real memory failures are called that, and system memory is told from graphics memory', async ({ llm }) => {
    let titles = await llm.evaluate(errors => errors.map(e => (0, eval)('llmaHumanizeError')(e).title), [
        'Not enough free host RAM to safely load model.gguf: it needs 4012 MB.',
        'System.OutOfMemoryException: Exception of type was thrown.',
        'std::bad_alloc',
        'CUDA error: out of memory (cuda:0)',
        'ggml: failed to allocate CUDA0 buffer of 4294 MiB',
        'No placement fits: 5.1 GB free, needs 8.0',
        'llama.cpp ran out of memory',
        'Engine OOM while decoding',
        'CUDA error: invalid device ordinal (cuda:3)',
        'There is no room left in the queue',
        'Could not allocate a port for the backend',
        'The cuda:0 worker stopped',
        // The local engine's own words.
        'Out of VRAM: the driver refused a 2048 MB allocation; 429 MB of 24564 MB was free.',
        'Vulkan error -2 (ErrorOutOfDeviceMemory): vkAllocateMemory(size=4294967296, typeIdx=1)',
        'Vulkan error -1 (ErrorOutOfHostMemory): vkCreateBuffer'
    ]);
    let ram = 'Not enough system memory (RAM)';
    let vram = 'The GPU ran out of memory (VRAM)';
    expect(titles.slice(0, 8)).toEqual([ram, ram, ram, vram, vram, vram, 'The model ran out of memory', 'The model ran out of memory']);
    // The rest keep their own words.
    expect(titles.slice(8, 12)).toEqual(['CUDA error: invalid device ordinal (cuda:3)', 'There is no room left in the queue', 'Could not allocate a port for the backend', 'The cuda:0 worker stopped']);
    expect(titles.slice(12)).toEqual([vram, vram, ram]);
});

// ---- form controls ----

for (let theme of ['modern_dark', 'modern_light']) {
    test(`form controls take the theme's colours in ${theme}`, async ({ llm }) => {
        expect((await api(llm, 'ChangeUserSettings', { settings: { theme } })).success).toBe(true);
        try {
            await reopen(llm);
            await expect(llm.locator(`link.theme_sheet_header[href*="${theme}.css"]`)).toBeAttached({ timeout: 30_000 });
            let wrong = () => llm.evaluate(() => {
                let probe = document.createElement('div');
                probe.style.cssText = 'background-color: var(--background-soft); color: var(--text)';
                document.getElementById('llma-container')!.appendChild(probe);
                let want = getComputedStyle(probe);
                let theirs = [want.backgroundColor, want.color];
                let found = Array.from(document.querySelectorAll<HTMLElement>('#llma-container .form-control, #llma-container .form-select'))
                    .filter(el => el.checkVisibility() && !(el as HTMLInputElement).disabled)
                    .filter(el => getComputedStyle(el).backgroundColor != theirs[0] || getComputedStyle(el).color != theirs[1])
                    .map(el => `${el.id || el.className} ${getComputedStyle(el).backgroundColor}`);
                probe.remove();
                return found;
            });
            await llm.locator('#llma-enhance-btn').click();
            await expect(llm.locator('#llma-enhance-all')).toBeVisible();
            await expect.poll(wrong).toEqual([]);
            await llm.locator('#llma-enhance-btn').click();
            await openSettings(llm, 'memory');
            await expect(llm.locator('#llma-memx-model')).toBeVisible();
            await expect.poll(wrong).toEqual([]);
            await llm.locator('#llma-memx-model').focus();
            await expect.poll(wrong).toEqual([]);
            await tab(llm, 'knowledge').click();
            await expect(llm.locator('#llma-know-query')).toBeVisible();
            await expect.poll(wrong).toEqual([]);
        }
        finally {
            await api(llm, 'ChangeUserSettings', { settings: { theme: 'modern_dark' } });
        }
    });
}

// ---- the first-run guide ----

test('the guide\'s buttons sit under what scrolls, not over it', async ({ llm }) => {
    await llm.setViewportSize({ width: 1366, height: 768 });
    expect((await api(llm, 'LLMAssistantSetOnboarding', { action: 'run' })).success).toBe(true);
    // The guide shows over an empty chat: this user has none after the fixture.
    await reopen(llm);
    try {
        await expect(llm.locator('#llma-onboarding li[data-item]').first()).toBeAttached();
        let body = llm.locator('.llma-onboarding-body');
        expect(await body.evaluate(el => el.scrollHeight > el.clientHeight)).toBe(true);
        let scroll = (await body.boundingBox())!;
        let actions = (await llm.locator('.llma-onboarding-actions').boundingBox())!;
        expect(actions.y).toBeGreaterThanOrEqual(scroll.y + scroll.height - 0.5);
        await expect(llm.locator('#llma-onboarding-skip')).toBeInViewport({ ratio: 1 });
        await expect(llm.locator('.llma-input-wrap')).toBeInViewport({ ratio: 1 });
    }
    finally {
        await api(llm, 'LLMAssistantSetOnboarding', { action: 'skip' });
    }
});

// ---- the phone's message box ----

test.describe('on a touch phone', () => {
    test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

    /** How many rows the message box's buttons take. */
    let buttonRows = (page: Page) => page.locator('.llma-input-actions').evaluate(actions => {
        let middles = Array.from(actions.querySelectorAll<HTMLElement>('button, label.btn, .llma-attach-label'))
            .filter(el => el.checkVisibility({ visibilityProperty: true }))
            .map(el => el.getBoundingClientRect()).filter(box => box.width > 1)
            .map(box => Math.round((box.top + box.bottom) / 2));
        return middles.sort((a, b) => a - b).filter((y, i, all) => i == 0 || y - all[i - 1] > 10).length;
    });

    test('the message box is the text and two rows of buttons, with no keyboard hint', async ({ llm }) => {
        await expect(llm.locator('#llma-input')).toBeVisible();
        await expect(llm.locator('#llma-input-hint')).toBeHidden();
        expect(await buttonRows(llm)).toBe(2);
        expect((await llm.locator('.llma-input-wrap').boundingBox())!.height).toBeLessThan(165);
        await expect(llm.locator('#llma-send-btn')).toBeInViewport({ ratio: 1 });
        await expect(llm.locator('#llma-model-chip')).toBeInViewport({ ratio: 1 });
        // Compare mode names two models on the chip, which cuts them short rather than push Send onto a third row.
        await llm.evaluate(() => (0, eval)('llmaToggleCompareMode')(true));
        await llm.evaluate(() => {
            let second = document.getElementById('llma-model-select-b') as HTMLSelectElement;
            second.value = 'mock-model-b';
            second.dispatchEvent(new Event('change'));
        });
        await expect(llm.locator('#llma-model-chip-text')).toContainText(' vs ');
        for (let width of [390, 360]) {
            await llm.setViewportSize({ width: width, height: 844 });
            expect(await buttonRows(llm)).toBe(2);
            await expect(llm.locator('#llma-send-btn')).toBeInViewport({ ratio: 1 });
            await expect(llm.locator('#llma-model-chip')).toBeInViewport({ ratio: 1 });
        }
        await llm.evaluate(() => (0, eval)('llmaToggleCompareMode')(false));
    });
});

// ---- image parameters ----

test('an image call looks its parameters up without errors from the providers of special names', async ({ llm }) => {
    let log = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '.run', 'swarm.log');
    test.skip(!fs.existsSync(log), 'The server log is only there for the harness\'s own SwarmUI.');
    let traces = () => (fs.readFileSync(log, 'utf8').match(/Error generating dynamic Comfy param/g) ?? []).length;
    let before = traces();
    let reply = await api(llm, 'LLMAssistantExecuteTool', { toolId: 'generate_image', arguments: JSON.stringify({ prompt: 'a fox', images: 1, params: { steps: 20, width: 768, height: 768 } }) });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    await llm.waitForTimeout(1000);
    expect(traces()).toBe(before);
});

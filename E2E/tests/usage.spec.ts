import { test, expect, send, lastAssistant, setScenarios, api, reopen, mockRequests, enableSessionTools, resetSessionTools } from './fixtures';
import type { Page } from '@playwright/test';

// Tokens and cost: the figure under a reply and the chat's in the context bar, the user's price table, estimates, totals and the soft budget.
// Nothing is priced out of the box, so every cost here comes from a price the spec typed in. Mock replies report 1,000 prompt and 200 completion tokens.

/** One scripted exchange whose reply reports 1,000 + 200 tokens, or nothing with `noUsage`. */
function exchange(match: string, reply: string, noUsage = false) {
    return { match, rounds: [{ text: reply, usage: { prompt_tokens: 1000, completion_tokens: 200 }, noUsage }] };
}

/** Sends one message and waits for its reply to finish. */
async function exchangeWith(page: Page, text: string, replyContains: string) {
    await send(page, text);
    await expect(lastAssistant(page)).toContainText(replyContains);
    await expect(page.locator('#llma-send-btn')).toBeVisible();
}

/** Opens Settings on the Usage tab, with its form drawn. */
async function openUsageTab(page: Page) {
    await page.locator('#llma-settings-btn').click();
    await page.locator('.llma-modal-tab[data-tab="usage"]').click();
    await expect(page.locator('#llma-usage-save')).toBeVisible();
}

/** Fills the price table's only row (adding it when there is none) and saves with the tab's own button. */
async function setPrice(page: Page, model: string, input: string, output: string) {
    await openUsageTab(page);
    let rows = page.locator('#llma-usage-rows .llma-usage-row');
    if (await rows.count() === 0) {
        await page.locator('#llma-usage-add').click();
    }
    await rows.first().locator('[data-field="model"]').fill(model);
    await rows.first().locator('[data-field="input"]').fill(input);
    await rows.first().locator('[data-field="output"]').fill(output);
    await page.locator('#llma-usage-save').click();
    await expect(page.locator('#llma-usage-status')).toHaveText('Saved.');
    await page.locator('#llma-settings-close').click();
    await expect(page.locator('#llma-settings-overlay')).toBeHidden();
}

const chip = (page: Page) => page.locator('.llma-usage-chip').last();
const chatTotal = (page: Page) => page.locator('#llma-usage-thread');

test.afterEach(async ({ llm }) => {
    let cleared = await api(llm, 'LLMAssistantSaveUsageSettings', { currency: 'USD', prices: {}, budget: { enabled: false, daily: 0, weekly: 0 } });
    expect(cleared.success, JSON.stringify(cleared)).toBe(true);
    // Save & Close writes the shared layer for an admin, and some specs here switch a setting off: both layers are put back.
    await api(llm, 'LLMAssistantResetSettings', { scope: 'shared' });
    await api(llm, 'LLMAssistantResetSettings');
});

test('a model with no price shows tokens only', async ({ llm }) => {
    await setScenarios([exchange('hello', 'Hi there.')]);
    await exchangeWith(llm, 'hello', 'Hi there.');
    await expect(chip(llm)).toHaveText('1,200 tok');
    await expect(chatTotal(llm)).toHaveText('Chat: 1,200 tok');
    await chip(llm).click();
    let popover = llm.locator('#popover_llma_usage');
    await expect(popover).toContainText('No price set for this model');
    await expect(popover).toContainText('Counted by the backend.');
    await expect(popover).not.toContainText('USD');
});

test('a price shows the cost, and editing it changes the displayed cost', async ({ llm }) => {
    await setScenarios([exchange('hello', 'Hi there.')]);
    await exchangeWith(llm, 'hello', 'Hi there.');
    // (1,000 x 2 + 200 x 10) / 1,000,000
    await setPrice(llm, 'mock-model', '2', '10');
    await expect(chip(llm)).toHaveText('1,200 tok · 0.0040 USD');
    await expect(chatTotal(llm)).toHaveText('Chat: 1,200 tok · 0.0040 USD');
    await setPrice(llm, 'mock-model', '4', '20');
    await expect(chip(llm)).toHaveText('1,200 tok · 0.0080 USD');
    await expect(chatTotal(llm)).toHaveText('Chat: 1,200 tok · 0.0080 USD');
    // A reload draws the same from the saved chat.
    await reopen(llm);
    await llm.locator('.llma-thread-item').first().click();
    await expect(chip(llm)).toHaveText('1,200 tok · 0.0080 USD');
});

test('"Edit prices" on a reply opens the table with a row for its model', async ({ llm }) => {
    await setScenarios([exchange('hello', 'Hi there.')]);
    await exchangeWith(llm, 'hello', 'Hi there.');
    await chip(llm).click();
    await llm.locator('#popover_llma_usage .llma-usage-edit').click();
    let row = llm.locator('#llma-usage-rows .llma-usage-row');
    await expect(row.locator('[data-field="model"]')).toHaveValue('mock-model');
    await row.locator('[data-field="input"]').fill('2');
    await row.locator('[data-field="output"]').fill('10');
    // Save & Close saves the tab too.
    await llm.locator('#llma-settings-save').click();
    await expect(llm.locator('#llma-settings-overlay')).toBeHidden();
    await expect(chip(llm)).toHaveText('1,200 tok · 0.0040 USD');
});

test('a half-filled price is refused and the dialog stays open', async ({ llm }) => {
    await openUsageTab(llm);
    await llm.locator('#llma-usage-add').click();
    await llm.locator('#llma-usage-rows [data-field="model"]').fill('mock-model');
    await llm.locator('#llma-usage-rows [data-field="input"]').fill('2');
    await llm.locator('#llma-settings-save').click();
    await expect(llm.locator('#llma-usage-status')).toContainText('needs input and output');
    await expect(llm.locator('#llma-settings-overlay')).toBeVisible();
    expect((await api(llm, 'LLMAssistantGetUsageSettings')).usage.prices).toEqual({});
});

test('closing with unsaved prices asks first, and discarded edits are gone for good', async ({ llm }) => {
    await openUsageTab(llm);
    await llm.locator('#llma-usage-add').click();
    let row = llm.locator('#llma-usage-rows .llma-usage-row');
    await row.locator('[data-field="model"]').fill('mock-model');
    await row.locator('[data-field="input"]').fill('2');
    await row.locator('[data-field="output"]').fill('10');
    // Keeping on editing keeps the dialog and what was typed.
    llm.once('dialog', dialog => dialog.dismiss());
    await llm.locator('#llma-settings-close').click();
    await expect(llm.locator('#llma-settings-overlay')).toBeVisible();
    await expect(row.locator('[data-field="input"]')).toHaveValue('2');
    llm.once('dialog', dialog => dialog.accept());
    await llm.locator('#llma-settings-close').click();
    await expect(llm.locator('#llma-settings-overlay')).toBeHidden();
    // The dialog reopens on the same tab, drawn from what is saved.
    await llm.locator('#llma-settings-btn').click();
    await expect(llm.locator('#llma-usage-save')).toBeVisible();
    await expect(row).toHaveCount(0);
    await llm.locator('#llma-usage-currency').fill('GBP');
    await llm.locator('#llma-settings-save').click();
    await expect(llm.locator('#llma-settings-overlay')).toBeHidden();
    expect((await api(llm, 'LLMAssistantGetUsageSettings')).usage).toMatchObject({ currency: 'GBP', prices: {} });
});

test('a number box holding something unreadable is refused, not saved as nothing', async ({ llm }) => {
    await api(llm, 'LLMAssistantSaveUsageSettings', { prices: { 'mock-model': { input: 2, output: 10, cached: 1 } }, budget: { enabled: true, daily: 5, weekly: 0 } });
    await openUsageTab(llm);
    for (let box of [llm.locator('#llma-usage-budget-daily'), llm.locator('#llma-usage-rows [data-field="cached"]')]) {
        let before = await box.inputValue();
        await box.fill('');
        await box.pressSequentially('1e');
        await llm.locator('#llma-usage-save').click();
        await expect(llm.locator('#llma-usage-status')).toContainText('This is not a number');
        let saved = (await api(llm, 'LLMAssistantGetUsageSettings')).usage;
        expect(saved.budget.daily).toBe(5);
        expect(saved.prices['mock-model'].cached).toBe(1);
        await box.fill(before);
    }
    await llm.locator('#llma-usage-save').click();
    await expect(llm.locator('#llma-usage-status')).toHaveText('Saved.');
});

test('a chat sent without naming a model is saved with the one chosen, so it can be priced', async ({ llm }) => {
    await setScenarios([exchange('unnamed', 'Hi there.')]);
    let threadId = (await api(llm, 'LLMAssistantCreateThread')).thread.id;
    await llm.evaluate(id => new Promise<void>((resolve, reject) => {
        (window as any).makeWSRequest('LLMAssistantSendMessageWS', { threadId: id, message: 'unnamed' }, (data: any) => {
            if (data.done) {
                resolve();
            }
            else if (data.error) {
                reject(new Error(data.error));
            }
        });
    }), threadId);
    // The backend's default model answered, and the saved reply says so.
    await api(llm, 'LLMAssistantSaveUsageSettings', { prices: { 'mock-model': { input: 2, output: 10 } } });
    let usage = await api(llm, 'LLMAssistantGetThreadUsage', { threadId });
    expect(usage.replies[0]).toMatchObject({ model: 'mock-model', cost: 0.004 });
});

test('an ordinary settings save leaves the price table alone', async ({ llm }) => {
    await api(llm, 'LLMAssistantSaveUsageSettings', { prices: { 'mock-model': { input: 2, output: 10 } } });
    await api(llm, 'LLMAssistantSaveSettings', { settings: { usageCost: { prices: {} } } });
    expect((await api(llm, 'LLMAssistantGetUsageSettings')).usage.prices['mock-model']).toMatchObject({ input: 2, output: 10 });
});

test('a reply the backend reported nothing for is labelled estimated', async ({ llm }) => {
    await api(llm, 'LLMAssistantSaveUsageSettings', { prices: { 'mock-model': { input: 2, output: 10 } } });
    await setScenarios([exchange('hello', 'Hi there.', true)]);
    await exchangeWith(llm, 'hello', 'Hi there.');
    await expect(chip(llm)).toHaveText(/^~[\d,]+ tok · ~<?[\d.]+ USD · estimated$/);
    await expect(chatTotal(llm)).toHaveText(/^Chat: ~[\d,]+ tok · ~/);
    await chip(llm).click();
    await expect(llm.locator('#popover_llma_usage')).toContainText('Estimated from the length of the saved text');
});

test('totals add up across chats for the session, the day and the week', async ({ llm }) => {
    await setScenarios([exchange('hello', 'Hi there.'), exchange('again', 'Hello again.')]);
    await api(llm, 'LLMAssistantSaveUsageSettings', { currency: 'EUR', prices: { 'mock-model': { input: 2, output: 10 } } });
    await exchangeWith(llm, 'hello', 'Hi there.');
    await llm.locator('#llma-new-thread-btn').click();
    await exchangeWith(llm, 'again', 'Hello again.');
    await expect(chatTotal(llm)).toHaveText('Chat: 1,200 tok · 0.0040 EUR');
    await chatTotal(llm).click();
    let popover = llm.locator('#popover_llma_usage');
    await expect(popover.locator('tr[data-period="day"]')).toContainText('2,400 tok');
    await expect(popover.locator('tr[data-period="day"]')).toContainText('0.0080 EUR');
    await expect(popover.locator('tr[data-period="week"]')).toContainText('2,400 tok');
    await expect(popover.locator('tr[data-period="session"]')).toContainText('2,400 tok');
    let totals = await api(llm, 'LLMAssistantGetUsageTotals', { timeZone: 'Pacific/Auckland', since: new Date(Date.now() + 60_000).toISOString() });
    expect(totals.periods.day).toMatchObject({ replies: 2, totalTokens: 2400, estimatedReplies: 0, unpricedReplies: 0 });
    expect(totals.periods.session.replies).toBe(0);
    expect(totals.periods.session.cost).toBeNull();
    // "This session" is also how long ago it began, which the browser's clock cannot skew.
    let recent = await api(llm, 'LLMAssistantGetUsageTotals', { sessionSeconds: 3600 });
    expect(recent.periods.session.replies).toBe(2);
    let refused = await api(llm, 'LLMAssistantGetUsageTotals', { timeZone: 'Mars/Olympus' });
    expect(refused.success).toBeFalsy();
});

test('a stopped reply shows an estimated figure without a reload', async ({ llm }) => {
    await setScenarios([{ match: 'slow', rounds: [{ text: 'word '.repeat(200), chunkDelayMs: 150 }] }]);
    await send(llm, 'slow');
    await expect(lastAssistant(llm)).toContainText('word');
    await llm.locator('#llma-stop-btn').click();
    await expect(chip(llm)).toHaveText(/^~[\d,]+ tok · estimated$/);
});

test('a turn stopped during a later round is marked incomplete in the reply, the chat and the totals', async ({ llm }) => {
    await setScenarios([{
        match: 'two rounds', rounds: [
            { toolCalls: [{ name: 'memory_read', arguments: {} }], usage: { prompt_tokens: 1000, completion_tokens: 200 } },
            { text: 'never seen', delayMs: 15_000 }
        ]
    }]);
    await send(llm, 'two rounds');
    await expect.poll(async () => (await mockRequests()).filter(r => (r.messages ?? []).some((m: any) => m.role === 'tool')).length).toBeGreaterThan(0);
    await llm.locator('#llma-stop-btn').click();
    await expect(chip(llm)).toHaveText('1,200 tok · incomplete');
    await expect(chatTotal(llm)).toHaveText('Chat: 1,200 tok · incomplete');
    await chatTotal(llm).click();
    let popover = llm.locator('#popover_llma_usage');
    await expect(popover.locator('tr[data-period="day"]')).toContainText('(incomplete)');
    await expect(popover).toContainText('the real figures are higher');
    await expect(popover).toContainText('drop out of these totals and the budget');
});

test('headless tool turns and sub-sessions are in the totals', async ({ llm }) => {
    await enableSessionTools(llm);
    try {
        await setScenarios([
            {
                match: 'delegate', rounds: [
                    { toolCalls: [{ name: 'session_start', arguments: { title: 'Child', prompt: 'child-task', model: 'mock-model' } }], usage: { prompt_tokens: 1000, completion_tokens: 200 } },
                    { text: 'Started.', usage: { prompt_tokens: 1000, completion_tokens: 200 } }
                ]
            },
            exchange('child-task', 'Child done.')
        ]);
        let run = await api(llm, 'LLMAssistantRunTurn', { message: 'delegate', model: 'mock-model', approvalMode: 'auto' });
        expect(run.success, JSON.stringify(run)).toBe(true);
        // Two rounds of the headless turn and one reply in the sub-session.
        await expect.poll(async () => (await api(llm, 'LLMAssistantGetUsageTotals')).periods.day.totalTokens, { timeout: 30_000 }).toBe(3600);
        let usage = await api(llm, 'LLMAssistantGetThreadUsage', { threadId: run.threadId });
        expect(usage.replies).toHaveLength(1);
        expect(usage.replies[0]).toMatchObject({ promptTokens: 2000, completionTokens: 400, estimated: false, partial: false });
    }
    finally {
        await resetSessionTools(llm);
    }
});

test('a reached budget warns and blocks nothing', async ({ llm }) => {
    await setScenarios([exchange('hello', 'Hi there.'), exchange('again', 'Hello again.')]);
    await api(llm, 'LLMAssistantSaveUsageSettings', { prices: { 'mock-model': { input: 2, output: 10 } } });
    await exchangeWith(llm, 'hello', 'Hi there.');
    // Off by default: no warning however much was spent.
    await expect(chatTotal(llm)).toContainText('0.0040 USD');
    await expect(llm.locator('#llma-usage-warning')).toBeHidden();
    await openUsageTab(llm);
    await llm.locator('#llma-usage-budget-enabled').check();
    await llm.locator('#llma-usage-budget-daily').fill('0.001');
    await llm.locator('#llma-usage-save').click();
    await expect(llm.locator('#llma-usage-status')).toHaveText('Saved.');
    await llm.locator('#llma-settings-close').click();
    await expect(llm.locator('#llma-usage-warning')).toHaveText('Over daily budget');
    await expect(llm.locator('.llma-toast').filter({ hasText: 'nothing is blocked' })).toBeVisible();
    await exchangeWith(llm, 'again', 'Hello again.');
    // Both limits are said in one message, and the badge stays on a new chat, where it opens the totals.
    await openUsageTab(llm);
    await llm.locator('#llma-usage-budget-weekly').fill('0.002');
    await llm.locator('#llma-usage-save').click();
    await expect(llm.locator('#llma-usage-status')).toHaveText('Saved.');
    await llm.locator('#llma-settings-close').click();
    let toast = llm.locator('.llma-toast').filter({ hasText: 'nothing is blocked' });
    await expect(toast).toContainText('Today:');
    await expect(toast).toContainText('This week:');
    await llm.locator('#llma-new-thread-btn').click();
    await expect(llm.locator('#llma-usage-warning')).toHaveText('Over budget');
    await expect(chatTotal(llm)).toBeHidden();
    await llm.locator('#llma-usage-warning').click();
    await expect(llm.locator('#popover_llma_usage tr[data-period="week"]')).toBeVisible();
    await expect(llm.locator('#popover_llma_usage')).not.toContainText('This chat');
});

test('a time zone the server does not know still gets the warning and the totals, counted in UTC', async ({ llm }) => {
    await llm.evaluate(() => {
        (0, eval)('llmaUsage').timeZone = () => 'Mars/Olympus';
    });
    await api(llm, 'LLMAssistantSaveUsageSettings', { prices: { 'mock-model': { input: 2, output: 10 } }, budget: { enabled: true, daily: 0.001 } });
    await setScenarios([exchange('hello', 'Hi there.')]);
    await exchangeWith(llm, 'hello', 'Hi there.');
    await expect(llm.locator('#llma-usage-warning')).toHaveText('Over daily budget');
    await llm.locator('#llma-usage-warning').click();
    let popover = llm.locator('#popover_llma_usage');
    await expect(popover.locator('tr[data-period="day"]')).toContainText('1,200 tok');
    await expect(popover).toContainText('counted in UTC');
});

test('each compare lane shows its own cost, and only the priced model has one', async ({ llm }) => {
    await api(llm, 'LLMAssistantSaveUsageSettings', { prices: { 'mock-model': { input: 2, output: 10 } } });
    await setScenarios([
        { match: 'versus', model: 'mock-model', rounds: [{ text: 'lane A says hi', usage: { prompt_tokens: 1000, completion_tokens: 200 } }] },
        { match: 'versus', model: 'mock-model-b', rounds: [{ text: 'lane B says hi', usage: { prompt_tokens: 500, completion_tokens: 100 } }] }
    ]);
    await llm.locator('#llma-compare-toggle').click();
    await llm.locator('#llma-model-select-b').selectOption('mock-model-b');
    await send(llm, 'versus');
    let lanes = llm.locator('.llma-compare-col');
    await expect(lanes.filter({ hasText: 'lane A' }).locator('.llma-usage-chip')).toHaveText('1,200 tok · 0.0040 USD');
    await expect(lanes.filter({ hasText: 'lane B' }).locator('.llma-usage-chip')).toHaveText('600 tok');
    await expect(chatTotal(llm)).toHaveText('Chat: 1,800 tok · 0.0040 USD (priced replies only)');
});

test('turning the token count off hides the figures', async ({ llm }) => {
    await setScenarios([exchange('hello', 'Hi there.')]);
    await exchangeWith(llm, 'hello', 'Hi there.');
    await expect(chip(llm)).toBeVisible();
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('#llma-s-show-tokens').uncheck();
    await llm.locator('#llma-settings-save').click();
    await expect(llm.locator('.llma-usage-chip')).toHaveCount(0);
    await expect(chatTotal(llm)).toBeHidden();
});

test('the popover and the price table fit a phone', async ({ llm }) => {
    await api(llm, 'LLMAssistantSaveUsageSettings', { prices: { 'mock-model': { input: 2, output: 10 } } });
    await setScenarios([exchange('hello', 'Hi there.')]);
    await exchangeWith(llm, 'hello', 'Hi there.');
    await llm.setViewportSize({ width: 390, height: 844 });
    await chatTotal(llm).click();
    let popover = llm.locator('#popover_llma_usage');
    await expect(popover.locator('tr[data-period="week"]')).toBeVisible();
    let box = (await popover.boundingBox())!;
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(390);
    await popover.locator('.llma-usage-edit').click();
    let remove = llm.locator('#llma-usage-rows .llma-usage-remove');
    await expect(remove).toBeVisible();
    let removeBox = (await remove.boundingBox())!;
    expect(removeBox.x + removeBox.width).toBeLessThanOrEqual(390);
});

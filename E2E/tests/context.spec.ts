import { test, expect, send, lastAssistant, setScenarios, mockRequests, api } from './fixtures';
import type { Page } from '@playwright/test';

// How full a chat is (token usage and the model's window), and shortening a long chat with a summary. The mock states a 4000-token window for
// "mock-model" and none for "mock-model-b", and every reply can report its own usage.

const HEADING = '[Summary of the earlier conversation, which was shortened to save space]';

/** One scripted exchange whose reply reports `prompt` + `completion` tokens, so the chat looks that big to the client and the server. */
function exchange(match: string, reply: string, prompt: number, completion = 100) {
    return { match, rounds: [{ text: reply, usage: { prompt_tokens: prompt, completion_tokens: completion } }] };
}

/** The scripted answer to the summarizer's request (its prompt opens "Conversation to summarize:" or "Summary so far:"). */
function summarizer(text: string) {
    return { regex: 'Conversation to summarize:|Summary so far:', rounds: [{ text }] };
}

/** Sets the Context settings through the dialog. Defaults are restored by the `afterEach` below. */
async function setContext(page: Page, options: { window?: number; auto?: boolean; threshold?: number }) {
    await page.locator('#llma-settings-btn').click();
    await expect(page.locator('#llma-settings-overlay')).toBeVisible();
    if (options.window !== undefined) {
        await page.locator('#llma-s-context-window').fill(String(options.window));
    }
    if (options.auto !== undefined) {
        await page.locator('#llma-s-auto-compact').setChecked(options.auto);
    }
    if (options.threshold !== undefined) {
        await page.locator('#llma-s-compact-threshold').fill(String(options.threshold));
    }
    await page.locator('#llma-settings-save').click();
    await expect(page.locator('#llma-settings-overlay')).toBeHidden();
}

/** The bodies of the chat requests whose newest user message holds `text`. */
async function requestsFor(text: string): Promise<any[]> {
    return (await mockRequests()).filter(r => {
        let lastUser = [...(r.messages ?? [])].reverse().find((m: any) => m.role === 'user');
        return String(lastUser?.content ?? '').includes(text);
    });
}

/** Messages of a request that are not the system prompt. */
const conversation = (request: any): any[] => request.messages.filter((m: any) => m.role !== 'system');

/** Sends one message and waits for its reply to finish. */
async function exchangeWith(page: Page, text: string, replyContains: string) {
    await send(page, text);
    await expect(lastAssistant(page)).toContainText(replyContains);
    await expect(page.locator('#llma-send-btn')).toBeVisible();
}

test.afterEach(async ({ llm }) => {
    // Choosing a model saves the page's own copy of the settings, so that goes first; the resets then have the last word.
    // The chosen model is remembered, and two specs pick the one with no stated window.
    await llm.locator('#llma-model-select').selectOption('mock-model');
    await llm.waitForTimeout(500);
    // Saving from the dialog writes the shared layer for an admin, so both layers are put back.
    await api(llm, 'LLMAssistantResetSettings', { scope: 'shared' });
    await api(llm, 'LLMAssistantResetSettings');
});

test('the bar shows the tokens the model reported against its window', async ({ llm }) => {
    await setScenarios([exchange('hello', 'Hi there.', 1000, 200)]);
    await exchangeWith(llm, 'hello', 'Hi there.');
    await expect(llm.locator('#llma-ctx-tokens')).toHaveText('1,200 / 4,000 tokens');
    let fill = await llm.locator('#llma-ctx-fill').evaluate(el => (el as HTMLElement).style.width);
    expect(fill).toBe('30%');
});

test('the request asks the endpoint for token counts', async ({ llm }) => {
    await setScenarios([exchange('hello', 'Hi there.', 50, 5)]);
    await exchangeWith(llm, 'hello', 'Hi there.');
    let [request] = await requestsFor('hello');
    expect(request.stream_options).toEqual({ include_usage: true });
});

test('the reply is saved with its usage, so a reloaded chat shows the same figure', async ({ llm }) => {
    await setScenarios([exchange('hello', 'Hi there.', 1000, 200)]);
    await exchangeWith(llm, 'hello', 'Hi there.');
    let threads = (await api(llm, 'LLMAssistantGetThreads')).threads;
    let thread = (await api(llm, 'LLMAssistantGetThread', { threadId: threads[0].id })).thread;
    let reply = thread.messages.find((m: any) => m.role === 'assistant');
    expect(reply.meta.usage).toMatchObject({ promptTokens: 1000, completionTokens: 200, contextTokens: 1200, rounds: 1 });
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await llm.locator('.llma-thread-item').first().click();
    await expect(llm.locator('#llma-ctx-tokens')).toHaveText('1,200 / 4,000 tokens');
});

test('a model that states no window shows the count without a limit', async ({ llm }) => {
    await setScenarios([exchange('hello', 'Hi there.', 700, 50)]);
    await llm.locator('#llma-model-select').selectOption('mock-model-b');
    await exchangeWith(llm, 'hello', 'Hi there.');
    await expect(llm.locator('#llma-ctx-tokens')).toHaveText('750 tokens');
    expect(await llm.locator('#llma-ctx-fill').evaluate(el => (el as HTMLElement).style.width)).toBe('0%');
});

test('the Context Window setting overrides what the model states', async ({ llm }) => {
    await setContext(llm, { window: 10000 });
    await setScenarios([exchange('hello', 'Hi there.', 1000, 200)]);
    await exchangeWith(llm, 'hello', 'Hi there.');
    await expect(llm.locator('#llma-ctx-tokens')).toHaveText('1,200 / 10,000 tokens');
});

test('a chat that nears the window is summarized before the next reply, and the model is sent the summary', async ({ llm }) => {
    await setScenarios([
        summarizer('SUMMARY: the user greeted and counted to two.'),
        exchange('turn one', 'Reply one.', 1000, 100),
        exchange('turn two', 'Reply two.', 2000, 100),
        exchange('turn three', 'Reply three.', 3000, 300),
        exchange('turn four', 'Reply four.', 600, 100)
    ]);
    await exchangeWith(llm, 'turn one', 'Reply one.');
    await exchangeWith(llm, 'turn two', 'Reply two.');
    // 3,300 tokens of a 4,000 window is past the 80% mark: the next message is preceded by a summary.
    await exchangeWith(llm, 'turn three', 'Reply three.');
    expect(await requestsFor('Conversation to summarize:')).toHaveLength(0);
    await exchangeWith(llm, 'turn four', 'Reply four.');

    let summaryRequests = await requestsFor('Conversation to summarize:');
    expect(summaryRequests).toHaveLength(1);
    // The summarizer is not given the chat's own system prompt or tools, and sees the start of the chat.
    expect(summaryRequests[0].tools).toBeUndefined();
    expect(JSON.stringify(summaryRequests[0].messages)).toContain('turn one');

    let [chat] = await requestsFor('turn four');
    let sent = conversation(chat);
    expect(sent[0].role).toBe('user');
    expect(sent[0].content).toContain(HEADING);
    expect(sent[0].content).toContain('SUMMARY: the user greeted and counted to two.');
    // What the summary replaced is not sent again; what is newer is.
    expect(JSON.stringify(sent)).not.toContain('turn one');
    expect(JSON.stringify(sent)).toContain('turn three');
    expect(sent.at(-1).content).toBe('turn four');

    let marker = llm.locator('.llma-compaction-marker');
    await expect(marker).toHaveCount(1);
    await expect(marker.locator('summary')).toContainText('Earlier messages were summarized (2');
    await marker.locator('summary').click();
    await expect(marker.locator('.llma-compaction-text')).toContainText('SUMMARY: the user greeted');
    // The bar now reflects the shorter chat: the last reply reported 700 tokens.
    await expect(llm.locator('#llma-ctx-tokens')).toHaveText('700 / 4,000 tokens');
});

test('the marker and the summary come back when the chat is reopened', async ({ llm }) => {
    await setScenarios([
        summarizer('SUMMARY: persisted.'),
        exchange('turn one', 'Reply one.', 1000), exchange('turn two', 'Reply two.', 2000), exchange('turn three', 'Reply three.', 3300), exchange('turn four', 'Reply four.', 500)
    ]);
    for (let [text, reply] of [['turn one', 'Reply one.'], ['turn two', 'Reply two.'], ['turn three', 'Reply three.'], ['turn four', 'Reply four.']]) {
        await exchangeWith(llm, text, reply);
    }
    await expect(llm.locator('.llma-compaction-marker')).toHaveCount(1);
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await llm.locator('.llma-thread-item').first().click();
    await expect(llm.locator('.llma-compaction-marker')).toHaveCount(1);
    await expect(llm.locator('.llma-compaction-marker .llma-compaction-text')).toContainText('SUMMARY: persisted.');
});

test('Shorten Long Chats off: a chat past the mark is sent in full', async ({ llm }) => {
    await setContext(llm, { auto: false });
    await setScenarios([
        summarizer('SUMMARY: should not be written.'),
        exchange('turn one', 'Reply one.', 1000), exchange('turn two', 'Reply two.', 3900), exchange('turn three', 'Reply three.', 500)
    ]);
    await exchangeWith(llm, 'turn one', 'Reply one.');
    await exchangeWith(llm, 'turn two', 'Reply two.');
    await exchangeWith(llm, 'turn three', 'Reply three.');
    expect(await requestsFor('Conversation to summarize:')).toHaveLength(0);
    let [chat] = await requestsFor('turn three');
    expect(JSON.stringify(conversation(chat))).toContain('turn one');
    await expect(llm.locator('.llma-compaction-marker')).toHaveCount(0);
});

test('a model with no window is never summarized on its own', async ({ llm }) => {
    await setScenarios([
        summarizer('SUMMARY: should not be written.'),
        exchange('turn one', 'Reply one.', 1000), exchange('turn two', 'Reply two.', 90000), exchange('turn three', 'Reply three.', 500)
    ]);
    await llm.locator('#llma-model-select').selectOption('mock-model-b');
    await exchangeWith(llm, 'turn one', 'Reply one.');
    await exchangeWith(llm, 'turn two', 'Reply two.');
    await exchangeWith(llm, 'turn three', 'Reply three.');
    expect(await requestsFor('Conversation to summarize:')).toHaveLength(0);
});

test('a summary that fails leaves the reply to go ahead with the whole chat, and says so', async ({ llm }) => {
    await setScenarios([
        { regex: 'Conversation to summarize:', rounds: [{ error: { status: 500, message: 'summarizer is down' } }] },
        exchange('turn one', 'Reply one.', 1000), exchange('turn two', 'Reply two.', 2000), exchange('turn three', 'Reply three.', 3300), exchange('turn four', 'Reply four.', 3400)
    ]);
    for (let [text, reply] of [['turn one', 'Reply one.'], ['turn two', 'Reply two.'], ['turn three', 'Reply three.']]) {
        await exchangeWith(llm, text, reply);
    }
    await send(llm, 'turn four');
    await expect(llm.locator('.llma-toast')).toContainText('Could not shorten the chat');
    await expect(lastAssistant(llm)).toContainText('Reply four.');
    let [chat] = await requestsFor('turn four');
    expect(JSON.stringify(conversation(chat))).toContain('turn one');
    await expect(llm.locator('.llma-compaction-marker')).toHaveCount(0);
});

test('/compact summarizes the chat on request instead of sending a message', async ({ llm }) => {
    // The last reply reports a chat past the 80% mark, which is exactly what /compact is for; the next message must not summarize again.
    await setScenarios([summarizer('SUMMARY: by hand.'), exchange('one', 'A.', 100), exchange('two', 'B.', 200), exchange('three', 'C.', 3300)]);
    for (let [text, reply] of [['one', 'A.'], ['two', 'B.'], ['three', 'C.']]) {
        await exchangeWith(llm, text, reply);
    }
    let before = (await mockRequests()).length;
    await send(llm, '/compact keep the numbers');
    await expect(llm.locator('.llma-compaction-marker')).toHaveCount(1);
    await expect(llm.locator('.llma-msg-row.user')).toHaveCount(3);
    let added = (await mockRequests()).slice(before);
    expect(added).toHaveLength(1);
    expect(JSON.stringify(added[0].messages)).toContain('Also: keep the numbers');
    // The next message carries the summary.
    await exchangeWith(llm, 'four', 'Mock reply: four');
    let [chat] = await requestsFor('four');
    expect(conversation(chat)[0].content).toContain('SUMMARY: by hand.');
    // Only the /compact itself wrote a summary: the stale 3,300 reported before it did not trigger a second one.
    expect((await mockRequests()).filter(r => JSON.stringify(r.messages).includes('You shorten a conversation'))).toHaveLength(1);
});

test('/compact on a chat too short to shorten says so and sends nothing', async ({ llm }) => {
    await setScenarios([exchange('one', 'A.', 100)]);
    await exchangeWith(llm, 'one', 'A.');
    let before = (await mockRequests()).length;
    await send(llm, '/compact');
    await expect(llm.locator('.llma-toast')).toContainText('already as short as it can be');
    expect((await mockRequests()).length).toBe(before);
    await expect(llm.locator('.llma-compaction-marker')).toHaveCount(0);
});

test('a summary stops applying when a message it covers is edited, or the chat is forked before it', async ({ llm }) => {
    await setScenarios([summarizer('SUMMARY: stale soon.'), exchange('one', 'A.', 100), exchange('two', 'B.', 200), exchange('three', 'C.', 300)]);
    for (let [text, reply] of [['one', 'A.'], ['two', 'B.'], ['three', 'C.']]) {
        await exchangeWith(llm, text, reply);
    }
    await send(llm, '/compact');
    await expect(llm.locator('.llma-compaction-marker')).toHaveCount(1);
    let threadId = (await api(llm, 'LLMAssistantGetThreads')).threads[0].id;
    let get = async () => await api(llm, 'LLMAssistantGetThread', { threadId });
    let result = await get();
    expect(result.activeCompaction.summary).toBe('SUMMARY: stale soon.');
    let covered = result.thread.messages.find((m: any) => m.id === result.activeCompaction.upToMessageId);

    // Fork before it: the branch showing no longer contains the last summarized message.
    let first = result.thread.messages[0];
    await api(llm, 'LLMAssistantSetActiveLeaf', { threadId, messageId: first.id });
    expect((await get()).activeCompaction).toBeUndefined();
    await api(llm, 'LLMAssistantSetActiveLeaf', { threadId, messageId: result.thread.activeLeafId });
    expect((await get()).activeCompaction?.summary).toBe('SUMMARY: stale soon.');

    // Edit a covered message in place: what was summarized is no longer what the chat says.
    await api(llm, 'LLMAssistantEditMessage', { threadId, messageId: first.id, content: 'one, but different now' });
    expect((await get()).activeCompaction).toBeUndefined();
    expect(covered).toBeTruthy();
    await exchangeWith(llm, 'four', 'Mock reply: four');
    let [chat] = await requestsFor('four');
    expect(JSON.stringify(conversation(chat))).not.toContain(HEADING);
    expect(JSON.stringify(conversation(chat))).toContain('one, but different now');
});

test('the Context group is in settings, built from core inputs', async ({ llm }) => {
    await llm.locator('#llma-settings-btn').click();
    let group = llm.locator('#input_group_content_llma_general_context');
    await expect(group).toBeVisible();
    await expect(group.locator('.auto-input')).toHaveCount(3);
    await expect(group.locator('#llma-s-auto-compact')).toBeChecked();
    await expect(group.locator('#llma-s-compact-threshold_rangeslider')).toHaveValue('0.8');
    await expect(group.locator('#llma-s-context-window')).toHaveValue('0');
});

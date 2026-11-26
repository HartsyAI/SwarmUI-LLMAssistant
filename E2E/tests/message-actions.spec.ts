import { test, expect, send, lastAssistant, setScenarios, mockRequests, clearRequests, api, reopen, enableSessionTools, resetSessionTools, useDocumentCaps } from './fixtures';
import type { Page } from '@playwright/test';

// The per-message actions: copy, regenerate / retry, edit and resend, delete, branch from here.

const userRows = (page: Page) => page.locator('#llma-messages .llma-msg-row.user');
const replyRows = (page: Page) => page.locator('#llma-messages .llma-msg-row.assistant');
const action = (page: Page, row: ReturnType<typeof replyRows>, name: string) => row.locator('.llma-msg-actions').getByRole('button', { name, exact: true });

/** The only chat's stored messages. */
async function stored(page: Page): Promise<{ id: string; parentId: string | null; role: string; content: string }[]> {
    let listed = await api(page, 'LLMAssistantGetThreads');
    let got = await api(page, 'LLMAssistantGetThread', { threadId: listed.threads[0].id });
    return got.thread.messages;
}

/** Sends a message and waits for the whole reply. */
async function exchange(page: Page, text: string, reply: string) {
    await send(page, text);
    await expect(lastAssistant(page)).toContainText(reply);
    await expect(page.locator('#llma-send-btn')).toBeVisible();
}

test('Copy gives plain text and Copy as Markdown gives the source, without the reasoning', async ({ llm, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    let source = '## Plan\n\nUse **bold** and `code_here`.\n\n- first\n- second\n\nMath $a_1 + b_2$ stays.';
    await setScenarios([{ match: 'format me', rounds: [{ text: `<think>secret reasoning</think>${source}` }] }]);
    await exchange(llm, 'format me', 'second');
    let reply = replyRows(llm).last();
    await reply.hover();
    await action(llm, reply, 'Copy as Markdown').click();
    await expect.poll(() => llm.evaluate(() => navigator.clipboard.readText())).toContain('## Plan');
    let markdown = await llm.evaluate(() => navigator.clipboard.readText());
    expect(markdown.replace(/\r/g, '')).toBe(source);
    await action(llm, reply, 'Copy').click();
    await expect.poll(() => llm.evaluate(() => navigator.clipboard.readText())).not.toContain('##');
    let text = (await llm.evaluate(() => navigator.clipboard.readText())).replace(/\r/g, '');
    expect(text).toContain('Plan');
    expect(text).toContain('Use bold and code_here.');
    expect(text).toContain('- first\n- second');
    expect(text).toContain('$a_1 + b_2$');
    expect(text).not.toContain('**');
    expect(text).not.toContain('secret reasoning');
    let user = userRows(llm).first();
    await user.hover();
    await action(llm, user, 'Copy').click();
    await expect.poll(() => llm.evaluate(() => navigator.clipboard.readText())).toBe('format me');
});

test('Copy loads nothing the reply names: styles in it are stripped, as the chat strips them', async ({ llm, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    let loaded: string[] = [];
    llm.on('request', request => request.url().includes('llma-leak') && loaded.push(request.url()));
    let hostile = 'Hello <style>#llma-container{background-image:url(/llma-leak-sheet)}</style><div style="background-image:url(/llma-leak-attribute);height:9px">there</div>';
    await setScenarios([{ match: 'hostile', rounds: [{ text: hostile }] }]);
    await exchange(llm, 'hostile', 'there');
    let reply = replyRows(llm).last();
    await reply.hover();
    await action(llm, reply, 'Copy').click();
    await expect.poll(() => llm.evaluate(() => navigator.clipboard.readText())).toContain('there');
    expect(await llm.evaluate(() => navigator.clipboard.readText())).not.toContain('llma-leak');
    await llm.waitForTimeout(500);
    expect(loaded).toEqual([]);
});

test('a request that failed with no reply can be retried, from the failed reply or after a reload', async ({ llm }) => {
    await setScenarios([{ match: 'flaky', rounds: [{ error: { status: 500, message: 'mock upstream failure' } }] }]);
    await send(llm, 'flaky');
    await expect(llm.locator('.llma-msg-error')).toBeVisible({ timeout: 20_000 });
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    // Nothing was saved for the reply: after a reload only the question is there, with Retry under it.
    await reopen(llm);
    await expect(userRows(llm)).toHaveCount(1);
    await expect(replyRows(llm)).toHaveCount(0);
    await expect(llm.locator('.llma-msg-retry')).toBeVisible();
    // It fails again, and Regenerate on the failed reply on screen asks again for the same question.
    await llm.locator('.llma-msg-retry').click();
    await expect(llm.locator('.llma-msg-error')).toBeVisible({ timeout: 20_000 });
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await setScenarios([{ match: 'flaky', rounds: [{ text: 'worked this time' }] }]);
    let failed = replyRows(llm).last();
    await failed.hover();
    await action(llm, failed, 'Regenerate').click();
    await expect(lastAssistant(llm)).toContainText('worked this time');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await expect(userRows(llm)).toHaveCount(1);
    await expect(replyRows(llm)).toHaveCount(1);
    await expect(llm.locator('.llma-branch-pager')).toHaveCount(0);
    await expect(llm.locator('.llma-msg-retry')).toHaveCount(0);
    let messages = await stored(llm);
    expect(messages.map(m => m.role)).toEqual(['user', 'assistant']);
    expect(messages[1].parentId).toBe(messages[0].id);
});

test('Stop before anything arrived says so, saves no reply, and can be asked again', async ({ llm }) => {
    await setScenarios([{ match: 'patience', rounds: [{ delayMs: 8000, text: 'late reply' }] }]);
    await send(llm, 'patience');
    await expect(llm.locator('#llma-stop-btn')).toBeVisible();
    await llm.locator('#llma-stop-btn').click();
    await expect(llm.locator('.llma-msg-stopped')).toBeVisible();
    await expect(llm.locator('.llma-typing')).toHaveCount(0);
    await setScenarios([{ match: 'patience', rounds: [{ text: 'prompt reply' }] }]);
    let stopped = replyRows(llm).last();
    await stopped.hover();
    await action(llm, stopped, 'Regenerate').click();
    await expect(lastAssistant(llm)).toContainText('prompt reply');
    await expect(replyRows(llm)).toHaveCount(1);
});

test('the actions that change the chat are off while a reply streams', async ({ llm }) => {
    let long = Array.from({ length: 120 }, (_, i) => `word${i}`).join(' ');
    await setScenarios([{ match: 'busy', rounds: [{ text: long, chunkDelayMs: 100 }] }]);
    await send(llm, 'busy');
    await expect(lastAssistant(llm)).toContainText('word1');
    let user = userRows(llm).first();
    await user.hover();
    for (let name of ['Edit', 'Delete', 'Branch from here']) {
        await expect(action(llm, user, name)).toHaveAttribute('aria-disabled', 'true');
    }
    await expect(action(llm, user, 'Copy')).not.toHaveAttribute('aria-disabled', 'true');
    await action(llm, user, 'Delete').click({ force: true });
    await expect(llm.locator('.llma-toast')).toContainText('Stop generation before deleting');
    await expect(userRows(llm)).toHaveCount(1);
    await llm.locator('#llma-stop-btn').click();
    await expect(action(llm, user, 'Delete')).toHaveAttribute('aria-disabled', 'false');
});

test('edit: Cancel and Escape put the message back, Ctrl+Enter resends it as a new branch and the later messages leave the view', async ({ llm }) => {
    await setScenarios([
        { match: 'draft one', rounds: [{ text: 'reply to draft' }] },
        { match: 'follow up', rounds: [{ text: 'reply to follow up' }] },
        { match: 'final wording', rounds: [{ text: 'reply to final' }] }
    ]);
    await exchange(llm, 'draft one', 'reply to draft');
    await exchange(llm, 'follow up', 'reply to follow up');
    let first = userRows(llm).first();
    await first.hover();
    await action(llm, first, 'Edit').click();
    await first.locator('textarea').fill('thrown away');
    await first.getByRole('button', { name: 'Cancel' }).click();
    await expect(first.locator('.llma-msg-bubble')).toHaveText('draft one');
    await expect(action(llm, first, 'Edit')).toBeFocused();
    await action(llm, first, 'Edit').click();
    await first.locator('textarea').press('Escape');
    await expect(first.locator('textarea')).toHaveCount(0);
    await action(llm, first, 'Edit').click();
    await first.locator('textarea').fill('final wording');
    await first.locator('textarea').press('Control+Enter');
    await expect(lastAssistant(llm)).toContainText('reply to final');
    await expect(userRows(llm)).toHaveCount(1);
    await expect(llm.locator('#llma-messages')).not.toContainText('follow up');
    // The model was sent the edited wording and none of what came after the old one.
    let sent = (await mockRequests()).filter(r => r.stream && r.messages.some((m: any) => m.content === 'final wording')).pop();
    expect(sent.messages.filter((m: any) => m.role !== 'system').map((m: any) => m.content)).toEqual(['final wording']);
    expect((await stored(llm)).length).toBe(6);
});

test('Branch from here shows the chat up to that reply, the next message branches, and the rest stays reachable', async ({ llm }) => {
    await setScenarios([
        { match: 'alpha', rounds: [{ text: 'reply alpha' }] },
        { match: 'beta', rounds: [{ text: 'reply beta' }] },
        { match: 'gamma', rounds: [{ text: 'reply gamma' }] }
    ]);
    await exchange(llm, 'alpha', 'reply alpha');
    await exchange(llm, 'beta', 'reply beta');
    let first = replyRows(llm).first();
    await first.hover();
    await action(llm, first, 'Branch from here').click();
    await expect(userRows(llm)).toHaveCount(1);
    await expect(llm.locator('#llma-input')).toBeFocused();
    // Nothing has branched yet, so the way back is the row under the last message.
    await llm.locator('.llma-msg-later').click();
    await expect(userRows(llm)).toHaveCount(2);
    await expect(llm.locator('.llma-msg-tail')).toHaveCount(0);
    await first.hover();
    await action(llm, first, 'Branch from here').click();
    await expect(userRows(llm)).toHaveCount(1);
    await exchange(llm, 'gamma', 'reply gamma');
    await expect(llm.locator('#llma-messages')).not.toContainText('reply beta');
    await expect(llm.locator('.llma-branch-pager')).toHaveCount(1);
    await llm.getByRole('button', { name: 'Previous branch' }).click();
    await expect(lastAssistant(llm)).toContainText('reply beta');
});

test('delete asks before it takes other messages with it, and does not ask for a last reply', async ({ llm }) => {
    await setScenarios([
        { match: 'keep', rounds: [{ text: 'reply keep' }] },
        { match: 'drop', rounds: [{ text: 'reply drop' }] }
    ]);
    await exchange(llm, 'keep', 'reply keep');
    await exchange(llm, 'drop', 'reply drop');
    let questions: string[] = [];
    let answer = false;
    llm.on('dialog', dialog => {
        questions.push(dialog.message());
        return answer ? dialog.accept() : dialog.dismiss();
    });
    let second = userRows(llm).last();
    await second.hover();
    await action(llm, second, 'Delete').click();
    // The stored chat is read first, so the question comes a moment after the click.
    await expect.poll(() => questions.length).toBe(1);
    expect(questions[0]).toContain('1 message under it');
    await expect(userRows(llm)).toHaveCount(2);
    // The last reply takes nothing with it, so there is no question.
    let reply = replyRows(llm).last();
    await reply.hover();
    await action(llm, reply, 'Delete').click();
    await expect(replyRows(llm)).toHaveCount(1);
    expect(questions).toHaveLength(1);
    await expect(llm.locator('.llma-msg-retry')).toBeVisible();
    answer = true;
    let first = userRows(llm).first();
    await first.hover();
    await action(llm, first, 'Delete').click();
    await expect(llm.locator('#llma-messages .llma-msg-row')).toHaveCount(0);
    expect(questions[1]).toContain('every message under it');
    expect(await stored(llm)).toHaveLength(0);
});

/** Where the page thinks the next message goes, and the parents it gives the stored messages. */
async function pageTree(page: Page): Promise<{ leaf: string; parents: Record<string, string | null> }> {
    return await page.evaluate(() => {
        let state = (0, eval)('LLMAState');
        let parents: Record<string, string | null> = {};
        for (let node of state.allNodes.filter((n: any) => !n.unsaved)) {
            parents[node.id] = node.parentId || null;
        }
        return { leaf: state.activeLeafId, parents };
    });
}

/** Expects the page and the server to hold the same tree and the same tip. */
async function expectSameTree(page: Page) {
    await expect.poll(async () => {
        let listed = await api(page, 'LLMAssistantGetThreads');
        let saved = (await api(page, 'LLMAssistantGetThread', { threadId: listed.threads[0].id })).thread;
        let here = await pageTree(page);
        let parents = Object.fromEntries(saved.messages.map((m: any) => [m.id, m.parentId || null]));
        return JSON.stringify({ leaf: here.leaf == saved.activeLeafId, parents: Object.entries(here.parents).sort() }) ==
            JSON.stringify({ leaf: true, parents: Object.entries(parents).sort() });
    }).toBe(true);
}

/** Answers every confirm with `accept`, and collects the questions. */
function questionsAsked(page: Page, accept: boolean): string[] {
    let questions: string[] = [];
    page.on('dialog', dialog => {
        questions.push(dialog.message());
        return accept ? dialog.accept() : dialog.dismiss();
    });
    return questions;
}

test('after Stop the next message hangs under the kept reply, and deleting that reply asks first', async ({ llm }) => {
    let long = Array.from({ length: 150 }, (_, i) => `word${i}`).join(' ');
    await setScenarios([{ match: 'cut me off', rounds: [{ text: long, chunkDelayMs: 80 }] }, { match: 'carry on', rounds: [{ text: 'carried on' }] }]);
    await send(llm, 'cut me off');
    await expect(lastAssistant(llm)).toContainText('word3');
    await llm.locator('#llma-stop-btn').click();
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await exchange(llm, 'carry on', 'carried on');
    await expectSameTree(llm);
    let messages = await stored(llm);
    expect(messages.map(m => m.role)).toEqual(['user', 'assistant', 'user', 'assistant']);
    expect(messages[2].parentId).toBe(messages[1].id);
    let questions = questionsAsked(llm, false);
    let stopped = replyRows(llm).first();
    await stopped.hover();
    await action(llm, stopped, 'Delete').click();
    await expect.poll(() => questions.length).toBe(1);
    expect(questions[0]).toContain('(2)');
    expect(await stored(llm)).toHaveLength(4);
});

test('delete counts what the server holds, also what this page never saw', async ({ llm }) => {
    await setScenarios([{ match: 'here', rounds: [{ text: 'reply here' }] }, { match: 'elsewhere', rounds: [{ text: 'reply elsewhere' }] }]);
    await exchange(llm, 'here', 'reply here');
    let threadId = (await api(llm, 'LLMAssistantGetThreads')).threads[0].id;
    // Another client adds a turn under the reply on screen.
    expect((await api(llm, 'LLMAssistantRunTurn', { threadId, message: 'elsewhere' })).success).toBe(true);
    let questions = questionsAsked(llm, false);
    let reply = replyRows(llm).first();
    await reply.hover();
    await action(llm, reply, 'Delete').click();
    await expect.poll(() => questions.length).toBe(1);
    expect(questions[0]).toContain('(2)');
    expect(await stored(llm)).toHaveLength(4);
});

test('deleting an old failed reply keeps the later messages on screen', async ({ llm }) => {
    await setScenarios([{ match: 'stumble', rounds: [{ error: { status: 500, message: 'mock upstream failure' } }] }, { match: 'onwards', rounds: [{ text: 'reply onwards' }] }]);
    await send(llm, 'stumble');
    await expect(llm.locator('.llma-msg-error')).toBeVisible({ timeout: 20_000 });
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await exchange(llm, 'onwards', 'reply onwards');
    await expect(replyRows(llm)).toHaveCount(2);
    let questions = questionsAsked(llm, false);
    let failed = replyRows(llm).first();
    await failed.hover();
    await action(llm, failed, 'Delete').click();
    await expect(replyRows(llm)).toHaveCount(1);
    await expect(userRows(llm)).toHaveCount(2);
    await expect(lastAssistant(llm)).toContainText('reply onwards');
    expect(questions).toEqual([]);
    expect(await stored(llm)).toHaveLength(3);
    await expectSameTree(llm);
});

test('a reply the page wrongly takes for unsaved is still deleted on the server', async ({ llm }) => {
    await setScenarios([{ match: 'solid', rounds: [{ text: 'solid reply' }] }]);
    await exchange(llm, 'solid', 'solid reply');
    // As after a socket that dropped before the first token while the server went on to save the reply.
    await llm.evaluate(() => {
        let state = (0, eval)('LLMAState');
        state.messages[state.messages.length - 1].unsaved = true;
    });
    let reply = replyRows(llm).first();
    await reply.hover();
    await action(llm, reply, 'Delete').click();
    await expect(replyRows(llm)).toHaveCount(0);
    await expect.poll(async () => (await stored(llm)).length).toBe(1);
    await reopen(llm);
    await expect(replyRows(llm)).toHaveCount(0);
});

test('compare: a model that fails does not take the other with it, and the next message hangs where the server puts it', async ({ llm }) => {
    await setScenarios([
        { match: 'duel', model: 'mock-model', rounds: [{ error: { status: 500, message: 'lane one broke' } }] },
        { match: 'duel', model: 'mock-model-b', rounds: [{ delayMs: 1500, text: 'lane two answered' }] },
        { match: 'afterwards', rounds: [{ text: 'afterwards reply' }] }
    ]);
    await llm.locator('#llma-compare-toggle').click();
    await llm.locator('#llma-model-select-b').selectOption('mock-model-b');
    await send(llm, 'duel');
    let columns = llm.locator('.llma-compare-col');
    await expect(columns.nth(1)).toContainText('lane two answered', { timeout: 30_000 });
    await expect(columns.nth(0).locator('.llma-msg-error')).toBeVisible();
    await expect(columns.nth(1).locator('.llma-msg-error')).toHaveCount(0);
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await expect.poll(async () => (await stored(llm)).length).toBe(2);
    await llm.locator('#llma-compare-toggle').click();
    await exchange(llm, 'afterwards', 'afterwards reply');
    let messages = await stored(llm);
    let next = messages.find(m => m.content == 'afterwards')!;
    expect(next.parentId).toBe(messages[0].id);
    await expectSameTree(llm);
});

test('the branch cannot be switched while a reply streams, from the sidebar either', async ({ llm }) => {
    let long = Array.from({ length: 120 }, (_, i) => `word${i}`).join(' ');
    await setScenarios([{ match: 'streaming now', rounds: [{ text: long, chunkDelayMs: 100 }] }]);
    await send(llm, 'streaming now');
    await expect(lastAssistant(llm)).toContainText('word1');
    let leaf = await llm.evaluate(async () => {
        let state = (0, eval)('LLMAState');
        let before = state.activeLeafId;
        await (0, eval)('llmaJumpToBranch')(state.activeThreadId, state.allNodes[0].id + '-other');
        return { before, after: state.activeLeafId };
    });
    expect(leaf.after).toBe(leaf.before);
    await expect(llm.locator('.llma-toast')).toContainText('Stop generation before switching branches');
    await llm.locator('#llma-stop-btn').click();
});

test('Copy keeps think tags and dollar signs that are inside code', async ({ llm, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    let source = 'Write `<think>` or `$HOME` for $5.\n\n```html\n<think>kept in code</think>\necho $$ $A_1\n```\n\nDone';
    await setScenarios([{ match: 'tags in code', rounds: [{ text: `<think>real reasoning</think>${source}` }] }]);
    await exchange(llm, 'tags in code', 'Done');
    let reply = replyRows(llm).last();
    await reply.hover();
    await action(llm, reply, 'Copy as Markdown').click();
    await expect.poll(() => llm.evaluate(() => navigator.clipboard.readText())).toContain('Done');
    expect((await llm.evaluate(() => navigator.clipboard.readText())).replace(/\r/g, '')).toBe(source);
    await action(llm, reply, 'Copy').click();
    await expect.poll(() => llm.evaluate(() => navigator.clipboard.readText())).not.toContain('```');
    let text = (await llm.evaluate(() => navigator.clipboard.readText())).replace(/\r/g, '');
    expect(text).toContain('Write <think> or $HOME for $5.');
    expect(text).toContain('<think>kept in code</think>\necho $$ $A_1');
    expect(text).not.toContain('real reasoning');
});

test('with no clipboard API, Copy says whether the old copy command worked and gives focus back', async ({ llm }) => {
    await setScenarios([{ match: 'over lan', rounds: [{ text: 'plain words' }] }]);
    await exchange(llm, 'over lan', 'plain words');
    // A page served over plain HTTP from another machine: no clipboard API, only the copy command, which may refuse.
    await llm.evaluate(() => {
        Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true });
        (window as any).copyWorks = false;
        document.execCommand = () => {
            (window as any).copied = (document.activeElement as HTMLTextAreaElement).value;
            return (window as any).copyWorks;
        };
    });
    let reply = replyRows(llm).last();
    let copy = action(llm, reply, 'Copy');
    await copy.focus();
    await llm.keyboard.press('Enter');
    await expect(llm.locator('.llma-toast')).toContainText('Could not copy');
    await expect(copy).toBeFocused();
    await llm.evaluate(() => (window as any).copyWorks = true);
    await llm.keyboard.press('Enter');
    await expect(llm.locator('.llma-toast')).toContainText('Copied!');
    await expect(copy).toBeFocused();
    expect(await llm.evaluate(() => (window as any).copied)).toBe('plain words');
});

/** Makes the page give up on the streaming reply, as when its connection fails, while the server goes on writing it. */
async function loseConnection(page: Page) {
    await page.evaluate(() => {
        let socket = (0, eval)('LLMAState')._activeSocket as WebSocket;
        socket.onmessage = null;
        (socket.onerror as () => void)();
    });
    await expect(page.locator('#llma-send-btn')).toBeVisible();
}

const WORDS = Array.from({ length: 40 }, (_, i) => `word${i}`).join(' ');

test('deleting a reply the page lost is not undone by the server finishing it', async ({ llm }) => {
    test.slow();
    await setScenarios([{ match: 'lost reply', rounds: [{ text: WORDS, chunkDelayMs: 150 }] }]);
    await send(llm, 'lost reply');
    await expect(lastAssistant(llm)).toContainText('word3');
    await loseConnection(llm);
    let reply = replyRows(llm).last();
    await reply.hover();
    await action(llm, reply, 'Delete').click();
    await expect(replyRows(llm)).toHaveCount(0);
    // Longer than the rest of the reply would have taken.
    await llm.waitForTimeout(8000);
    expect(await stored(llm)).toHaveLength(1);
});

test('deleting a question whose reply never reached the page stops that reply', async ({ llm }) => {
    test.slow();
    await clearRequests();
    await setScenarios([{ match: 'silent start', rounds: [{ delayMs: 8000, text: 'too late' }] }]);
    await send(llm, 'silent start');
    await expect.poll(() => mockRequests().then(requests => requests.length)).toBeGreaterThan(0);
    await loseConnection(llm);
    let questions = questionsAsked(llm, true);
    let question = userRows(llm).first();
    await question.hover();
    await action(llm, question, 'Delete').click();
    await expect(userRows(llm)).toHaveCount(0);
    // The reply on its way counts as a message under the question.
    expect(questions).toHaveLength(1);
    await expect(llm.locator('#llma-announcer')).toContainText('Message deleted');
    await llm.waitForTimeout(9000);
    expect(await stored(llm)).toHaveLength(0);
});

for (let lost of [
    { when: 'before its first word', round: { delayMs: 3000, text: 'arrived late' }, seen: '' },
    { when: 'part way', round: { text: `${WORDS.slice(0, 80)} arrived late`, chunkDelayMs: 200 }, seen: 'word2' },
]) {
    test(`a reply the page lost ${lost.when} is drawn once the server has it`, async ({ llm }) => {
        test.slow();
        await clearRequests();
        await setScenarios([{ match: 'late one', rounds: [lost.round] }]);
        await send(llm, 'late one');
        await expect.poll(() => mockRequests().then(requests => requests.length)).toBeGreaterThan(0);
        if (lost.seen) {
            await expect(lastAssistant(llm)).toContainText(lost.seen);
        }
        await loseConnection(llm);
        await expect(llm.locator('.llma-msg-error')).toBeVisible();
        await expect.poll(async () => (await stored(llm)).length, { timeout: 30_000 }).toBe(2);
        // What every later action does: reads the chat and takes the server's tree.
        await llm.evaluate(() => (0, eval)('llmaSaveActiveThread')());
        await expect(lastAssistant(llm)).toContainText('arrived late');
        await expect(llm.locator('.llma-msg-error')).toHaveCount(0);
        await expect(replyRows(llm)).toHaveCount(1);
        await expectSameTree(llm);
    });
}

test('a delete stops nothing for a reply that failed, nor when the answer is no', async ({ llm }) => {
    test.slow();
    let stops = 0;
    llm.on('request', request => request.url().endsWith('/API/LLMAssistantStopGeneration') && stops++);
    await setScenarios([{ match: 'stumble', rounds: [{ error: { status: 500, message: 'mock upstream failure' } }] }, { match: 'silent start', rounds: [{ delayMs: 6000, text: 'kept after all' }] }]);
    await send(llm, 'stumble');
    await expect(llm.locator('.llma-msg-error')).toBeVisible({ timeout: 20_000 });
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    let question = userRows(llm).first();
    await question.hover();
    await action(llm, question, 'Delete').click();
    await expect(userRows(llm)).toHaveCount(0);
    expect(stops).toBe(0);
    await clearRequests();
    await send(llm, 'silent start');
    await expect.poll(() => mockRequests().then(requests => requests.length)).toBeGreaterThan(0);
    await loseConnection(llm);
    let questions = questionsAsked(llm, false);
    await question.hover();
    await action(llm, question, 'Delete').click();
    await expect.poll(() => questions.length).toBe(1);
    // The reply goes on being written, and arrives.
    await expect.poll(async () => (await stored(llm)).map(m => m.content), { timeout: 30_000 }).toEqual(['silent start', 'kept after all']);
    expect(stops).toBe(0);
});

/** How many replies the page still takes to be possibly being written. */
const lostCount = (page: Page): Promise<number> => page.evaluate(() => (0, eval)('llmaActions').lost.size);

test('a lost reply is forgotten once a read shows it stored', async ({ llm }) => {
    test.slow();
    await clearRequests();
    await setScenarios([{ match: 'slow one', rounds: [{ delayMs: 3000, text: 'stored after all' }] }]);
    await send(llm, 'slow one');
    await expect.poll(() => mockRequests().then(requests => requests.length)).toBeGreaterThan(0);
    await loseConnection(llm);
    expect(await lostCount(llm)).toBe(1);
    await expect.poll(async () => (await stored(llm)).length, { timeout: 30_000 }).toBe(2);
    await llm.evaluate(() => (0, eval)('llmaSaveActiveThread')());
    await expect(lastAssistant(llm)).toContainText('stored after all');
    expect(await lostCount(llm)).toBe(0);
    // Deleting the reply and then the question asks nothing and stops nothing: no phantom reply is counted.
    let stops = 0;
    llm.on('request', request => request.url().endsWith('/API/LLMAssistantStopGeneration') && stops++);
    let questions = questionsAsked(llm, true);
    let reply = replyRows(llm).first();
    await reply.hover();
    await action(llm, reply, 'Delete').click();
    await expect(replyRows(llm)).toHaveCount(0);
    let question = userRows(llm).first();
    await question.hover();
    await action(llm, question, 'Delete').click();
    await expect(userRows(llm)).toHaveCount(0);
    expect(questions).toEqual([]);
    expect(stops).toBe(0);
});

test('a lost reply is kept until the server takes up a new turn', async ({ llm }) => {
    test.slow();
    await clearRequests();
    await setScenarios([{ match: 'slow one', rounds: [{ delayMs: 20_000, text: 'never mind' }] }, { match: 'next one', rounds: [{ text: 'next reply' }] }]);
    await send(llm, 'slow one');
    await expect.poll(() => mockRequests().then(requests => requests.length)).toBeGreaterThan(0);
    await loseConnection(llm);
    // A turn that starts in the page and is never answered changes nothing.
    await llm.evaluate(() => {
        (0, eval)('llmaSetStreaming')(true);
        (0, eval)('llmaSetStreaming')(false);
    });
    expect(await lostCount(llm)).toBe(1);
    await exchange(llm, 'next one', 'next reply');
    expect(await lostCount(llm)).toBe(0);
});

test('catching up with a late reply leaves a reader who scrolled away where they are', async ({ llm }) => {
    test.slow();
    await clearRequests();
    let tall = Array.from({ length: 60 }, (_, i) => `line ${i}`).join('\n\n');
    await setScenarios([{ match: 'tall one', rounds: [{ text: tall }] }, { match: 'late one', rounds: [{ delayMs: 3000, text: 'arrived late' }] }]);
    await exchange(llm, 'tall one', 'line 59');
    await clearRequests();
    await send(llm, 'late one');
    await expect.poll(() => mockRequests().then(requests => requests.length)).toBeGreaterThan(0);
    await loseConnection(llm);
    await expect.poll(async () => (await stored(llm)).length, { timeout: 30_000 }).toBe(4);
    let list = llm.locator('#llma-messages');
    await list.evaluate(e => e.scrollTo({ top: 40, behavior: 'instant' }));
    await llm.evaluate(() => (0, eval)('llmaSaveActiveThread')());
    await expect(lastAssistant(llm)).toContainText('arrived late');
    await llm.waitForTimeout(1000);
    expect(await list.evaluate(e => e.scrollTop)).toBeLessThan(100);
});

test('a message\'s documents stay with it through edit, retry, branching and a redraw', async ({ llm }) => {
    test.slow();
    await setScenarios([{ match: 'with a file', rounds: [{ error: { status: 500, message: 'mock upstream failure' } }] }]);
    await llm.locator('#llma-attach-file').setInputFiles([{ name: 'plans.txt', mimeType: 'text/plain', buffer: Buffer.from('The launch is on Friday.') }]);
    await expect(llm.locator('#llma-document-chips .llma-doc-chip')).toHaveCount(1, { timeout: 20_000 });
    await expect(llm.locator('.llma-doc-chip-reading')).toHaveCount(0, { timeout: 20_000 });
    await send(llm, 'with a file');
    await expect(llm.locator('.llma-msg-error')).toBeVisible({ timeout: 20_000 });
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    let chips = userRows(llm).locator('.llma-doc-chip');
    await expect(chips).toContainText('plans.txt');
    // Retry.
    await setScenarios([{ match: 'with a file', rounds: [{ text: 'first answer' }] }, { match: 'new wording', rounds: [{ text: 'second answer' }] }]);
    await llm.locator('.llma-msg-retry').click();
    await expect(lastAssistant(llm)).toContainText('first answer');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await expect(chips).toContainText('plans.txt');
    // Cancel puts the chip back; Save & resend carries it to the new branch.
    let user = userRows(llm).first();
    await user.hover();
    await action(llm, user, 'Edit').click();
    await user.locator('textarea').press('Escape');
    await expect(chips).toContainText('plans.txt');
    await user.hover();
    await action(llm, user, 'Edit').click();
    await user.locator('textarea').fill('new wording');
    await user.locator('textarea').press('Control+Enter');
    await expect(lastAssistant(llm)).toContainText('second answer');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await expect(chips).toContainText('plans.txt');
    let messages = await stored(llm) as any[];
    expect(messages.filter(m => m.role == 'user').map(m => m.documents?.[0]?.name)).toEqual(['plans.txt', 'plans.txt']);
    // Branch from here, then a redraw from the server's tree.
    let reply = replyRows(llm).last();
    await reply.hover();
    await action(llm, reply, 'Branch from here').click();
    await llm.evaluate(() => (0, eval)('llmaSaveActiveThread')());
    await expect(chips).toContainText('plans.txt');
    await llm.evaluate(() => (0, eval)('llmaReloadActiveThread')());
    await expect(chips).toContainText('plans.txt');
    // The request for the new branch carried the file's words.
    let sent = (await mockRequests()).map(r => JSON.stringify(r.messages ?? [])).filter(text => text.includes('new wording'));
    expect(sent[0]).toContain('The launch is on Friday.');
});

test('reading a chat takes its status as read, not what the list last said', async ({ llm }) => {
    await exchange(llm, 'status', 'Mock reply');
    let remove = action(llm, userRows(llm).first(), 'Delete');
    for (let read of ['llmaReloadActiveThread()', 'llmaSwitchThread(LLMAState.activeThreadId)', 'llmaSaveActiveThread()']) {
        await llm.evaluate(() => {
            (0, eval)('LLMAState').threads[0].sessionStatus = 'running';
            (0, eval)('llmaActions').syncTail();
        });
        await expect(remove).toHaveAttribute('aria-disabled', 'true');
        await llm.evaluate(code => (0, eval)(code), read);
        await expect(remove).toHaveAttribute('aria-disabled', 'false');
    }
});

test.describe('a session working in the background', () => {
    test.beforeEach(async ({ llm }) => {
        await enableSessionTools(llm);
    });

    test.afterEach(async ({ llm }) => {
        await resetSessionTools(llm);
    });

    /** A chat with a session that is still writing its first reply, slowly enough to outlast a test. Returns the session's chat id. */
    async function workingSession(page: Page): Promise<string> {
        let parent = (await api(page, 'LLMAssistantCreateThread', { assistantId: 'default' })).thread.id;
        let slow = Array.from({ length: 600 }, (_, i) => `slow${i}`).join(' ');
        await setScenarios([{ match: 'long job', rounds: [{ text: slow, chunkDelayMs: 200 }] }]);
        let started = await api(page, 'LLMAssistantExecuteTool', { toolId: 'session_start', threadId: parent, arguments: { title: 'Long job', prompt: 'long job', model: 'mock-model' } });
        let child = started.result.id;
        await expect.poll(async () => (await api(page, 'LLMAssistantGetThread', { threadId: child })).thread.sessionStatus, { timeout: 30_000 }).toBe('running');
        return child;
    }

    test('its chat offers no action that changes it', async ({ llm }) => {
        await workingSession(llm);
        await reopen(llm);
        await llm.locator('.llma-thread-child').click();
        await expect(llm.locator('#llma-session-banner')).toContainText('Running');
        let user = userRows(llm).first();
        await user.hover();
        for (let name of ['Edit', 'Delete', 'Branch from here']) {
            await expect(action(llm, user, name)).toHaveAttribute('aria-disabled', 'true');
        }
        await expect(llm.locator('.llma-msg-retry')).toHaveCount(0);
        await action(llm, user, 'Delete').click({ force: true });
        await expect(llm.locator('.llma-toast')).toContainText('working in the background');
        await expect(userRows(llm)).toHaveCount(1);
        await llm.locator('#llma-session-banner').getByRole('button', { name: 'Stop' }).click();
        await expect(action(llm, userRows(llm).first(), 'Delete')).toHaveAttribute('aria-disabled', 'false', { timeout: 30_000 });
    });

    for (let route of ['LLMAssistantRegenerateWS', 'LLMAssistantEditMessageWS']) {
        test(`${route} with a message that is not there leaves the turn running`, async ({ llm }) => {
            let child = await workingSession(llm);
            let done = await llm.evaluate(([name, body]) => new Promise<any>(resolve => {
                (0, eval)('makeWSRequest')(name, body, (data: any) => data.done && resolve(data), 0, (error: string) => resolve({ error }));
            }), [route, { threadId: child, messageId: 'no-such-message', content: 'new wording', model: 'mock-model' }] as const);
            expect(done.error).toContain('not found');
            expect((await api(llm, 'LLMAssistantGetThread', { threadId: child })).thread.sessionStatus).toBe('running');
            await api(llm, 'LLMAssistantStopGeneration', { threadId: child });
        });

        test(`${route} stops its turn first, so the new reply lands where it was asked for`, async ({ llm }) => {
            let child = await workingSession(llm);
            let before = (await api(llm, 'LLMAssistantGetThread', { threadId: child })).thread.messages;
            // Long enough for some of the reply to have arrived: it is saved when the turn is stopped.
            await llm.waitForTimeout(2000);
            await setScenarios([{ match: 'long job', rounds: [{ text: 'fresh answer' }] }, { match: 'new wording', rounds: [{ text: 'fresh answer' }] }]);
            let done = await llm.evaluate(([name, body]) => new Promise<any>(resolve => {
                (0, eval)('makeWSRequest')(name, body, (data: any) => data.done && resolve(data), 0, (error: string) => resolve({ error }));
            }), [route, { threadId: child, messageId: before[0].id, content: 'new wording', model: 'mock-model' }] as const);
            expect(done.error).toBeUndefined();
            await llm.waitForTimeout(1000);
            let after = (await api(llm, 'LLMAssistantGetThread', { threadId: child })).thread;
            let replies = after.messages.filter((m: any) => m.role == 'assistant' && m.content == 'fresh answer');
            expect(replies).toHaveLength(1);
            // The reply answers the message that was asked about, not what the stopped turn saved on its way out.
            let asked = route == 'LLMAssistantRegenerateWS' ? before[0] : after.messages.find((m: any) => m.content == 'new wording');
            expect(replies[0].parentId).toBe(asked.id);
            expect(after.activeLeafId).toBe(replies[0].id);
            expect(after.sessionStatus).not.toBe('running');
        });
    }
});

test('deleting the branch on screen leaves a way to the other one', async ({ llm }) => {
    await setScenarios([{ match: 'twice', rounds: [{ text: 'take one' }] }]);
    await exchange(llm, 'twice', 'take one');
    await setScenarios([{ match: 'twice', rounds: [{ text: 'take two' }] }]);
    let reply = replyRows(llm).last();
    await reply.hover();
    await action(llm, reply, 'Regenerate').click();
    await expect(lastAssistant(llm)).toContainText('take two');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    reply = replyRows(llm).last();
    await reply.hover();
    await action(llm, reply, 'Delete').click();
    await expect(replyRows(llm)).toHaveCount(0);
    await llm.locator('.llma-msg-later').click();
    await expect(lastAssistant(llm)).toContainText('take one');
});

for (let compare of [false, true]) {
    test(`an error as the first thing a turn says ends the wait for a lost reply, a refusal does not${compare ? ', with two models' : ''}`, async ({ llm }) => {
        test.slow();
        await clearRequests();
        await setScenarios([{ match: 'slow one', rounds: [{ delayMs: 30_000, text: 'never mind' }] }, { match: 'broken one', rounds: [{ error: { status: 500, message: 'mock upstream failure' } }] }]);
        if (compare) {
            await llm.locator('#llma-compare-toggle').click();
            await llm.locator('#llma-model-select-b').selectOption('mock-model-b');
        }
        let lost = compare ? 2 : 1;
        await send(llm, 'slow one');
        await expect.poll(() => mockRequests().then(requests => requests.length)).toBeGreaterThanOrEqual(lost);
        await loseConnection(llm);
        expect(await lostCount(llm)).toBe(lost);
        // Refused for its documents: the server takes no turn up, and what it was writing goes on.
        try {
            await llm.locator('#llma-attach-file').setInputFiles([1, 2].map(n => ({ name: `d${n}.txt`, mimeType: 'text/plain', buffer: Buffer.from(`words ${n}`) })));
            await expect(llm.locator('#llma-document-chips .llma-doc-chip')).toHaveCount(2, { timeout: 20_000 });
            await expect(llm.locator('.llma-doc-chip-reading')).toHaveCount(0, { timeout: 20_000 });
            await useDocumentCaps(llm, { documentMaxPerMessage: 1 });
            await send(llm, 'refused one');
            await expect(llm.locator('.llma-toast')).toContainText('at most 1 documents');
            await expect(llm.locator('#llma-send-btn')).toBeVisible();
            expect(await lostCount(llm)).toBe(lost);
        }
        finally {
            await useDocumentCaps(llm, { documentMaxPerMessage: 5 });
        }
        for (let left of [1, 0]) {
            await llm.locator('#llma-document-chips .llma-doc-chip-remove').first().click();
            await expect(llm.locator('#llma-document-chips .llma-doc-chip')).toHaveCount(left);
        }
        await send(llm, 'broken one');
        await expect(llm.locator('.llma-msg-error').last()).toBeVisible({ timeout: 20_000 });
        await expect(llm.locator('#llma-send-btn')).toBeVisible();
        await expect.poll(() => lostCount(llm)).toBe(0);
    });
}

test('a tool run by hand is saved under its row\'s id: it can be deleted or regenerated at once', async ({ llm }) => {
    await setScenarios([{ match: 'before the tool', rounds: [{ text: 'first reply' }] }]);
    await exchange(llm, 'before the tool', 'first reply');
    await llm.evaluate(() => (0, eval)('llmaRunToolDirect')({ id: 'memory_read' }, {}));
    await expect(replyRows(llm)).toHaveCount(2);
    let row = replyRows(llm).last();
    let id = (await row.getAttribute('data-msg-id'))!;
    let messages = await stored(llm);
    expect(messages.map(m => m.id)).toContain(id);
    expect(messages.find(m => m.id == id)!.parentId).toBe(messages[1].id);
    await expectSameTree(llm);
    // A second call under an id the chat holds is refused before the tool runs.
    let listed = await api(llm, 'LLMAssistantGetThreads');
    expect(await api(llm, 'LLMAssistantExecuteTool', { toolId: 'memory_read', arguments: {}, threadId: listed.threads[0].id, messageId: id })).toMatchObject({ success: false });
    expect(await stored(llm)).toHaveLength(3);
    await row.hover();
    await action(llm, row, 'Delete').click();
    await expect(replyRows(llm)).toHaveCount(1);
    await expect(llm.locator('#llma-announcer')).toContainText('Message deleted');
    expect(await stored(llm)).toHaveLength(2);
    // Regenerate asks for another reply in its place, as it does after a reload.
    await llm.evaluate(() => (0, eval)('llmaRunToolDirect')({ id: 'memory_read' }, {}));
    await expect(replyRows(llm)).toHaveCount(2);
    row = replyRows(llm).last();
    await row.hover();
    await action(llm, row, 'Regenerate').click();
    await expect.poll(async () => (await stored(llm)).length, { timeout: 30_000 }).toBe(4);
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    let after = await stored(llm);
    expect(after[3].parentId).toBe(after[2].parentId);
    await expect(llm.locator('.llma-toast', { hasText: 'Nothing to regenerate from' })).toHaveCount(0);
});

test('deleting a message the saved chat does not hold says that, not that it was deleted', async ({ llm }) => {
    await setScenarios([{ match: 'twice deleted', rounds: [{ text: 'the reply' }] }]);
    await exchange(llm, 'twice deleted', 'the reply');
    let listed = await api(llm, 'LLMAssistantGetThreads');
    let reply = replyRows(llm).first();
    // Deleted from somewhere else (another tab) while this page still shows it.
    expect(await api(llm, 'LLMAssistantDeleteMessage', { threadId: listed.threads[0].id, messageId: await reply.getAttribute('data-msg-id') })).toMatchObject({ success: true });
    await reply.hover();
    await action(llm, reply, 'Delete').click();
    await expect(replyRows(llm)).toHaveCount(0);
    await expect(llm.locator('#llma-announcer')).toContainText('not in the saved chat');
    await expect(llm.locator('#llma-announcer')).not.toContainText('Message deleted');
});

test('a message whose chat could not be made stays in the composer, with what was attached', async ({ llm }) => {
    await setScenarios([{ match: 'no chat yet', rounds: [{ text: 'now there is one' }] }]);
    await llm.route('**/API/LLMAssistantCreateThread', route => route.fulfill({ json: { success: false, error: 'NO-CHAT' } }));
    await llm.locator('#llma-attach-file').setInputFiles({ name: 'kept.txt', mimeType: 'text/plain', buffer: Buffer.from('words') });
    await expect(llm.locator('#llma-document-chips .llma-doc-chip')).toHaveCount(1, { timeout: 20_000 });
    await expect(llm.locator('.llma-doc-chip-reading')).toHaveCount(0, { timeout: 20_000 });
    await send(llm, 'no chat yet');
    await expect(llm.locator('.llma-toast', { hasText: 'Failed to create chat' })).toBeVisible();
    await expect(llm.locator('#llma-input')).toHaveValue('no chat yet');
    await expect(llm.locator('#llma-document-chips .llma-doc-chip')).toHaveCount(1);
    await expect(llm.locator('#llma-messages .llma-msg-row')).toHaveCount(0);
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await llm.unroute('**/API/LLMAssistantCreateThread');
    await llm.locator('#llma-input').press('Enter');
    await expect(lastAssistant(llm)).toContainText('now there is one');
    expect((await stored(llm))[0].content).toBe('no chat yet');
});

test('a picture with no words is sent', async ({ llm }) => {
    await setScenarios([{ regex: '.*', rounds: [{ text: 'a small square' }] }]);
    let png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==', 'base64');
    await llm.locator('#llma-attach-file').setInputFiles({ name: 'square.png', mimeType: 'image/png', buffer: png });
    await expect(llm.locator('#llma-attachment-preview img')).toBeVisible();
    await send(llm, '');
    await expect(lastAssistant(llm)).toContainText('a small square');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await expect(llm.locator('#llma-attachment-preview')).toBeHidden();
    let message: any = (await stored(llm))[0];
    expect(message.content).toBe('(image)');
    expect(message.media).toHaveLength(1);
});

test('an edit of a message that is gone leaves no unsaved branch, and the wording goes to the composer', async ({ llm }) => {
    await setScenarios([{ regex: '.*', rounds: [{ text: 'the reply' }] }]);
    await exchange(llm, 'first one', 'the reply');
    await exchange(llm, 'second one', 'the reply');
    let listed = await api(llm, 'LLMAssistantGetThreads');
    let question = userRows(llm).last();
    await question.hover();
    await action(llm, question, 'Edit').click();
    await question.locator('textarea').fill('worded again');
    // Deleted from somewhere else while it was being edited.
    expect(await api(llm, 'LLMAssistantDeleteMessage', { threadId: listed.threads[0].id, messageId: await question.getAttribute('data-msg-id') })).toMatchObject({ success: true });
    await question.locator('textarea').press('Control+Enter');
    await expect(userRows(llm)).toHaveCount(1);
    await expect(replyRows(llm)).toHaveCount(1);
    await expect(llm.locator('#llma-input')).toHaveValue('worded again');
    await expect(llm.locator('.llma-toast', { hasText: 'not found' })).toBeVisible();
    await expect(llm.locator('.llma-msg-error')).toHaveCount(0);
    await expect(llm.locator('.llma-msg-retry')).toHaveCount(0);
    await expectSameTree(llm);
});

test('a refused edit of a message that is still there opens it again with the new wording', async ({ llm }) => {
    await setScenarios([{ regex: '.*', rounds: [{ text: 'the reply' }] }]);
    await llm.routeWebSocket(/LLMAssistantEditMessageWS/, socket => socket.onMessage(() => socket.send(JSON.stringify({ success: false, error: 'EDIT-REFUSED', refused: true }))));
    await reopen(llm);
    await exchange(llm, 'as first worded', 'the reply');
    let question = userRows(llm).first();
    await question.hover();
    await action(llm, question, 'Edit').click();
    await question.locator('textarea').fill('as worded second');
    await question.locator('textarea').press('Control+Enter');
    await expect(question.locator('textarea')).toHaveValue('as worded second');
    await expect(llm.locator('.llma-toast', { hasText: 'EDIT-REFUSED' })).toBeVisible();
    await expect(userRows(llm)).toHaveCount(1);
    await expect(question.locator('textarea')).toBeFocused();
    await expect(llm.locator('.llma-msg-error')).toHaveCount(0);
    await expectSameTree(llm);
    await question.getByRole('button', { name: 'Cancel' }).click();
    await expect(question).toContainText('as first worded');
});

test('a refused regenerate leaves the chat as it is stored', async ({ llm }) => {
    await setScenarios([{ regex: '.*', rounds: [{ text: 'the reply' }] }]);
    await exchange(llm, 'first one', 'the reply');
    await exchange(llm, 'second one', 'the reply');
    let listed = await api(llm, 'LLMAssistantGetThreads');
    // The question under the reply goes from somewhere else, and the reply with it.
    expect(await api(llm, 'LLMAssistantDeleteMessage', { threadId: listed.threads[0].id, messageId: await userRows(llm).last().getAttribute('data-msg-id') })).toMatchObject({ success: true });
    let reply = replyRows(llm).last();
    await reply.hover();
    await action(llm, reply, 'Regenerate').click();
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await expect(userRows(llm)).toHaveCount(1);
    await expect(replyRows(llm)).toHaveCount(1);
    await expect(llm.locator('.llma-toast', { hasText: 'not found' })).toBeVisible();
    await expect(llm.locator('.llma-msg-error')).toHaveCount(0);
    await expect(llm.locator('.llma-msg-retry')).toHaveCount(0);
    await expectSameTree(llm);
});

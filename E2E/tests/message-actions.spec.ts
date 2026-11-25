import { test, expect, send, lastAssistant, setScenarios, mockRequests, api, reopen } from './fixtures';
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
    expect(questions).toHaveLength(1);
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

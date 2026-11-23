import { test as base, expect, setScenarios, api, reopen, enableSessionTools, resetSessionTools } from './fixtures';
import type { Page } from '@playwright/test';

// The chat library in the sidebar: search across messages, pins, import with a preview, and export as a download.

const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await use(llm);
        await resetSessionTools(llm);
    }
});

type Turn = [user: string, reply: string];

/** A chat with these turns, made through the headless route (no socket, no UI), and named `title`. */
async function chat(page: Page, title: string, turns: Turn[]): Promise<string> {
    await setScenarios(turns.map(([user, reply]) => ({ match: user, rounds: [{ text: reply }] })));
    let threadId: string | undefined;
    for (let [user] of turns) {
        let turn = await api(page, 'LLMAssistantRunTurn', threadId ? { message: user, threadId } : { message: user });
        expect(turn.success, JSON.stringify(turn)).toBe(true);
        threadId = turn.threadId;
    }
    expect((await api(page, 'LLMAssistantRenameThread', { threadId, title })).success).toBe(true);
    return threadId!;
}

const rows = (page: Page) => page.locator('#llma-thread-list .llma-thread-item');
const names = async (page: Page) => (await rows(page).locator('.llma-thread-name').allTextContents()).map(t => t.trim());
const search = (page: Page) => page.locator('#llma-thread-search');
const toast = (page: Page) => page.locator('.llma-toast');

async function threadCount(page: Page): Promise<number> {
    return (await api(page, 'LLMAssistantGetThreads')).threads.length;
}

// ---- Search ----

test('search lists title matches, finds message text with the match marked, and a snippet opens the chat at that message', async ({ llm }) => {
    await chat(llm, 'Gardening notes', [['How do I grow tomatoes on a balcony?', 'Use a deep pot and water them daily.']]);
    await chat(llm, 'Tomatoes forever', [['hello there', 'hi back']]);
    await chat(llm, 'Unrelated', [['what is two plus two', 'four']]);
    await reopen(llm);
    await expect(rows(llm)).toHaveCount(3);

    await search(llm).fill('tomatoes');
    // The title match first, then the chat whose message matched, with a snippet under it; the third chat is gone.
    await expect(llm.locator('#llma-thread-list .llma-thread-snippet')).toHaveCount(1);
    expect(await names(llm)).toEqual(['Tomatoes forever', 'Gardening notes']);
    await expect(llm.locator('.llma-search-status')).toHaveText('2 chats');
    let snippet = llm.locator('.llma-thread-snippet');
    await expect(snippet).toContainText('How do I grow tomatoes on a balcony?');
    await expect(snippet.locator('mark.llma-search-hit')).toHaveText('tomatoes');
    // Snippets are not chat rows, so bulk select counts only the chats.
    await expect(rows(llm)).toHaveCount(2);

    await snippet.click();
    await expect(llm.locator('#llma-thread-title')).toHaveText('Gardening notes');
    await expect(llm.locator('.llma-msg-row.user .llma-msg-bubble').first()).toContainText('grow tomatoes');
    await expect(llm.locator('#llma-messages .llma-msg-found')).toHaveCount(1);
    // The results are still there after the chat opened (the list was redrawn with the search on).
    await expect(llm.locator('.llma-thread-snippet')).toHaveCount(1);
    await expect(rows(llm).filter({ hasText: 'Gardening notes' })).toHaveClass(/active/);

    // The reply's text is found too, and the snippet says who said it.
    await search(llm).fill('deep pot');
    await expect(llm.locator('.llma-thread-snippet-who')).toHaveText('Reply');
    await expect(llm.locator('.llma-thread-snippet mark.llma-search-hit')).toHaveText(['deep', 'pot']);

    await search(llm).fill('no-such-words-anywhere');
    await expect(llm.locator('.llma-search-status')).toHaveText('No chats match.');
    await expect(rows(llm)).toHaveCount(0);

    await search(llm).fill('x');
    await expect(llm.locator('.llma-search-status')).toContainText('at least 2 characters');

    // Escape clears the search and the whole list is back.
    await search(llm).press('Escape');
    await expect(search(llm)).toHaveValue('');
    await expect(rows(llm)).toHaveCount(3);
    await expect(llm.locator('.llma-search-status')).toHaveCount(0);
});

test('a snippet from another branch switches to that branch', async ({ llm }) => {
    let id = await chat(llm, 'Branchy', [['root question', 'first reply']]);
    let first = (await api(llm, 'LLMAssistantGetThread', { threadId: id })).thread;
    first = typeof first === 'string' ? JSON.parse(first) : first;
    await api(llm, 'LLMAssistantSetActiveLeaf', { threadId: id, messageId: first.messages[0].id });
    await setScenarios([{ match: 'side question', rounds: [{ text: 'side branch reply' }] }]);
    expect((await api(llm, 'LLMAssistantRunTurn', { message: 'side question', threadId: id })).success).toBe(true);
    // Back to the first branch, so "side branch reply" is not on the one showing.
    await api(llm, 'LLMAssistantSetActiveLeaf', { threadId: id, messageId: first.messages[1].id });
    await reopen(llm);
    await search(llm).fill('side branch');
    let snippet = llm.locator('.llma-thread-snippet').filter({ hasText: 'side branch reply' });
    await expect(snippet).toHaveCount(1);
    await snippet.click();
    await expect(llm.locator('.llma-msg-row.assistant .llma-msg-bubble').last()).toContainText('side branch reply');
    await expect(llm.locator('#llma-messages .llma-msg-found')).toHaveCount(1);
});

test('while the message search loads the titles are filtered at once, and a stale reply never replaces a newer one', async ({ llm }) => {
    await chat(llm, 'alphaword in the title', [['unrelated one', 'unrelated reply']]);
    await chat(llm, 'Body one', [['talking about alphaword here', 'yes alphaword']]);
    await chat(llm, 'Other', [['mentioning bravoword now', 'ok']]);
    await reopen(llm);
    await expect(rows(llm)).toHaveCount(3);

    // The first search is held back; the second is answered at once.
    await llm.route('**/API/LLMAssistantSearchThreads', async route => {
        let query = route.request().postDataJSON().query;
        if (query === 'alphaword') {
            await new Promise(resolve => setTimeout(resolve, 2500));
        }
        await route.continue();
    });
    let slow = llm.waitForResponse(r => r.url().includes('LLMAssistantSearchThreads') && (r.request().postData() ?? '').includes('alphaword'), { timeout: 15_000 });
    await search(llm).fill('alphaword');
    // At once: only the title match, and a note that messages are being searched.
    expect(await names(llm)).toEqual(['alphaword in the title']);
    await expect(llm.locator('.llma-search-status')).toHaveText('Searching messages…');
    await expect(llm.locator('.llma-thread-snippet')).toHaveCount(0);

    // Before the slow reply arrives the query changes.
    await search(llm).fill('bravoword');
    await expect(llm.locator('.llma-thread-snippet mark.llma-search-hit')).toHaveText('bravoword');
    expect(await names(llm)).toEqual(['Other']);
    // The slow reply lands now; it must not replace what the box asks for.
    await slow;
    await llm.waitForTimeout(400);
    expect(await names(llm)).toEqual(['Other']);
    await expect(llm.locator('.llma-thread-snippet')).toHaveCount(1);
    await expect(llm.locator('.llma-search-status')).toHaveText('1 chat');

    // And the other way round: back to the first query once the route answers fast, it is the first query's results that show.
    await llm.unroute('**/API/LLMAssistantSearchThreads');
    await search(llm).fill('alphaword');
    await expect(llm.locator('.llma-thread-snippet')).toHaveCount(2);
    expect(await names(llm)).toEqual(['alphaword in the title', 'Body one']);
});

test('when the message search fails the title matches stay with a note', async ({ llm }) => {
    await chat(llm, 'Findable title', [['hello', 'hi']]);
    await chat(llm, 'Other', [['hello again', 'hi again']]);
    await reopen(llm);
    await llm.route('**/API/LLMAssistantSearchThreads', route => route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"boom"}' }));
    await search(llm).fill('findable');
    await expect(llm.locator('.llma-search-status')).toContainText('Message search is unavailable');
    expect(await names(llm)).toEqual(['Findable title']);
    await llm.unroute('**/API/LLMAssistantSearchThreads');
});

test('more results load a page at a time', async ({ llm }) => {
    for (let i = 0; i < 3; i++) {
        await chat(llm, `Page chat ${i}`, [[`pageword number ${i}`, 'ok']]);
    }
    await reopen(llm);
    await llm.evaluate(() => { (0, eval)('llmaLibrary').pageSize = 2; });
    await search(llm).fill('pageword');
    await expect(rows(llm)).toHaveCount(2);
    await llm.locator('.llma-search-more').click();
    await expect(rows(llm)).toHaveCount(3);
    await expect(llm.locator('.llma-search-more')).toHaveCount(0);
    await expect(llm.locator('.llma-search-status')).toHaveText('3 chats');
});

// ---- Pin ----

test('a pinned chat is listed first under Pinned, stays there after a reload, and goes back when unpinned', async ({ llm }) => {
    await chat(llm, 'Alpha chat', [['alpha message', 'alpha reply']]);
    await chat(llm, 'Bravo chat', [['bravo message', 'bravo reply']]);
    await chat(llm, 'Charlie chat', [['charlie message', 'charlie reply']]);
    await reopen(llm);
    expect(await names(llm)).toEqual(['Charlie chat', 'Bravo chat', 'Alpha chat']);
    await expect(llm.locator('.llma-thread-group-label')).toHaveText(['Today']);

    let alpha = rows(llm).filter({ hasText: 'Alpha chat' });
    await alpha.hover();
    await alpha.locator('.llma-thread-pin').click();
    expect(await names(llm)).toEqual(['Alpha chat', 'Charlie chat', 'Bravo chat']);
    await expect(llm.locator('.llma-thread-group-label')).toHaveText(['Pinned', 'Today']);
    await expect(rows(llm).first().locator('.llma-thread-pin')).toHaveAttribute('aria-pressed', 'true');
    // The star is shown without hovering, since it is the only sign of the pin.
    await expect(rows(llm).first().locator('.llma-thread-pin')).toBeVisible();
    // The server has it, and it did not count as activity.
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetThreads')).threads[0].pinned).toBe(true);

    await reopen(llm);
    expect(await names(llm)).toEqual(['Alpha chat', 'Charlie chat', 'Bravo chat']);
    await expect(llm.locator('.llma-thread-group-label')).toHaveText(['Pinned', 'Today']);

    // Pinning another puts it after the first (newest first among pinned).
    let bravo = rows(llm).filter({ hasText: 'Bravo chat' });
    await bravo.hover();
    await bravo.locator('.llma-thread-pin').click();
    expect(await names(llm)).toEqual(['Bravo chat', 'Alpha chat', 'Charlie chat']);

    // Grouping by assistant keeps pinned chats on top.
    await llm.locator('#llma-thread-group-toggle').click();
    expect((await names(llm)).slice(0, 2)).toEqual(['Bravo chat', 'Alpha chat']);
    await expect(llm.locator('.llma-thread-group-label').first()).toHaveText('Pinned');
    await llm.locator('#llma-thread-group-toggle').click();

    // Unpin both: back in date order, no Pinned group.
    for (let title of ['Bravo chat', 'Alpha chat']) {
        let row = rows(llm).filter({ hasText: title });
        await row.hover();
        await row.locator('.llma-thread-pin').click();
    }
    await expect(llm.locator('.llma-thread-group-label')).toHaveText(['Today']);
    expect(await names(llm)).toEqual(['Charlie chat', 'Bravo chat', 'Alpha chat']);
    await reopen(llm);
    expect(await names(llm)).toEqual(['Charlie chat', 'Bravo chat', 'Alpha chat']);
});

test('a pin is kept when the pinned chat is renamed, branched or continued, and the pinned chat can still be opened and deleted', async ({ llm }) => {
    let id = await chat(llm, 'Keep me', [['keep question', 'keep answer']]);
    await chat(llm, 'Newer chat', [['newer question', 'newer answer']]);
    await reopen(llm);
    let keep = rows(llm).filter({ hasText: 'Keep me' });
    await keep.hover();
    await keep.locator('.llma-thread-pin').click();
    await keep.click();
    await expect(llm.locator('#llma-thread-title')).toHaveText('Keep me');
    await setScenarios([{ match: 'continue it', rounds: [{ text: 'continued' }] }]);
    await llm.locator('#llma-input').fill('continue it');
    await llm.locator('#llma-input').press('Enter');
    await expect(llm.locator('.llma-msg-row.assistant .llma-msg-bubble').last()).toContainText('continued');
    expect((await api(llm, 'LLMAssistantGetThreads')).threads[0]).toMatchObject({ id, pinned: true });
    await reopen(llm);
    expect(await names(llm)).toEqual(['Keep me', 'Newer chat']);
    llm.once('dialog', dialog => dialog.accept());
    let again = rows(llm).filter({ hasText: 'Keep me' });
    await again.hover();
    await again.locator('.llma-thread-del').click();
    await expect(rows(llm)).toHaveCount(1);
    await expect(llm.locator('.llma-thread-group-label')).toHaveText(['Today']);
});

test('a pinned chat keeps the sessions it started under it, and a session has no pin of its own', async ({ llm }) => {
    await enableSessionTools(llm);
    let parent = (await api(llm, 'LLMAssistantCreateThread', { assistantId: 'default', title: 'Parent chat' })).thread.id;
    await chat(llm, 'Newest chat', [['newest question', 'newest answer']]);
    await setScenarios([{ match: 'child-task', rounds: [{ text: 'child report' }] }]);
    let started = await api(llm, 'LLMAssistantExecuteTool', { toolId: 'session_start', threadId: parent, arguments: { title: 'Child session', prompt: 'child-task', model: 'mock-model' } });
    expect(started.success, JSON.stringify(started)).toBe(true);
    let child = started.result.id;
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetThread', { threadId: child })).thread?.sessionStatus, { timeout: 30_000 }).toBe('done');
    await reopen(llm);
    expect(await names(llm)).toEqual(['Parent chat', 'Child session', 'Newest chat']);
    await expect(rows(llm).filter({ hasText: 'Child session' }).locator('.llma-thread-pin')).toHaveCount(0);

    let row = rows(llm).filter({ hasText: 'Parent chat' });
    await row.hover();
    await row.locator('.llma-thread-pin').click();
    await expect(llm.locator('.llma-thread-group-label')).toHaveText(['Pinned', 'Today']);
    expect(await names(llm)).toEqual(['Parent chat', 'Child session', 'Newest chat']);
    await expect(rows(llm).filter({ hasText: 'Child session' })).toHaveClass(/llma-thread-child/);
    await reopen(llm);
    expect(await names(llm)).toEqual(['Parent chat', 'Child session', 'Newest chat']);
    await expect(llm.locator('.llma-thread-group-label')).toHaveText(['Pinned', 'Today']);
    // The server: the child is listed with its parent and cannot be pinned.
    let refused = await api(llm, 'LLMAssistantPinThread', { threadId: child, pinned: true });
    expect(refused.success).toBe(false);
    expect(refused.error).toMatch(/session cannot be pinned/);
    let listed = (await api(llm, 'LLMAssistantGetThreads')).threads as any[];
    expect(listed.find(t => t.id === child).pinned).toBeUndefined();
    expect(listed.find(t => t.id === child).parentThreadId).toBe(parent);
});

// ---- Export ----

async function openChat(page: Page, title: string) {
    await rows(page).filter({ hasText: title }).click();
    await expect(page.locator('#llma-thread-title')).toHaveText(title);
}

test('export downloads the chat as JSON (importable again), Markdown and plain text', async ({ llm }) => {
    await chat(llm, 'Export me', [['first export question', 'first export answer'], ['second export question', 'second export answer']]);
    await reopen(llm);
    await openChat(llm, 'Export me');

    let download = async (item: string) => {
        await llm.locator('#llma-export-btn').click();
        let [file] = await Promise.all([llm.waitForEvent('download'), llm.locator(item).click()]);
        let stream = await file.createReadStream();
        let chunks: Buffer[] = [];
        for await (let chunk of stream) {
            chunks.push(chunk as Buffer);
        }
        return { name: file.suggestedFilename(), text: Buffer.concat(chunks).toString('utf8') };
    };

    let json = await download('#llma-export-json');
    expect(json.name).toBe('Export_me.json');
    let parsed = JSON.parse(json.text);
    expect(parsed).toMatchObject({ format: 'llmassistant-thread', version: 1, title: 'Export me' });
    expect(parsed.messages.map((m: any) => m.content)).toEqual(['first export question', 'first export answer', 'second export question', 'second export answer']);
    await expect(toast(llm)).toContainText('JSON');

    let md = await download('#llma-export-md');
    expect(md.name).toBe('Export_me.md');
    expect(md.text).toContain('# Export me');
    expect(md.text).toMatch(/## You\s+first export question\s+## Assistant\s+first export answer/);

    let txt = await download('#llma-export-txt');
    expect(txt.name).toMatch(/^export_me_\d{4}-\d{2}-\d{2}\.txt$/);
    expect(txt.text).toContain('second export answer');

    // The JSON file imports again as it is.
    let before = await threadCount(llm);
    await llm.locator('#llma-import-file').setInputFiles({ name: json.name, mimeType: 'application/json', buffer: Buffer.from(json.text) });
    await expect(llm.locator('#llma-import-overlay')).toBeVisible();
    await expect(llm.locator('.llma-import-title')).toHaveText('Export me');
    await expect(llm.locator('.llma-import-facts')).toContainText('4 messages');
    await llm.locator('#llma-import-confirm').click();
    await expect(llm.locator('#llma-import-overlay')).toBeHidden();
    expect(await threadCount(llm)).toBe(before + 1);
});

test('export with nothing to export says so and downloads nothing', async ({ llm }) => {
    await llm.evaluate(() => (0, eval)('llmaExportThread')('json'));
    await expect(toast(llm)).toContainText('No messages to export');
});

// ---- Import ----

test('import shows a preview first, cancel saves nothing, and Import chat adds a new chat and opens it', async ({ llm }) => {
    let source = await chat(llm, 'Source chat', [['kiwi question', 'kiwi answer'], ['kiwi again', 'kiwi again answer']]);
    let file = (await api(llm, 'LLMAssistantExportThread', { threadId: source })).content as string;
    await reopen(llm);
    let before = await threadCount(llm);
    let choose = (content: string, name = 'chat.json') => llm.locator('#llma-import-file').setInputFiles({ name, mimeType: 'application/json', buffer: Buffer.from(content) });

    await choose(file);
    let dialog = llm.locator('#llma-import-overlay');
    await expect(dialog).toBeVisible();
    await expect(llm.locator('.llma-import-title')).toHaveText('Source chat');
    await expect(llm.locator('.llma-import-facts')).toContainText('4 messages');
    await expect(llm.locator('.llma-import-facts')).toContainText('chat.json');
    await expect(llm.locator('.llma-import-sample-row')).toHaveCount(3);
    await expect(llm.locator('.llma-import-sample')).toContainText('kiwi question');
    await expect(llm.locator('.llma-import-warnings')).toHaveCount(0);
    // Nothing was saved by the preview, and Cancel (and Escape, and the backdrop) close it.
    expect(await threadCount(llm)).toBe(before);
    await llm.locator('#llma-import-cancel').click();
    await expect(dialog).toBeHidden();
    await choose(file);
    await expect(dialog).toBeVisible();
    await llm.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    expect(await threadCount(llm)).toBe(before);

    await choose(file);
    await llm.locator('#llma-import-confirm').click();
    await expect(dialog).toBeHidden();
    await expect(toast(llm)).toContainText('Chat imported');
    await expect(llm.locator('#llma-thread-title')).toHaveText('Source chat');
    await expect(llm.locator('.llma-msg-row.user .llma-msg-bubble')).toHaveText(['kiwi question', 'kiwi again']);
    await expect(llm.locator('.llma-msg-row.assistant .llma-msg-bubble')).toHaveText(['kiwi answer', 'kiwi again answer']);
    expect(await threadCount(llm)).toBe(before + 1);
    await expect(rows(llm).filter({ hasText: 'Source chat' })).toHaveCount(2);
    let ids = (await api(llm, 'LLMAssistantGetThreads')).threads.map((t: any) => t.id);
    expect(new Set(ids).size).toBe(ids.length);
    // It can be carried on, like any chat.
    await setScenarios([{ match: 'kiwi more', rounds: [{ text: 'kiwi more answer' }] }]);
    await llm.locator('#llma-input').fill('kiwi more');
    await llm.locator('#llma-input').press('Enter');
    await expect(llm.locator('.llma-msg-row.assistant .llma-msg-bubble').last()).toContainText('kiwi more answer');
    // The pin is not carried over from a file, and the imported chat is searchable.
    await reopen(llm);
    await search(llm).fill('kiwi again');
    await expect(rows(llm)).toHaveCount(2);
});

test('import shows what is left out in the preview, and refuses a bad file with a message and no dialog', async ({ llm }) => {
    await reopen(llm);
    let before = await threadCount(llm);
    let choose = (content: string, name = 'chat.json') => llm.locator('#llma-import-file').setInputFiles({ name, mimeType: 'application/json', buffer: Buffer.from(content) });
    let dialog = llm.locator('#llma-import-overlay');

    await choose(JSON.stringify({
        title: 'With extras',
        assistantId: 'gone-assistant',
        messages: [
            { id: 'a', parentId: null, role: 'user', content: 'with an image', media: [{ url: '/x.png' }] },
            { id: 'b', parentId: 'a', role: 'tool', content: 'tool text' },
            { id: 'c', parentId: 'b', role: 'assistant', content: 'plain reply' }
        ]
    }));
    await expect(dialog).toBeVisible();
    await expect(llm.locator('.llma-import-warnings li')).toHaveCount(3);
    await expect(llm.locator('.llma-import-warnings')).toContainText('1 message left out');
    await expect(llm.locator('.llma-import-warnings')).toContainText('Attachments and tool calls are not imported');
    await expect(llm.locator('.llma-import-warnings')).toContainText('not available here');
    await expect(llm.locator('.llma-import-facts')).toContainText('2 messages');
    await llm.locator('#llma-import-cancel').click();

    for (let [content, message] of [
        ['this is not json', /not valid JSON/],
        ['{"messages":[{"id":"a","parentId":"a","role":"user","content":"x"}]}', /loop/],
        ['{"messages":[{"id":"a","role":"user","content":"x"},{"id":"a","role":"user","content":"y"}]}', /repeats the id/],
        ['{}', /no messages list/]
    ] as [string, RegExp][]) {
        await choose(content);
        await expect(toast(llm)).toHaveText(message);
        await expect(dialog).toBeHidden();
    }
    // A file over the size a chat can be is refused without being sent.
    await llm.locator('#llma-import-file').setInputFiles({ name: 'big.json', mimeType: 'application/json', buffer: Buffer.alloc(4 * 1024 * 1024 + 10, 120) });
    await expect(toast(llm)).toContainText('too large');
    await expect(dialog).toBeHidden();
    expect(await threadCount(llm)).toBe(before);
});

test('importing works with no chats at all, from the empty list', async ({ llm }) => {
    await reopen(llm);
    await expect(llm.locator('.llma-threads-empty')).toBeVisible();
    await expect(llm.locator('#llma-thread-import-btn')).toBeVisible();
    await llm.locator('#llma-import-file').setInputFiles({ name: 'first.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify({ title: 'First ever', messages: [{ role: 'user', content: 'hello import' }, { role: 'assistant', content: 'hello back' }] })) });
    await llm.locator('#llma-import-confirm').click();
    await expect(llm.locator('#llma-thread-title')).toHaveText('First ever');
    await expect(rows(llm)).toHaveCount(1);
});

// ---- Review follow-ups ----

test('a pin made while the title is being generated is kept, and so is a rename', async ({ llm }) => {
    // The title request (its last message starts with "User:") answers after a delay, long enough to pin and rename in the meantime.
    await setScenarios([
        { regex: '^User:', rounds: [{ text: 'Generated title', delayMs: 4000 }] },
        { match: 'pinrace question', rounds: [{ text: 'pinrace answer' }] },
        { match: 'renamerace question', rounds: [{ text: 'renamerace answer' }] }
    ]);
    await llm.locator('#llma-input').fill('pinrace question');
    await llm.locator('#llma-input').press('Enter');
    await expect(llm.locator('.llma-msg-row.assistant .llma-msg-bubble').last()).toContainText('pinrace answer');
    let id = (await api(llm, 'LLMAssistantGetThreads')).threads[0].id;
    let pinned = await api(llm, 'LLMAssistantPinThread', { threadId: id, pinned: true });
    expect(pinned.success).toBe(true);
    // The generated title arrives afterwards and does not undo the pin.
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetThreads')).threads.find((t: any) => t.id === id)?.title, { timeout: 20_000 }).toBe('Generated title');
    let after = (await api(llm, 'LLMAssistantGetThreads')).threads.find((t: any) => t.id === id);
    expect(after.pinned).toBe(true);
    let thread = (await api(llm, 'LLMAssistantGetThread', { threadId: id })).thread;
    thread = typeof thread === 'string' ? JSON.parse(thread) : thread;
    expect(thread.pinned).toBe(true);
    expect(thread.messages.map((m: any) => m.content)).toEqual(['pinrace question', 'pinrace answer']);

    // A rename made while the title is being generated wins over the generated title.
    await llm.locator('#llma-new-thread-btn').click();
    await llm.locator('#llma-input').fill('renamerace question');
    await llm.locator('#llma-input').press('Enter');
    await expect(llm.locator('.llma-msg-row.assistant .llma-msg-bubble').last()).toContainText('renamerace answer');
    let second = (await api(llm, 'LLMAssistantGetThreads')).threads.find((t: any) => t.id !== id).id;
    expect((await api(llm, 'LLMAssistantRenameThread', { threadId: second, title: 'My own name' })).success).toBe(true);
    await llm.waitForTimeout(6000);
    expect((await api(llm, 'LLMAssistantGetThreads')).threads.find((t: any) => t.id === second).title).toBe('My own name');
    // The page was not told of the title that was refused: only the first chat shows the generated one, in the list and in the header.
    expect(await names(llm)).toEqual(expect.arrayContaining(['Generated title']));
    expect((await names(llm)).filter(n => n === 'Generated title')).toHaveLength(1);
    await expect(llm.locator('#llma-thread-title')).not.toHaveText('Generated title');
});

test('the search on screen follows new messages, deletes and renames without being retyped', async ({ llm }) => {
    let a = await chat(llm, 'Chat A', [['zebraword in a', 'reply a']]);
    await chat(llm, 'Chat B', [['zebraword in b', 'reply b']]);
    await reopen(llm);
    await search(llm).fill('zebraword');
    await expect(rows(llm)).toHaveCount(2);
    await expect(llm.locator('.llma-search-status')).toHaveText('2 chats');

    // A message sent in a chat is found at once by the search that is on.
    await rows(llm).filter({ hasText: 'Chat A' }).click();
    await expect(llm.locator('#llma-thread-title')).toHaveText('Chat A');
    await search(llm).fill('quokkaword');
    await expect(llm.locator('.llma-search-status')).toHaveText('No chats match.');
    await setScenarios([{ match: 'quokkaword please', rounds: [{ text: 'quokka reply' }] }]);
    await llm.locator('#llma-input').fill('quokkaword please');
    await llm.locator('#llma-input').press('Enter');
    await expect(llm.locator('.llma-msg-row.assistant .llma-msg-bubble').last()).toContainText('quokka reply');
    await expect(llm.locator('.llma-thread-snippet mark.llma-search-hit').first()).toHaveText('quokkaword');
    expect(await names(llm)).toEqual(['Chat A']);

    // A delete is gone from the results and the count follows.
    await search(llm).fill('zebraword');
    await expect(rows(llm)).toHaveCount(2);
    llm.once('dialog', dialog => dialog.accept());
    let doomed = rows(llm).filter({ hasText: 'Chat B' });
    await doomed.hover();
    await doomed.locator('.llma-thread-del').click();
    await expect(rows(llm)).toHaveCount(1);
    await expect(llm.locator('.llma-search-status')).toHaveText('1 chat');
    // A rename shows in the results.
    expect((await api(llm, 'LLMAssistantRenameThread', { threadId: a, title: 'Renamed zebraword' })).success).toBe(true);
    await llm.evaluate(() => (0, eval)('llmaLoadThreads')());
    await expect(rows(llm).locator('.llma-thread-name')).toHaveText(['Renamed zebraword']);
});

test('quick pin clicks reach the server in order and a refused pin shows what the server has', async ({ llm }) => {
    let id = await chat(llm, 'Clicky', [['click question', 'click answer']]);
    await chat(llm, 'Other', [['other question', 'other answer']]);
    await reopen(llm);
    let row = rows(llm).filter({ hasText: 'Clicky' });
    await row.hover();
    // Three clicks in a row end pinned (pin, unpin, pin), on the page and on the server.
    let calls: boolean[] = [];
    await llm.route('**/API/LLMAssistantPinThread', async route => {
        calls.push(route.request().postDataJSON().pinned);
        await new Promise(resolve => setTimeout(resolve, 300));
        await route.continue();
    });
    // The row moves between groups at each click, so the clicks are sent to the button directly.
    for (let i = 0; i < 3; i++) {
        await llm.locator('#llma-thread-list .llma-thread-pin[data-id="' + id + '"]').dispatchEvent('click');
    }
    await expect.poll(() => calls.length).toBe(3);
    expect(calls).toEqual([true, false, true]);
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetThreads')).threads.find((t: any) => t.id === id).pinned).toBe(true);
    await expect(llm.locator('.llma-thread-group-label').first()).toHaveText('Pinned');
    await llm.unroute('**/API/LLMAssistantPinThread');

    // A refusal puts the list back as the server has it: still pinned.
    await llm.route('**/API/LLMAssistantPinThread', route => route.fulfill({ status: 200, contentType: 'application/json', body: '{"success":false,"error":"nope"}' }));
    await llm.locator('#llma-thread-list .llma-thread-pin[data-id="' + id + '"]').dispatchEvent('click');
    await expect(toast(llm)).toContainText('nope');
    await expect(llm.locator('.llma-thread-group-label').first()).toHaveText('Pinned');
    await llm.unroute('**/API/LLMAssistantPinThread');
});

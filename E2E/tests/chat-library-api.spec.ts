import { test, expect, setScenarios, api } from './fixtures';
import type { Page } from '@playwright/test';

// The chat library over the API alone: no UI is touched. Search, pin, import and export, each with its validation.

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
    let renamed = await api(page, 'LLMAssistantRenameThread', { threadId, title });
    expect(renamed.success).toBe(true);
    return threadId!;
}

async function getThread(page: Page, threadId: string): Promise<any> {
    let thread = (await api(page, 'LLMAssistantGetThread', { threadId })).thread;
    return typeof thread === 'string' ? JSON.parse(thread) : thread;
}

async function row(page: Page, threadId: string): Promise<any> {
    return (await api(page, 'LLMAssistantGetThreads')).threads.find((t: any) => t.id === threadId);
}

const fail = (reply: any, text: RegExp | string) => {
    expect(reply.success, JSON.stringify(reply)).toBe(false);
    expect(reply.error).toMatch(text);
};

// ---- Search ----

test('search finds a title and message text, ranks the title first, and returns snippets with the match marked', async ({ llm }) => {
    let body = await chat(llm, 'Gardening notes', [['How do I grow tomatoes on a balcony?', 'Use a deep pot and water them daily.']]);
    let title = await chat(llm, 'Tomatoes forever', [['hello there', 'hi back']]);
    await chat(llm, 'Unrelated', [['what is two plus two', 'four']]);

    let found = await api(llm, 'LLMAssistantSearchThreads', { query: 'tomatoes' });
    expect(found).toMatchObject({ success: true, total: 2, offset: 0, hasMore: false, partial: false });
    expect(found.results.map((r: any) => r.threadId)).toEqual([title, body]);
    expect(found.results[0]).toMatchObject({ title: 'Tomatoes forever', titleMatch: true, snippets: [] });
    let hit = found.results[1];
    expect(hit).toMatchObject({ title: 'Gardening notes', titleMatch: false, matchedMessages: 1 });
    expect(hit.snippets).toHaveLength(1);
    let snippet = hit.snippets[0];
    expect(snippet.role).toBe('user');
    let marked = snippet.ranges.map(([start, length]: number[]) => snippet.text.slice(start, start + length));
    expect(marked).toEqual(['tomatoes']);
    let thread = await getThread(llm, body);
    expect(thread.messages.map((m: any) => m.id)).toContain(snippet.messageId);

    // The reply's text is found too, case does not matter, and every word must be there.
    let reply = await api(llm, 'LLMAssistantSearchThreads', { query: 'DEEP pot' });
    expect(reply.results.map((r: any) => [r.threadId, r.snippets[0].role])).toEqual([[body, 'assistant']]);
    expect((await api(llm, 'LLMAssistantSearchThreads', { query: 'deep submarine' })).results).toEqual([]);
});

test('search reads only message text: not tool calls, attachments or other chats edits', async ({ llm }) => {
    let id = await chat(llm, 'Plain', [['remember zebra-token please', 'noted']]);
    // A deleted message is gone from the search at once, an edited one is found by its new text.
    let thread = await getThread(llm, id);
    let replyId = thread.messages[1].id;
    let edited = await api(llm, 'LLMAssistantEditMessage', { threadId: id, messageId: replyId, content: 'now says quokka-token' });
    expect(edited.success).toBe(true);
    expect((await api(llm, 'LLMAssistantSearchThreads', { query: 'quokka-token' })).results.map((r: any) => r.threadId)).toEqual([id]);
    expect((await api(llm, 'LLMAssistantSearchThreads', { query: 'noted' })).results).toEqual([]);
    let deleted = await api(llm, 'LLMAssistantDeleteMessage', { threadId: id, messageId: replyId });
    expect(deleted.success).toBe(true);
    expect((await api(llm, 'LLMAssistantSearchThreads', { query: 'quokka-token' })).results).toEqual([]);
    // A deleted chat is gone too.
    expect((await api(llm, 'LLMAssistantSearchThreads', { query: 'zebra-token' })).results).toHaveLength(1);
    await api(llm, 'LLMAssistantDeleteThread', { threadId: id });
    expect((await api(llm, 'LLMAssistantSearchThreads', { query: 'zebra-token' })).results).toEqual([]);
});

test('search is paged, bounded and validated', async ({ llm }) => {
    let ids: string[] = [];
    for (let i = 0; i < 5; i++) {
        ids.push(await chat(llm, `Chat ${i}`, [[`common-word number ${i}`, `reply ${i}`]]));
    }
    let first = await api(llm, 'LLMAssistantSearchThreads', { query: 'common-word', limit: 2 });
    expect(first).toMatchObject({ success: true, total: 5, offset: 0, limit: 2, hasMore: true });
    expect(first.results).toHaveLength(2);
    let second = await api(llm, 'LLMAssistantSearchThreads', { query: 'common-word', limit: 2, offset: 2 });
    let last = await api(llm, 'LLMAssistantSearchThreads', { query: 'common-word', limit: 2, offset: 4 });
    expect(last).toMatchObject({ hasMore: false });
    let seen = [...first.results, ...second.results, ...last.results].map((r: any) => r.threadId);
    expect(new Set(seen).size).toBe(5);
    expect([...seen].sort()).toEqual([...ids].sort());
    expect((await api(llm, 'LLMAssistantSearchThreads', { query: 'common-word', offset: 100 })).results).toEqual([]);

    fail(await api(llm, 'LLMAssistantSearchThreads', {}), /query is required/);
    fail(await api(llm, 'LLMAssistantSearchThreads', { query: '   ' }), /query is required/);
    fail(await api(llm, 'LLMAssistantSearchThreads', { query: 'a' }), /at least 2/);
    fail(await api(llm, 'LLMAssistantSearchThreads', { query: 5 }), /query is required/);
    fail(await api(llm, 'LLMAssistantSearchThreads', { query: 'ok', limit: 0 }), /limit must be/);
    fail(await api(llm, 'LLMAssistantSearchThreads', { query: 'ok', limit: 51 }), /limit must be/);
    fail(await api(llm, 'LLMAssistantSearchThreads', { query: 'ok', limit: 'ten' }), /limit must be/);
    fail(await api(llm, 'LLMAssistantSearchThreads', { query: 'ok', limit: 1.5 }), /limit must be/);
    fail(await api(llm, 'LLMAssistantSearchThreads', { query: 'ok', offset: -1 }), /offset must be/);
    fail(await api(llm, 'LLMAssistantSearchThreads', { query: 'ok', mode: 'bogus' }), /mode must be/);
    // A very long query is cut, not refused, and finds nothing.
    let long = await api(llm, 'LLMAssistantSearchThreads', { query: 'x'.repeat(20_000) });
    expect(long).toMatchObject({ success: true, results: [] });
    // The mode filter: these are all ordinary chats.
    expect((await api(llm, 'LLMAssistantSearchThreads', { query: 'common-word', mode: 'chat' })).total).toBe(5);
    expect((await api(llm, 'LLMAssistantSearchThreads', { query: 'common-word', mode: 'code' })).total).toBe(0);
});

test('search treats the query as plain text and finds text on a branch that is not showing', async ({ llm }) => {
    let id = await chat(llm, 'Branches', [['first question [a-z]+ (x)', 'first answer']]);
    expect((await api(llm, 'LLMAssistantSearchThreads', { query: '[a-z]+' })).results).toHaveLength(1);
    expect((await api(llm, 'LLMAssistantSearchThreads', { query: '.*' })).results).toEqual([]);
    expect((await api(llm, 'LLMAssistantSearchThreads', { query: '(x)' })).results).toHaveLength(1);
    // A new branch from the first message: edit it into a sibling, then move back to the old branch.
    let thread = await getThread(llm, id);
    let firstUser = thread.messages[0];
    await setScenarios([{ match: 'rewritten question', rounds: [{ text: 'second answer' }] }]);
    let leafBefore = thread.activeLeafId;
    let moved = await api(llm, 'LLMAssistantSetActiveLeaf', { threadId: id, messageId: firstUser.id });
    expect(moved.success).toBe(true);
    let turn = await api(llm, 'LLMAssistantRunTurn', { message: 'rewritten question', threadId: id });
    expect(turn.success, JSON.stringify(turn)).toBe(true);
    expect((await api(llm, 'LLMAssistantSetActiveLeaf', { threadId: id, messageId: leafBefore })).success).toBe(true);
    let found = await api(llm, 'LLMAssistantSearchThreads', { query: 'rewritten question' });
    expect(found.results.map((r: any) => r.threadId)).toEqual([id]);
    let nodes = (await getThread(llm, id)).messages.map((m: any) => m.id);
    expect(nodes).toContain(found.results[0].snippets[0].messageId);
});

// ---- Pin ----

test('pinning is validated, persisted per chat, listed first, and is not activity', async ({ llm }) => {
    let a = await chat(llm, 'Alpha chat', [['alpha message', 'alpha reply']]);
    let b = await chat(llm, 'Bravo chat', [['bravo message', 'bravo reply']]);
    let c = await chat(llm, 'Charlie chat', [['charlie message', 'charlie reply']]);
    let order = async () => (await api(llm, 'LLMAssistantGetThreads')).threads.map((t: any) => t.id);
    expect(await order()).toEqual([c, b, a]);
    let before = await row(llm, a);
    expect(before.pinned).toBeUndefined();

    let pinned = await api(llm, 'LLMAssistantPinThread', { threadId: a, pinned: true });
    expect(pinned).toEqual({ success: true, threadId: a, pinned: true });
    expect(await order()).toEqual([a, c, b]);
    let after = await row(llm, a);
    expect(after.pinned).toBe(true);
    // Pinning does not change the chat's date, its title or its messages.
    expect(after.updatedAt).toBe(before.updatedAt);
    expect(after.title).toBe('Alpha chat');
    let thread = await getThread(llm, a);
    expect(thread.pinned).toBe(true);
    expect(thread.messages).toHaveLength(2);

    // Pinning twice is fine; a second pinned chat is listed after the first by recency; unpinning puts a chat back where its date puts it.
    expect((await api(llm, 'LLMAssistantPinThread', { threadId: a, pinned: true })).success).toBe(true);
    expect((await api(llm, 'LLMAssistantPinThread', { threadId: b, pinned: true })).success).toBe(true);
    expect(await order()).toEqual([b, a, c]);
    expect((await api(llm, 'LLMAssistantPinThread', { threadId: b, pinned: false })).pinned).toBe(false);
    expect((await api(llm, 'LLMAssistantPinThread', { threadId: a, pinned: false })).success).toBe(true);
    expect(await order()).toEqual([c, b, a]);
    expect((await row(llm, a)).pinned).toBeUndefined();
    expect((await getThread(llm, a)).pinned).toBeUndefined();

    // A pinned chat that gets a new message is still pinned, and the pin survives the other writes (rename, edit).
    expect((await api(llm, 'LLMAssistantPinThread', { threadId: c, pinned: true })).success).toBe(true);
    await setScenarios([{ match: 'more', rounds: [{ text: 'more reply' }] }]);
    expect((await api(llm, 'LLMAssistantRunTurn', { message: 'more please', threadId: c })).success).toBe(true);
    expect((await api(llm, 'LLMAssistantRenameThread', { threadId: c, title: 'Charlie renamed' })).success).toBe(true);
    expect(await row(llm, c)).toMatchObject({ pinned: true, title: 'Charlie renamed' });
    expect((await order())[0]).toBe(c);
});

test('pin rejects bad input and unknown chats, and a chat of another id cannot be reached', async ({ llm }) => {
    let a = await chat(llm, 'One', [['one', 'uno']]);
    fail(await api(llm, 'LLMAssistantPinThread', { pinned: true }), /threadId is required/);
    fail(await api(llm, 'LLMAssistantPinThread', { threadId: '', pinned: true }), /threadId is required/);
    fail(await api(llm, 'LLMAssistantPinThread', { threadId: a }), /pinned must be true or false/);
    fail(await api(llm, 'LLMAssistantPinThread', { threadId: a, pinned: 'true' }), /pinned must be true or false/);
    fail(await api(llm, 'LLMAssistantPinThread', { threadId: a, pinned: 1 }), /pinned must be true or false/);
    fail(await api(llm, 'LLMAssistantPinThread', { threadId: 'does-not-exist', pinned: true }), /not found/);
    fail(await api(llm, 'LLMAssistantPinThread', { threadId: '../other', pinned: true }), /not found/);
    expect((await row(llm, a)).pinned).toBeUndefined();
});

// ---- Export ----

test('export gives JSON that carries every branch and the one showing, Markdown of the conversation on screen, and refuses unknown formats', async ({ llm }) => {
    let id = await chat(llm, 'My: chat?', [['question one', 'answer one'], ['question two', 'answer two']]);
    let json = await api(llm, 'LLMAssistantExportThread', { threadId: id });
    expect(json).toMatchObject({ success: true, filename: 'My_chat.json' });
    let file = JSON.parse(json.content);
    expect(file).toMatchObject({ format: 'llmassistant-thread', version: 1, id, title: 'My: chat?' });
    expect(file.messages.map((m: any) => m.content)).toEqual(['question one', 'answer one', 'question two', 'answer two']);
    expect(file.activeLeafId).toBe(file.messages[3].id);
    expect(new Date(file.exportedAt).getTime()).toBeGreaterThan(0);

    let md = await api(llm, 'LLMAssistantExportThread', { threadId: id, format: 'markdown' });
    expect(md).toMatchObject({ success: true, filename: 'My_chat.md' });
    expect(md.content).toContain('# My: chat?');
    expect(md.content.indexOf('## You')).toBeLessThan(md.content.indexOf('## Assistant'));
    expect(md.content).toContain('answer two');
    expect((await api(llm, 'LLMAssistantExportThread', { threadId: id, format: 'md' })).filename).toBe('My_chat.md');
    expect((await api(llm, 'LLMAssistantExportThread', { threadId: id, format: 'JSON' })).filename).toBe('My_chat.json');

    // Markdown follows the branch showing: go back to the first turn and the second answer is not in it.
    await api(llm, 'LLMAssistantSetActiveLeaf', { threadId: id, messageId: file.messages[1].id });
    let back = await api(llm, 'LLMAssistantExportThread', { threadId: id, format: 'markdown' });
    expect(back.content).toContain('answer one');
    expect(back.content).not.toContain('answer two');
    expect(JSON.parse((await api(llm, 'LLMAssistantExportThread', { threadId: id })).content).messages).toHaveLength(4);

    fail(await api(llm, 'LLMAssistantExportThread', { threadId: id, format: 'pdf' }), /format must be/);
    fail(await api(llm, 'LLMAssistantExportThread', { threadId: 'nope' }), /not found/);
    // Core refuses a missing required parameter before the route runs.
    expect((await api(llm, 'LLMAssistantExportThread', {})).error).toMatch(/threadId/);
});

// ---- Import ----

const minimal = (extra: Record<string, unknown> = {}) => JSON.stringify({
    title: 'Imported one',
    messages: [{ role: 'user', content: 'imported question' }, { role: 'assistant', content: 'imported answer' }],
    ...extra
});

test('import previews without saving, then saves a new chat that opens, searches and exports like any other', async ({ llm }) => {
    let source = await chat(llm, 'Source chat', [['kiwi question', 'kiwi answer'], ['kiwi again', 'kiwi again answer']]);
    let exported = (await api(llm, 'LLMAssistantExportThread', { threadId: source })).content;
    let before = (await api(llm, 'LLMAssistantGetThreads')).threads.length;

    let preview = await api(llm, 'LLMAssistantImportThread', { content: exported, preview: true });
    expect(preview).toMatchObject({ success: true, preview: true });
    expect(preview.summary).toMatchObject({ title: 'Source chat', messageCount: 4, hasBranches: false, skipped: 0, warnings: [] });
    expect(preview.summary.sample).toEqual([{ role: 'user', text: 'kiwi question' }, { role: 'assistant', text: 'kiwi answer' }, { role: 'user', text: 'kiwi again' }]);
    expect((await api(llm, 'LLMAssistantGetThreads')).threads).toHaveLength(before);
    expect((await api(llm, 'LLMAssistantImportThread', { content: exported, preview: false, assistantId: '' })).success).toBe(true);
    expect((await api(llm, 'LLMAssistantGetThreads')).threads).toHaveLength(before + 1);
    await api(llm, 'LLMAssistantDeleteThread', { threadId: (await api(llm, 'LLMAssistantGetThreads')).threads[0].id });

    let applied = await api(llm, 'LLMAssistantImportThread', { content: exported });
    expect(applied.success, JSON.stringify(applied)).toBe(true);
    expect(applied.threadId).not.toBe(source);
    expect(applied.thread).toMatchObject({ id: applied.threadId, title: 'Source chat', messageCount: 4, mode: 'chat' });
    let thread = await getThread(llm, applied.threadId);
    let original = await getThread(llm, source);
    expect(thread.id).toBe(applied.threadId);
    expect(thread.messages.map((m: any) => [m.role, m.content])).toEqual(original.messages.map((m: any) => [m.role, m.content]));
    // New ids for everything, linked the same way, with the same leaf showing.
    let oldIds = new Set(original.messages.map((m: any) => m.id));
    expect(thread.messages.every((m: any) => !oldIds.has(m.id))).toBe(true);
    expect(thread.messages[0].parentId).toBeNull();
    expect(thread.messages[1].parentId).toBe(thread.messages[0].id);
    expect(thread.activeLeafId).toBe(thread.messages[3].id);
    expect(thread.titleAutoGenerated).toBe(true);
    // It is a chat like any other: listed, found by its text, exportable, and the imported copy exports to the same conversation.
    expect((await row(llm, applied.threadId))).toMatchObject({ title: 'Source chat' });
    let found = await api(llm, 'LLMAssistantSearchThreads', { query: 'kiwi again' });
    expect(found.results.map((r: any) => r.threadId).sort()).toEqual([applied.threadId, source].sort());
    let again = JSON.parse((await api(llm, 'LLMAssistantExportThread', { threadId: applied.threadId })).content);
    expect(again.messages.map((m: any) => m.content)).toEqual(JSON.parse(exported).messages.map((m: any) => m.content));
    // Importing the same file twice makes two chats.
    let twice = await api(llm, 'LLMAssistantImportThread', { content: exported });
    expect(twice.threadId).not.toBe(applied.threadId);
});

test('import keeps branches and which one was showing', async ({ llm }) => {
    let id = await chat(llm, 'Tree', [['root question', 'first reply']]);
    let thread = await getThread(llm, id);
    await api(llm, 'LLMAssistantSetActiveLeaf', { threadId: id, messageId: thread.messages[0].id });
    await setScenarios([{ match: 'branch question', rounds: [{ text: 'branch reply' }] }]);
    await api(llm, 'LLMAssistantRunTurn', { message: 'branch question', threadId: id });
    let exported = (await api(llm, 'LLMAssistantExportThread', { threadId: id })).content;
    let file = JSON.parse(exported);
    expect(file.messages.length).toBeGreaterThanOrEqual(3);
    let applied = await api(llm, 'LLMAssistantImportThread', { content: exported });
    expect(applied.success).toBe(true);
    expect(applied.summary.hasBranches).toBe(true);
    let copy = await getThread(llm, applied.threadId);
    expect(copy.messages).toHaveLength(file.messages.length);
    let leaf = copy.messages.find((m: any) => m.id === copy.activeLeafId);
    expect(leaf.content).toBe(file.messages.find((m: any) => m.id === file.activeLeafId).content);
    let byId = new Map(copy.messages.map((m: any) => [m.id, m]));
    let content = (m: any) => m.content;
    for (let m of copy.messages) {
        let source = file.messages.find((s: any) => s.content === m.content);
        let parent = m.parentId ? (byId.get(m.parentId) as any) : null;
        let sourceParent = source.parentId ? file.messages.find((s: any) => s.id === source.parentId) : null;
        expect(parent ? content(parent) : null).toBe(sourceParent ? content(sourceParent) : null);
    }
});

test('import refuses malformed files, wrong shapes and hostile ids with a message, and saves nothing', async ({ llm }) => {
    let count = async () => (await api(llm, 'LLMAssistantGetThreads')).threads.length;
    let before = await count();
    let message = (id: unknown, parentId?: unknown, extra: Record<string, unknown> = {}) => ({ id, ...(parentId === undefined ? {} : { parentId }), role: 'user', content: 'x', ...extra });
    let refused: [unknown, RegExp][] = [
        [undefined, /content is required/],
        ['', /content is required/],
        ['   ', /content is required/],
        [5, /content is required/],
        ['not json', /not valid JSON/],
        ['[1,2]', /expected a JSON object/],
        ['{}', /no messages list/],
        [JSON.stringify({ messages: [] }), /no user or assistant messages/],
        [JSON.stringify({ messages: [1] }), /not an object/],
        [JSON.stringify({ messages: [{ role: 'user', content: { a: 1 } }] }), /content that is not text/],
        [JSON.stringify({ messages: [{ role: 'system', content: 'only system' }] }), /no user or assistant messages/],
        [JSON.stringify({ format: 'other', messages: [{ role: 'user', content: 'x' }] }), /not an exported chat from this extension/],
        [JSON.stringify({ version: 2, messages: [{ role: 'user', content: 'x' }] }), /newer version/],
        ['{"messages":[{"role":"user","content":"x"}]} trailing', /not valid JSON/],
        // Hostile ids and links.
        [JSON.stringify({ messages: [message('a'), message('a')] }), /repeats the id/],
        [JSON.stringify({ messages: [message('a', 'ghost')] }), /parent message that is not in the file/],
        [JSON.stringify({ messages: [message('a', 'a')] }), /loop/],
        [JSON.stringify({ messages: [message('a', 'b'), message('b', 'a')] }), /loop/],
        [JSON.stringify({ messages: [message(7)] }), /id that is not a short text/],
        [JSON.stringify({ messages: [message('i'.repeat(500))] }), /id that is not a short text/],
        [JSON.stringify({ messages: [message('a', 7)] }), /parentId that is not a short text/],
        // Limits.
        [JSON.stringify({ messages: Array.from({ length: 2001 }, (_, i) => ({ role: 'user', content: `m${i}` })) }), /Too many messages/],
        [JSON.stringify({ messages: [{ role: 'user', content: 'x'.repeat(100_001) }] }), /too long/],
        ['{"messages":[],"pad":"' + 'x'.repeat(4_000_001) + '"}', /too large/]
    ];
    for (let [content, error] of refused) {
        let reply = await api(llm, 'LLMAssistantImportThread', content === undefined ? {} : { content });
        fail(reply, error);
        let previewReply = await api(llm, 'LLMAssistantImportThread', { content, preview: true });
        expect(previewReply.success, `${String(content).slice(0, 60)} (preview)`).toBe(false);
    }
    fail(await api(llm, 'LLMAssistantImportThread', { content: minimal(), preview: 'yes' }), /preview must be/);
    fail(await api(llm, 'LLMAssistantImportThread', { content: minimal(), assistantId: 'no-such-assistant' }), /Assistant 'no-such-assistant' not found/);
    expect(await count()).toBe(before);
    // The edges that are allowed.
    let atLimit = await api(llm, 'LLMAssistantImportThread', { content: JSON.stringify({ messages: Array.from({ length: 2000 }, (_, i) => ({ role: i % 2 ? 'assistant' : 'user', content: `m${i}` })) }), preview: true });
    expect(atLimit.success, JSON.stringify(atLimit).slice(0, 200)).toBe(true);
    expect(atLimit.summary.messageCount).toBe(2000);
    expect(await count()).toBe(before);
});

test('import never trusts ids or state in the file: new ids, text only, no attachments, tools, mode or workspace', async ({ llm }) => {
    let victim = await chat(llm, 'Victim', [['victim question', 'victim answer']]);
    let hostile = JSON.stringify({
        id: victim,
        title: 'Hostile\u0000 \u001b[31mtitle\n' + 'z'.repeat(500),
        assistantId: 'not-an-assistant',
        mode: 'code',
        workspace: 'C:/Windows',
        parentThreadId: victim,
        sessionStatus: 'running',
        scheduleId: 's-1',
        approvalMode: 'auto',
        toolsEnabled: true,
        memoryExtraction: true,
        pinned: true,
        compactions: [{ summary: 'injected summary text' }],
        params: { model: 'evil' },
        messages: [
            { id: victim, parentId: null, role: 'user', content: 'hello hostile', timestamp: '2025-01-02T03:04:05Z', media: [{ url: 'file:///C:/secrets.png', mediaType: 'image/png' }], toolCalls: [{ name: 'shell_exec', arguments: { command: 'calc' } }], rounds: [{ text: 'x' }] },
            { id: '../../etc/passwd', parentId: victim, role: 'assistant', content: 'proto reply', kind: null },
            { id: '__proto__', parentId: '../../etc/passwd', role: 'system', content: 'system text' },
            { id: 'tool-1', parentId: '__proto__', role: 'tool', content: 'tool output' },
            { id: 'last', parentId: 'tool-1', role: 'user', content: 'after the tool', timestamp: 'garbage' }
        ],
        activeLeafId: 'last'
    });
    let preview = await api(llm, 'LLMAssistantImportThread', { content: hostile, preview: true });
    expect(preview.success, JSON.stringify(preview)).toBe(true);
    expect(preview.summary).toMatchObject({ messageCount: 3, skipped: 2 });
    expect(preview.summary.warnings.join(' ')).toMatch(/2 messages left out/);
    expect(preview.summary.warnings.join(' ')).toMatch(/Attachments and tool calls are not imported/);
    expect(preview.summary.warnings.join(' ')).toMatch(/assistant this chat used is not available/);
    expect(preview.summary.title.length).toBeLessThanOrEqual(200);

    let applied = await api(llm, 'LLMAssistantImportThread', { content: hostile });
    expect(applied.success).toBe(true);
    expect(applied.threadId).not.toBe(victim);
    let thread = await getThread(llm, applied.threadId);
    // The victim chat is untouched.
    let untouched = await getThread(llm, victim);
    expect(untouched.messages.map((m: any) => m.content)).toEqual(['victim question', 'victim answer']);
    expect(untouched.pinned).toBeUndefined();
    // Only these fields exist on the new chat; none of the hostile ones.
    for (let key of ['workspace', 'parentThreadId', 'sessionStatus', 'scheduleId', 'approvalMode', 'toolsEnabled', 'memoryExtraction', 'pinned', 'compactions', 'params']) {
        expect(thread[key], key).toBeUndefined();
    }
    expect(thread.mode).toBe('chat');
    expect(thread.id).toBe(applied.threadId);
    expect(thread.messages.map((m: any) => [m.role, m.content])).toEqual([['user', 'hello hostile'], ['assistant', 'proto reply'], ['user', 'after the tool']]);
    for (let m of thread.messages) {
        expect(m.id).toMatch(/^[0-9a-f]{32}$/);
        for (let key of ['media', 'toolCalls', 'rounds', 'kind']) {
            expect(m[key], key).toBeUndefined();
        }
    }
    // The tool and system messages left a gap that the chain closes over: the last message hangs off the reply.
    expect(thread.messages[2].parentId).toBe(thread.messages[1].id);
    expect(thread.activeLeafId).toBe(thread.messages[2].id);
    expect(thread.messages[0].timestamp).toMatch(/^2025-01-02T03:04:05/);
    expect(JSON.stringify(thread)).not.toMatch(/secrets\.png|shell_exec|injected summary|etc\/passwd|evil/);
    expect(thread.title).toMatch(/^Hostile/);
    expect(thread.title).not.toMatch(/[\u0000-\u001f]/);
    // The assistant is one that exists here, not the one the file named.
    expect((await api(llm, 'LLMAssistantGetAssistant', { assistantId: thread.assistantId })).success).toBe(true);
    // Naming an assistant that exists wins over the file.
    let named = await api(llm, 'LLMAssistantImportThread', { content: hostile, assistantId: 'default' });
    expect((await getThread(llm, named.threadId)).assistantId).toBe('default');
});

test('import of an old-style export (no ids, no format) makes a straight chat titled from the first message', async ({ llm }) => {
    let applied = await api(llm, 'LLMAssistantImportThread', { content: JSON.stringify({ messages: [{ role: 'user', content: 'old style hello' }, { role: 'assistant', content: 'old style reply' }] }) });
    expect(applied.success, JSON.stringify(applied)).toBe(true);
    expect(applied.summary).toMatchObject({ messageCount: 2, hasBranches: false });
    let thread = await getThread(llm, applied.threadId);
    expect(thread.title).toBe('old style hello');
    expect(thread.messages[1].parentId).toBe(thread.messages[0].id);
    expect(thread.activeLeafId).toBe(thread.messages[1].id);
});

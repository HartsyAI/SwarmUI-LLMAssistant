import { test, expect, setScenarios, mockRequests, clearRequests, api, embeddingRequests, clearEmbeddingRequests, failEmbeddings, reopen } from './fixtures';
import type { Page } from '@playwright/test';

// The setup check, and the headless turn (one request, one reply) with its dry run.

/** Runs a headless turn. */
async function run(page: Page, request: Record<string, unknown>): Promise<any> {
    return await api(page, 'LLMAssistantRunTurn', request);
}

/** The chat as the server stores it. */
async function thread(page: Page, id: string): Promise<any> {
    return (await api(page, 'LLMAssistantGetThread', { threadId: id })).thread;
}

/** The report, as a map from check id to the check. */
async function readiness(page: Page, deep = false): Promise<{ overall: string; checks: Record<string, any>; raw: any }> {
    let raw = await api(page, 'LLMAssistantReadiness', { deep });
    expect(raw.success, JSON.stringify(raw)).toBe(true);
    return { overall: raw.overall, checks: Object.fromEntries(raw.checks.map((c: any) => [c.id, c])), raw };
}

// ---- The setup check ----

test('the setup check reports every part by a stable id and finds the backend ready', async ({ llm }) => {
    let report = await readiness(llm);
    expect(Object.keys(report.checks)).toEqual(['backends', 'model', 'tools', 'mcp', 'hooks', 'skills', 'knowledge', 'schedules', 'sessions', 'storage']);
    expect(report.checks.backends).toMatchObject({ status: 'ok' });
    expect(report.checks.backends.detail).toMatch(/\d+ backends?, \d+ models?/);
    expect(report.checks.storage.status).toBe('ok');
    expect(report.checks.tools.status).toBe('ok');
    expect(['ok', 'warn']).toContain(report.overall);
    expect(report.raw.deep).toBe(false);
    for (let check of Object.values(report.checks) as any[]) {
        expect(['ok', 'warn', 'fail', 'info']).toContain(check.status);
        expect(check.title.length).toBeGreaterThan(0);
        expect(check.detail.length).toBeGreaterThan(0);
    }
});

test('the check reads only: it connects nothing, and asks the embedding model only when it is deep', async ({ llm }) => {
    let providers = (await api(llm, 'LLMAssistantGetKnowledge')).providers;
    expect((await api(llm, 'LLMAssistantSetKnowledgeSettings', { providerId: providers[0].id, model: 'mock-embed' })).success).toBe(true);
    await api(llm, 'LLMAssistantAddKnowledge', { name: 'a.txt', text: 'a note about cats' });
    await clearEmbeddingRequests();

    let shallow = await readiness(llm);
    expect(shallow.checks.knowledge.status).toBe('ok');
    expect(shallow.checks.knowledge.detail).toContain('1 document');
    expect(shallow.checks.knowledge.detail).toContain('not tried');
    expect(await embeddingRequests()).toEqual([]);

    let deep = await readiness(llm, true);
    expect(deep.raw.deep).toBe(true);
    expect(deep.checks.knowledge.status).toBe('ok');
    expect(deep.checks.knowledge.detail).not.toContain('not tried');
    expect((await embeddingRequests()).map(r => r.model)).toEqual(['mock-embed']);

    // A model that stops answering is a warning with the reason, and the report still comes back.
    await failEmbeddings(500);
    let failing = await readiness(llm, true);
    expect(failing.checks.knowledge.status).toBe('warn');
    expect(failing.checks.knowledge.detail).toContain('500');
    expect(failing.checks.knowledge.fix).toContain('Settings > Knowledge');
    expect(['warn', 'fail']).toContain(failing.overall);
    // The other parts are unaffected.
    expect(failing.checks.backends.status).toBe('ok');
});

test('the check sees schedules and sessions, and says when a tool points at nothing', async ({ llm }) => {
    expect((await readiness(llm)).checks.schedules.status).toBe('info');
    let saved = await api(llm, 'LLMAssistantSaveSchedule', { schedule: { title: 'Hourly', prompt: 'x', model: 'mock-model', trigger: { kind: 'every', everyMinutes: 60 }, enabled: true } });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
    let schedules = (await readiness(llm)).checks.schedules;
    expect(schedules.status).toBe('ok');
    expect(schedules.detail).toContain('1 schedule switched on');
    await api(llm, 'LLMAssistantDeleteSchedule', { scheduleId: saved.id });

    // A tool of the user's own whose handler does not exist.
    let tool = await api(llm, 'LLMAssistantSaveTool', { tool: { id: 'ghost_tool', name: 'Ghost', description: 'Points at nothing.', handlerType: 'builtin', handlerId: 'no_such_handler', enabled: true, parameters: { type: 'object', properties: {} } }, scope: 'personal' });
    if (tool.success) {
        let report = await readiness(llm);
        expect(report.checks.tools.status).toBe('fail');
        expect(report.checks.tools.detail).toContain('ghost_tool');
        expect(report.overall).toBe('fail');
        await api(llm, 'LLMAssistantDeleteTool', { toolId: 'ghost_tool', scope: 'personal' });
        expect((await readiness(llm)).checks.tools.status).toBe('ok');
    }
});

test('the Setup check in the General tab shows the report, and the deep one says so', async ({ llm }) => {
    await llm.locator('#llma-settings-btn').click();
    await expect(llm.locator('#llma-readiness h6')).toHaveText('Setup check');
    await llm.locator('#llma-readiness-run').click();
    await expect(llm.locator('#llma-readiness-overall')).toBeVisible();
    await expect(llm.locator('#llma-readiness li[data-check]')).toHaveCount(10);
    await expect(llm.locator('#llma-readiness li[data-check="backends"] .badge')).toHaveText('OK');
    await expect(llm.locator('#llma-readiness li[data-check="mcp"] .badge')).toHaveText('Not set up');
    await expect(llm.locator('#llma-readiness-overall')).not.toContainText('deep check');
    await llm.locator('#llma-readiness-deep').click();
    await expect(llm.locator('#llma-readiness-overall')).toContainText('deep check');
});

// ---- The headless turn ----

test('a request with no chat makes one, returns the reply, and the chat is there to open', async ({ llm }) => {
    await setScenarios([{ match: 'what is two plus two', rounds: [{ text: 'Four.', usage: { prompt_tokens: 12, completion_tokens: 2 } }] }]);
    let reply = await run(llm, { message: 'what is two plus two' });
    expect(reply).toMatchObject({ success: true, response: 'Four.', created: true, stopReason: 'stop', toolCalls: [] });
    expect(reply.model.length).toBeGreaterThan(0);
    expect(reply.usage).toMatchObject({ promptTokens: 12, completionTokens: 2 });
    let saved = await thread(llm, reply.threadId);
    expect(saved.title).toBe('what is two plus two');
    expect(saved.messages.map((m: any) => `${m.role}:${m.content}`)).toEqual(['user:what is two plus two', 'assistant:Four.']);
    // The chat is in the list, and its title was not replaced by a generated one.
    expect(((await api(llm, 'LLMAssistantGetThreads')).threads as any[]).map(t => t.id)).toContain(reply.threadId);
    // No title request was made to the model: only the one chat request.
    expect((await mockRequests()).filter(r => !(r.tools ?? []).length && JSON.stringify(r.messages).includes('titles for chat'))).toEqual([]);
});

test('passing the chat back carries the conversation on, and the model is sent the history', async ({ llm }) => {
    await setScenarios([
        { match: 'my name is Ada', rounds: [{ text: 'Nice to meet you, Ada.' }] },
        { match: 'what is my name', rounds: [{ text: 'Your name is Ada.' }] },
    ]);
    let first = await run(llm, { message: 'my name is Ada' });
    await clearRequests();
    let second = await run(llm, { message: 'what is my name', threadId: first.threadId });
    expect(second).toMatchObject({ success: true, response: 'Your name is Ada.', threadId: first.threadId });
    expect(second.created).toBeUndefined();
    let sent = (await mockRequests()).find(r => JSON.stringify(r.messages).includes('what is my name'));
    let text = JSON.stringify(sent.messages);
    expect(text).toContain('my name is Ada');
    expect(text).toContain('Nice to meet you, Ada.');
    expect((await thread(llm, first.threadId)).messages).toHaveLength(4);
});

test('a tool that only reads runs, one that changes something is refused because nobody can approve it, and Auto lets it run', async ({ llm }) => {
    let file = `headless-${Date.now()}.txt`;
    await setScenarios([
        { match: 'read my memory', rounds: [{ toolCalls: [{ name: 'memory_read', arguments: {} }] }, { text: 'Read it.' }] },
        { match: 'write ask', rounds: [{ toolCalls: [{ name: 'file_write', arguments: { path: file, content: 'x', overwrite: true } }] }, { text: 'Tried to write.' }] },
        { match: 'write auto', rounds: [{ toolCalls: [{ name: 'file_write', arguments: { path: file, content: 'x', overwrite: true } }] }, { text: 'Wrote it.' }] },
    ]);
    let read = await run(llm, { message: 'read my memory' });
    expect(read.response).toBe('Read it.');
    expect(read.toolCalls).toHaveLength(1);
    expect(read.toolCalls[0]).toMatchObject({ name: 'memory_read' });
    expect(read.toolCalls[0].result.denied).toBeUndefined();

    // The default is Ask: it would have to ask the user, and nobody is there.
    let refused = await run(llm, { message: 'write ask' });
    expect(refused.response).toBe('Tried to write.');
    expect(refused.toolCalls[0].result).toMatchObject({ success: false, denied: true });
    expect(refused.toolCalls[0].result.error).toContain('cannot ask for it');

    let allowed = await run(llm, { message: 'write auto', approvalMode: 'auto' });
    expect(allowed.toolCalls[0].result.success).toBe(true);
    // Auto was for that request; it was not saved onto the chat, so the interface does not run in Auto because a script did.
    expect((await thread(llm, allowed.threadId)).approvalMode).toBeUndefined();
});

test('a script cannot loosen a chat the user keeps in Plan', async ({ llm }) => {
    await setScenarios([{ match: 'try writing', rounds: [{ toolCalls: [{ name: 'file_write', arguments: { path: 'plan-guard.txt', content: 'x', overwrite: true } }] }, { text: 'Done.' }] }]);
    let chat = (await api(llm, 'LLMAssistantCreateThread', { assistantId: 'default' })).thread.id;
    await api(llm, 'LLMAssistantSetThreadApprovalMode', { threadId: chat, mode: 'plan' });
    let reply = await run(llm, { message: 'try writing', threadId: chat, approvalMode: 'auto' });
    expect(reply.toolCalls[0].result).toMatchObject({ success: false, denied: true });
    expect(reply.toolCalls[0].result.error).toContain('Plan mode');
});

test('a dry run builds the request and reports on it without calling the model, saving anything, or changing the chat', async ({ llm }) => {
    let first = await run(llm, { message: 'hello there' });
    let before = await thread(llm, first.threadId);
    let threadsBefore = ((await api(llm, 'LLMAssistantGetThreads')).threads as any[]).length;
    await clearRequests();

    let existing = await run(llm, { message: 'a question for the next turn', threadId: first.threadId, dryRun: true });
    expect(existing).toMatchObject({ success: true, dryRun: true, modelFound: true, threadId: first.threadId, approvalMode: 'ask', wouldCompact: false });
    // The history it would send: the two messages so far and the new one.
    expect(existing.historyMessages).toBe(3);
    expect(existing.tools).toContain('memory_read');
    expect(existing.systemPromptChars).toBeGreaterThan(0);
    expect(existing.estimatedPromptTokens).toBeGreaterThan(0);
    expect(existing.contextWindow).toBe(4000);
    expect(existing.warnings.join(' ')).toContain('would be refused');

    // With no chat, nothing is made.
    let none = await run(llm, { message: 'just looking', dryRun: true, approvalMode: 'auto' });
    expect(none.success).toBe(true);
    expect(none.threadId).toBeNull();
    expect(none.historyMessages).toBe(1);
    expect(none.warnings.join(' ')).not.toContain('would be refused');

    // No model was called, and nothing changed.
    expect(await mockRequests()).toEqual([]);
    expect(await thread(llm, first.threadId)).toEqual(before);
    expect(((await api(llm, 'LLMAssistantGetThreads')).threads as any[]).length).toBe(threadsBefore);
});

test('a dry run says a long chat would be summarized first, and does not do it', async ({ llm }) => {
    // The mock model's window is 4,000 tokens; a message of 14,000 characters is most of it.
    let first = await run(llm, { message: 'long ' + 'word '.repeat(2800) });
    expect(first.success).toBe(true);
    let report = await run(llm, { message: 'and now?', threadId: first.threadId, dryRun: true });
    expect(report.wouldCompact).toBe(true);
    expect(report.warnings.join(' ')).toContain('summarized');
    expect((await thread(llm, first.threadId)).compactions).toBeUndefined();
});

test('a dry run names a model nobody offers', async ({ llm }) => {
    let report = await run(llm, { message: 'x', dryRun: true, model: 'no-such-model' });
    expect(report).toMatchObject({ success: true, model: 'no-such-model', modelFound: false });
    expect(report.warnings.join(' ')).toContain("No backend offers the model 'no-such-model'");
});

test('a chat with a reply being written is not interleaved with, and Stop ends a request that is waiting', async ({ llm }) => {
    await setScenarios([{ match: 'slow headless', rounds: [{ text: 'Late.', delayMs: 15_000 }] }]);
    let chat = (await api(llm, 'LLMAssistantCreateThread', { assistantId: 'default' })).thread.id;
    let slow = run(llm, { message: 'slow headless', threadId: chat });
    // Wait until the model has been sent the first one: by then its reply is registered. (Asking sooner would race it, and the second request would simply be the first.)
    await expect.poll(async () => (await mockRequests()).some(r => JSON.stringify(r.messages ?? []).includes('slow headless')), { timeout: 20_000 }).toBe(true);
    // With the first in flight, a second for the same chat is refused.
    expect((await run(llm, { message: 'second', threadId: chat })).error).toContain('already being written');
    expect((await api(llm, 'LLMAssistantStopGeneration', { threadId: chat })).cancelled).toBe(true);
    let stopped = await slow;
    // What was in the chat stays: the first message, and nothing from the refused one.
    expect(stopped.threadId).toBe(chat);
    let messages = (await thread(llm, chat)).messages;
    expect(messages[0].content).toBe('slow headless');
    expect(messages.some((m: any) => m.content === 'second')).toBe(false);
    // The chat is free again.
    await setScenarios([{ match: 'after', rounds: [{ text: 'Free again.' }] }]);
    expect((await run(llm, { message: 'after', threadId: chat })).response).toBe('Free again.');
});

test('bad requests are refused with a reason, and a model that fails leaves what was said in the chat', async ({ llm }) => {
    expect((await run(llm, {})).error).toContain('message is required');
    expect((await run(llm, { message: '   ' })).error).toContain('message is required');
    expect((await run(llm, { message: 'x', threadId: 'nope' })).error).toContain('not found');
    expect((await run(llm, { message: 'x', assistantId: 'no-such-assistant' })).error).toContain('There is no assistant');
    // A backend that errors: the turn fails with its reason, and the user's message is kept in the chat that was made.
    await setScenarios([{ match: 'will not work', rounds: [{ error: { status: 500, message: 'the backend fell over' } }] }]);
    let failed = await run(llm, { message: 'will not work' });
    expect(failed.success).toBe(false);
    expect(failed.error).toContain('fell over');
    expect(failed.threadId).toBeTruthy();
    expect((await thread(llm, failed.threadId)).messages[0].content).toBe('will not work');
});

test('with no model named, the first one a backend offers is used', async ({ llm }) => {
    await setScenarios([{ regex: 'no model named', rounds: [{ text: 'ok' }] }]);
    let reply = await run(llm, { message: 'no model named' });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    expect(['mock-model', 'mock-model-b']).toContain(reply.model);
});

test('a message to a session is a turn of that session, and it reports to its parent as any turn does', async ({ llm }) => {
    let parent = (await api(llm, 'LLMAssistantCreateThread', { assistantId: 'default' })).thread.id;
    let tools = (await api(llm, 'LLMAssistantGetTools')).tools as any[];
    for (let id of ['session_start', 'session_list', 'session_read', 'session_send', 'session_wait', 'session_stop', 'session_archive']) {
        await api(llm, 'LLMAssistantSaveTool', { tool: { ...tools.find(t => t.id === id), enabled: true }, scope: 'shared' });
    }
    await setScenarios([{ match: 'child first', rounds: [{ text: 'Child ready.' }] }, { match: 'child second', rounds: [{ text: 'Child answered.' }] }]);
    let started = (await api(llm, 'LLMAssistantExecuteTool', { toolId: 'session_start', threadId: parent, arguments: { title: 'Child', prompt: 'child first', model: 'mock-model' } })).result;
    await expect.poll(async () => (await thread(llm, started.id)).sessionStatus, { timeout: 30_000 }).toBe('done');
    let reply = await run(llm, { message: 'child second', threadId: started.id });
    expect(reply).toMatchObject({ success: true, response: 'Child answered.' });
    let child = await thread(llm, started.id);
    expect(child.sessionStatus).toBe('done');
    expect(child.lastReport).toBe('Child answered.');
    for (let id of ['session_start', 'session_list', 'session_read', 'session_send', 'session_wait', 'session_stop', 'session_archive']) {
        let tool = ((await api(llm, 'LLMAssistantGetTools')).tools as any[]).find(t => t.id === id);
        await api(llm, 'LLMAssistantSaveTool', { tool: { ...tool, enabled: false }, scope: 'shared' });
    }
});

import { test as base, expect, send, lastAssistant, setScenarios, mockRequests, clearRequests, api, turn, enableSessionTools, resetSessionTools, reopen } from './fixtures';
import type { Page } from '@playwright/test';

// Sessions: chats the assistant starts and directs. They run in the background with no socket, nest under their parent in the chat list, and report back.

const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await enableSessionTools(llm);
        await use(llm);
        await resetSessionTools(llm);
    }
});

/** Makes a chat and returns its id. */
async function newChat(page: Page): Promise<string> {
    let created = await api(page, 'LLMAssistantCreateThread', { assistantId: 'default' });
    expect(created.success).toBe(true);
    return created.thread.id;
}

/** Runs a session tool the way the "/" picker does (as the user, from the chat), so a spec can name a session by its id. */
async function run(page: Page, parent: string, toolId: string, args: Record<string, unknown> = {}): Promise<any> {
    let reply = await api(page, 'LLMAssistantExecuteTool', { toolId, threadId: parent, arguments: args });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    return reply.result;
}

/** Starts a session on the mock model and returns its id. */
async function start(page: Page, parent: string, title: string, prompt: string, extra: Record<string, unknown> = {}): Promise<string> {
    let result = await run(page, parent, 'session_start', { title, prompt, model: 'mock-model', ...extra });
    expect(result.success, JSON.stringify(result)).toBe(true);
    return result.id;
}

/** The chat as the server stores it. */
async function thread(page: Page, id: string): Promise<any> {
    return (await api(page, 'LLMAssistantGetThread', { threadId: id })).thread;
}

/** The chat list's rows for the sessions of a chat. */
async function sessionsOf(page: Page, parent: string): Promise<any[]> {
    return ((await api(page, 'LLMAssistantGetThreads')).threads as any[]).filter(t => t.parentThreadId == parent);
}

/** Waits for a session's status, polling the server (never the page). */
async function waitStatus(page: Page, id: string, status: string, timeout = 30_000) {
    await expect.poll(async () => (await thread(page, id))?.sessionStatus, { timeout }).toBe(status);
}

/** The chat requests whose last user message contains the text (a session's first message, say), oldest first. */
async function requestsFor(text: string): Promise<any[]> {
    return (await mockRequests()).filter(r => {
        let users = (r.messages ?? []).filter((m: any) => m.role == 'user');
        return JSON.stringify(users[users.length - 1]?.content ?? '').includes(text);
    });
}

const rows = (page: Page) => page.locator('#llma-thread-list .llma-thread-item');

test('the assistant starts sessions: they nest under their chat, run in the background, and report back', async ({ llm }) => {
    await setScenarios([
        turn('run two jobs', [
            { name: 'session_start', arguments: { title: 'Job A', prompt: 'job-a-task', model: 'mock-model' } },
            { name: 'session_start', arguments: { title: 'Job B', prompt: 'job-b-task', model: 'mock-model' } },
        ], 'Started both.'),
        { match: 'job-a-task', rounds: [{ text: 'Report A: all good.', delayMs: 800 }] },
        { match: 'job-b-task', rounds: [{ text: 'Report B: found 3 files.', delayMs: 800 }] },
    ]);
    await send(llm, 'run two jobs');
    await expect(lastAssistant(llm)).toContainText('Started both.');
    // The tool results in the chat are cards with the session and a button to open it.
    await expect(llm.locator('.llma-session-card')).toHaveCount(2);

    let parent = (await api(llm, 'LLMAssistantGetThreads')).threads.find((t: any) => !t.parentThreadId).id;
    await expect.poll(async () => (await sessionsOf(llm, parent)).length).toBe(2);
    let [a, b] = await sessionsOf(llm, parent);
    await waitStatus(llm, a.id, 'done');
    await waitStatus(llm, b.id, 'done');

    // What each session did is its own chat, with its report kept.
    let childA = await thread(llm, a.id);
    expect(childA.parentThreadId).toBe(parent);
    expect(childA.depth).toBe(1);
    expect([childA.lastReport, (await thread(llm, b.id)).lastReport].sort()).toEqual(['Report A: all good.', 'Report B: found 3 files.']);
    expect(childA.messages.map((m: any) => m.role)).toEqual(['user', 'assistant']);

    // The list nests them under the chat, with a count, and each row shows how it ended.
    await expect(llm.locator('.llma-thread-item[data-depth="0"]')).toHaveCount(1);
    await expect(llm.locator('.llma-thread-item.llma-thread-child[data-depth="1"]')).toHaveCount(2);
    await expect(llm.locator('.llma-session-count')).toHaveText('2');
    await expect(llm.locator('.llma-thread-child .llma-session-dot[data-status="done"]')).toHaveCount(2);
    await expect(llm.locator('.llma-session-card .llma-session-card-status')).toHaveText(['Done', 'Done']);
});

test('a session that finished is told to its chat as a saved session update, before the next message', async ({ llm }) => {
    let parent = await newChat(llm);
    await setScenarios([
        { match: 'research-task', rounds: [{ text: 'Found: the answer is 42.' }] },
        { match: 'what did it find', rounds: [{ text: 'It found 42.' }] },
    ]);
    let child = await start(llm, parent, 'Research', 'research-task');
    await waitStatus(llm, child, 'done');

    await reopen(llm);
    await llm.locator('#llma-thread-list .llma-thread-item[data-depth="0"]').click();
    await clearRequests();
    await send(llm, 'what did it find');
    await expect(lastAssistant(llm)).toContainText('It found 42.');

    // The model read the report with the conversation, ahead of what the user asked.
    await expect.poll(async () => (await requestsFor('what did it find')).length).toBeGreaterThan(0);
    let sent = (await requestsFor('what did it find'))[0];
    let users = sent.messages.filter((m: any) => m.role == 'user').map((m: any) => JSON.stringify(m.content));
    expect(users.length).toBeGreaterThanOrEqual(2);
    expect(users[users.length - 2]).toContain('[Session update]');
    expect(users[users.length - 2]).toContain('Found: the answer is 42.');
    // It is part of the saved chat (given once) and the page shows it as a note, not as something typed.
    let saved = await thread(llm, parent);
    expect(saved.messages.filter((m: any) => m.kind == 'session_report' && m.sessionId == child)).toHaveLength(1);
    expect(saved.sessionNotices).toBeUndefined();
    await expect(llm.locator('.llma-session-update')).toContainText('Found: the answer is 42.');
    await expect(llm.locator('.llma-session-update .llma-msg-actions')).toHaveCount(0);
});

test('session_wait returns the report, and session_read shows how it is going', async ({ llm }) => {
    let parent = await newChat(llm);
    await setScenarios([{ match: 'slow-task', rounds: [{ text: 'Slow report.', delayMs: 1500 }] }]);
    let child = await start(llm, parent, 'Slow', 'slow-task');
    let waiting = await run(llm, parent, 'session_wait', { id: child, timeoutSeconds: 30 });
    expect(waiting).toMatchObject({ success: true, id: child, status: 'done', report: 'Slow report.' });

    let read = await run(llm, parent, 'session_read', { id: child });
    expect(read.status).toBe('done');
    expect(read.recent.map((m: any) => m.role)).toEqual(['user', 'assistant']);
    expect(read.recent[1].content).toBe('Slow report.');

    // A wait that runs out says the session is still going, and does not fail.
    await setScenarios([{ match: 'long-task', rounds: [{ text: 'Late.', delayMs: 6000 }] }]);
    let slow = await start(llm, parent, 'Long', 'long-task');
    let early = await run(llm, parent, 'session_wait', { id: slow, timeoutSeconds: 1 });
    expect(early.success).toBe(true);
    expect(['running', 'queued']).toContain(early.status);
    expect(early.note).toContain('still working');

    let list = await run(llm, parent, 'session_list');
    expect(list.sessions.map((s: any) => s.title).sort()).toEqual(['Long', 'Slow']);
});

test('a message to a working session is read when its turn ends and starts another turn', async ({ llm }) => {
    let parent = await newChat(llm);
    await setScenarios([
        { match: 'first-task', rounds: [{ text: 'First done.', delayMs: 2500 }] },
        { match: 'also check the logs', rounds: [{ text: 'Logs checked.' }] },
    ]);
    let child = await start(llm, parent, 'Steered', 'first-task');
    await waitStatus(llm, child, 'running');
    let sent = await run(llm, parent, 'session_send', { id: child, message: 'also check the logs' });
    expect(sent).toMatchObject({ success: true, status: 'running' });
    expect(sent.note).toContain('when its current turn ends');

    // Two turns: the original, then the one the message started.
    await expect.poll(async () => (await thread(llm, child)).messages.map((m: any) => m.role).join(','), { timeout: 30_000 }).toBe('user,assistant,user,assistant');
    await waitStatus(llm, child, 'done');
    let messages = (await thread(llm, child)).messages;
    expect(messages[2].content).toBe('also check the logs');
    expect(messages[3].content).toBe('Logs checked.');
    expect((await thread(llm, child)).lastReport).toBe('Logs checked.');
});

test('a session that needs approval waits for the user, shows it, and carries on when answered', async ({ llm }) => {
    let parent = await newChat(llm);
    let file = `session-${Date.now()}.txt`;
    await setScenarios([{ match: 'write-task', rounds: [{ toolCalls: [{ name: 'file_write', arguments: { path: file, content: 'from a session', overwrite: true } }] }, { text: 'Wrote it.' }] }]);
    let child = await start(llm, parent, 'Writer', 'write-task', { approvalMode: 'ask' });
    await waitStatus(llm, child, 'awaiting_approval');
    let waiting = await thread(llm, child);
    expect(waiting.pendingApproval).toMatchObject({ name: 'file_write', risk: 'write' });
    expect(waiting.approvalMode).toBe('ask');

    // A parent waiting on it is told, rather than blocking on something only the user can clear.
    let wait = await run(llm, parent, 'session_wait', { id: child, timeoutSeconds: 30 });
    expect(wait.status).toBe('awaiting_approval');
    expect(wait.waitingForApproval.tool).toBe('file_write');
    expect(JSON.stringify(wait)).not.toContain(waiting.pendingApproval.approvalId);

    // The chat list shows it, and opening the session shows the call with the three answers.
    await reopen(llm);
    await expect(llm.locator('.llma-thread-child .llma-session-dot[data-status="awaiting_approval"]')).toHaveCount(1);
    await llm.locator('.llma-thread-child').click();
    await expect(llm.locator('#llma-session-banner')).toContainText('Needs approval');
    await expect(llm.locator('#llma-session-banner .llma-session-approval')).toContainText('Allow file write?');
    await llm.locator('#llma-session-banner button[data-decision="allow"]').click();

    await waitStatus(llm, child, 'done');
    expect((await thread(llm, child)).pendingApproval).toBeUndefined();
    expect((await thread(llm, child)).lastReport).toBe('Wrote it.');
    await expect(llm.locator('#llma-session-banner')).toContainText('Done');
    await expect(llm.locator('.llma-msg-row.assistant .llma-msg-bubble').last()).toContainText('Wrote it.');
});

test('denying a session\'s call, and an unanswered call, both refuse it and let the session finish', async ({ llm }) => {
    let parent = await newChat(llm);
    await setScenarios([{ match: 'deny-task', rounds: [{ toolCalls: [{ name: 'file_write', arguments: { path: 'never.txt', content: 'x', overwrite: true } }] }, { text: 'Could not write.' }] }]);
    let child = await start(llm, parent, 'Denied', 'deny-task', { approvalMode: 'ask' });
    await waitStatus(llm, child, 'awaiting_approval');
    let pending = (await thread(llm, child)).pendingApproval;
    let answered = await api(llm, 'LLMAssistantResolveToolApproval', { approvalId: pending.approvalId, decision: 'deny' });
    expect(answered.resolved).toBe(true);
    await waitStatus(llm, child, 'done');
    let calls = (await thread(llm, child)).messages.find((m: any) => m.role == 'assistant').toolCalls;
    expect(calls[0].result.denied).toBe(true);
});

test('a session is never less careful than its chat, and can use only tools its chat could', async ({ llm }) => {
    let parent = await newChat(llm);
    await api(llm, 'LLMAssistantSetThreadApprovalMode', { threadId: parent, mode: 'ask' });
    await setScenarios([{ regex: 'narrow-task', rounds: [{ text: 'Looked.' }] }]);
    let loose = await start(llm, parent, 'Loose', 'narrow-task one', { approvalMode: 'auto', tools: ['web_search', 'not_a_tool'] });
    let child = await thread(llm, loose);
    // Asked for auto, got the chat's own stricter mode; asked for a tool that does not exist, got only the real one.
    expect(child.approvalMode).toBe('ask');
    expect(child.sessionToolIds).toEqual(['web_search']);
    await waitStatus(llm, loose, 'done');
    let seen = (await requestsFor('narrow-task one'))[0];
    expect(seen.tools.map((t: any) => t.function.name)).toEqual(['web_search']);

    // A stricter request is honoured.
    let plan = await start(llm, parent, 'Plan', 'narrow-task two', { approvalMode: 'plan' });
    expect((await thread(llm, plan)).approvalMode).toBe('plan');
    // With no list, it gets all of the chat's tools but never the session tools (it is as deep as sessions go).
    let all = await thread(llm, plan);
    expect(all.sessionToolIds).toContain('web_search');
    for (let id of ['session_start', 'session_send', 'session_wait']) {
        expect(all.sessionToolIds).not.toContain(id);
    }
});

test('a tool outside a session\'s list is refused even if the model names it', async ({ llm }) => {
    let parent = await newChat(llm);
    await setScenarios([{ match: 'sneaky-task', rounds: [{ toolCalls: [{ name: 'file_write', arguments: { path: 'x.txt', content: 'x', overwrite: true } }] }, { text: 'Tried.' }] }]);
    let child = await start(llm, parent, 'Sneaky', 'sneaky-task', { tools: ['web_search'] });
    await waitStatus(llm, child, 'done');
    let call = (await thread(llm, child)).messages.find((m: any) => m.role == 'assistant').toolCalls[0];
    expect(call.result.success).toBe(false);
    expect(call.result.error).toContain('not available to this session');
});

test('sessions can nest only as deep as the limit, and a chat has a bound on how many it can start', async ({ llm }) => {
    let parent = await newChat(llm);
    await setScenarios([{ regex: '.*', rounds: [{ text: 'ok', delayMs: 4000 }] }]);
    let child = await start(llm, parent, 'Child', 'depth-task');
    // The default allows one level: a session cannot start sessions.
    let deeper = await run(llm, child, 'session_start', { title: 'Grandchild', prompt: 'x', model: 'mock-model' });
    expect(deeper.success).toBe(false);
    expect(deeper.error).toContain('only 1 deep');
    expect((await sessionsOf(llm, child)).length).toBe(0);

    // The chat's own limit: eight not archived.
    for (let i = 2; i <= 8; i++) {
        await start(llm, parent, `Child ${i}`, `depth-task ${i}`);
    }
    let ninth = await run(llm, parent, 'session_start', { title: 'Too many', prompt: 'x', model: 'mock-model' });
    expect(ninth.success).toBe(false);
    expect(ninth.error).toContain('already has 8 sessions');
    // Archiving one makes room.
    await run(llm, parent, 'session_archive', { id: child });
    expect((await run(llm, parent, 'session_start', { title: 'Now fits', prompt: 'x', model: 'mock-model' })).success).toBe(true);
});

test('no more than three of a user\'s sessions work at once; the rest wait their turn', async ({ llm }) => {
    let parent = await newChat(llm);
    await setScenarios([{ regex: 'queue-task', rounds: [{ text: 'ok', delayMs: 2500 }] }]);
    let ids: string[] = [];
    for (let i = 1; i <= 4; i++) {
        ids.push(await start(llm, parent, `Q${i}`, `queue-task ${i}`));
    }
    await expect.poll(async () => (await sessionsOf(llm, parent)).map(s => s.sessionStatus).sort().join(',')).toBe('queued,running,running,running');
    // They all get through, the queued one after one of the others finishes.
    for (let id of ids) {
        await waitStatus(llm, id, 'done', 60_000);
    }
});

test('Stop on a session stops it, and stopping its chat leaves its sessions running', async ({ llm }) => {
    let parent = await newChat(llm);
    await setScenarios([{ match: 'stop-task', rounds: [{ text: 'Never said.', delayMs: 15_000 }] }]);
    let child = await start(llm, parent, 'Stoppable', 'stop-task');
    await waitStatus(llm, child, 'running');
    // Stopping the chat that started it does not stop the session.
    await api(llm, 'LLMAssistantStopGeneration', { threadId: parent });
    await new Promise(r => setTimeout(r, 500));
    expect((await thread(llm, child)).sessionStatus).toBe('running');
    // Stopping the session does, and what it had is kept; it can be started again.
    let stopped = await api(llm, 'LLMAssistantStopGeneration', { threadId: child });
    expect(stopped.cancelled).toBe(true);
    await waitStatus(llm, child, 'idle');
    expect((await thread(llm, child)).messages[0].content).toBe('stop-task');
});

test('a queued session can be stopped before it starts', async ({ llm }) => {
    let parent = await newChat(llm);
    await setScenarios([{ regex: 'hold-task', rounds: [{ text: 'ok', delayMs: 5000 }] }]);
    let ids: string[] = [];
    for (let i = 1; i <= 4; i++) {
        ids.push(await start(llm, parent, `H${i}`, `hold-task ${i}`));
    }
    await waitStatus(llm, ids[3], 'queued');
    let stopped = await run(llm, parent, 'session_stop', { id: ids[3] });
    expect(stopped).toMatchObject({ success: true, wasRunning: true, status: 'idle' });
});

test('archiving a session stops it and takes it out of the list, and deleting its chat deletes it', async ({ llm }) => {
    let parent = await newChat(llm);
    await setScenarios([{ regex: 'tidy-task', rounds: [{ text: 'ok', delayMs: 8000 }] }]);
    let keep = await start(llm, parent, 'Keep', 'tidy-task 1');
    let gone = await start(llm, parent, 'Gone', 'tidy-task 2');
    await reopen(llm);
    await expect(llm.locator('.llma-thread-child')).toHaveCount(2);

    let archived = await api(llm, 'LLMAssistantArchiveSession', { threadId: gone });
    expect(archived.success).toBe(true);
    await expect.poll(async () => (await thread(llm, gone)).sessionStatus).toBe('archived');
    await reopen(llm);
    await expect(llm.locator('.llma-thread-child')).toHaveCount(1);
    // An archived session takes no more messages and does not come back when a turn that was ending finishes.
    let refused = await run(llm, parent, 'session_send', { id: gone, message: 'hello again' });
    expect(refused).toMatchObject({ success: false, error: 'That session is archived.' });
    // A chat that is not a session cannot be archived this way.
    expect((await api(llm, 'LLMAssistantArchiveSession', { threadId: parent })).success).toBe(false);

    // Deleting the chat deletes what it started, and stops what was working.
    let deleted = await api(llm, 'LLMAssistantDeleteThread', { threadId: parent });
    expect(deleted.success).toBe(true);
    expect((await api(llm, 'LLMAssistantGetThread', { threadId: keep })).success).toBe(false);
    expect((await api(llm, 'LLMAssistantGetThread', { threadId: gone })).success).toBe(false);
    expect(((await api(llm, 'LLMAssistantGetThreads')).threads as any[]).length).toBe(0);
});

test('a chat can reach only the sessions it started', async ({ llm }) => {
    let one = await newChat(llm);
    let two = await newChat(llm);
    await setScenarios([{ regex: 'own-task', rounds: [{ text: 'ok' }] }]);
    let child = await start(llm, one, 'Mine', 'own-task');
    for (let tool of ['session_read', 'session_wait', 'session_send', 'session_stop', 'session_archive']) {
        let result = await run(llm, two, tool, { id: child, message: 'hi', timeoutSeconds: 1 });
        expect(result.success, tool).toBe(false);
        expect(result.error, tool).toContain('There is no session');
    }
    // Nor a chat that is not a session at all, nor a made-up id.
    expect((await run(llm, one, 'session_read', { id: two })).success).toBe(false);
    expect((await run(llm, one, 'session_read', { id: 'nope' })).success).toBe(false);
    expect((await run(llm, two, 'session_list')).sessions).toEqual([]);
    // And the session tools do nothing without a saved chat to speak from.
    let reply = await api(llm, 'LLMAssistantExecuteTool', { toolId: 'session_list', arguments: {} });
    expect(reply.result.success).toBe(false);
    expect(reply.result.error).toContain('saved chat');
    expect((await thread(llm, child)).sessionStatus).not.toBe('archived');
});

test('bad requests are refused with a reason', async ({ llm }) => {
    let parent = await newChat(llm);
    expect((await run(llm, parent, 'session_start', { title: 'No prompt' })).error).toContain('Missing required parameter: prompt');
    expect((await run(llm, parent, 'session_start', { title: 'Bad model', prompt: 'x', model: 'no-such-model' })).error).toContain('There is no model');
    expect((await run(llm, parent, 'session_start', { title: 'Bad assistant', prompt: 'x', model: 'mock-model', assistantId: 'no-such-assistant' })).error).toContain('There is no assistant');
    expect((await run(llm, parent, 'session_start', { title: 'Long', prompt: 'x'.repeat(20_001), model: 'mock-model' })).error).toContain('too long');
    expect((await sessionsOf(llm, parent)).length).toBe(0);
});

test('the list keeps its nesting after a reload, and a session folds away under its chat', async ({ llm }) => {
    let parent = await newChat(llm);
    await setScenarios([{ regex: 'fold-task', rounds: [{ text: 'ok' }] }]);
    let one = await start(llm, parent, 'Fold one', 'fold-task 1');
    await start(llm, parent, 'Fold two', 'fold-task 2');
    await waitStatus(llm, one, 'done');
    await reopen(llm);
    await expect(llm.locator('.llma-thread-child')).toHaveCount(2);
    await expect(llm.locator('.llma-session-count')).toHaveText('2');
    await llm.locator('.llma-session-toggle').click();
    await expect(llm.locator('.llma-thread-child')).toHaveCount(0);
    await expect(llm.locator('.llma-session-count')).toHaveText('2');
    await llm.locator('.llma-session-toggle').click();
    await expect(llm.locator('.llma-thread-child')).toHaveCount(2);
});

test('opening a session shows who started it, and the user can talk to it', async ({ llm }) => {
    let parent = await newChat(llm);
    await setScenarios([
        { match: 'talk-task', rounds: [{ text: 'Ready.' }] },
        { match: 'a follow up', rounds: [{ text: 'Followed up.' }] },
    ]);
    let child = await start(llm, parent, 'Talker', 'talk-task');
    await waitStatus(llm, child, 'done');
    await reopen(llm);
    await llm.locator('.llma-thread-child').click();
    await expect(llm.locator('#llma-session-banner')).toContainText('Started by');
    await expect(llm.locator('#llma-session-banner')).toContainText('Done');
    await expect(llm.locator('#llma-session-banner button', { hasText: 'Archive' })).toBeVisible();
    await expect(llm.locator('#llma-session-banner button', { hasText: 'Stop' })).toHaveCount(0);

    await send(llm, 'a follow up');
    await expect(lastAssistant(llm)).toContainText('Followed up.');
    // The user's own turn in the session reports to the chat too.
    await expect.poll(async () => (await thread(llm, child)).lastReport).toBe('Followed up.');
    expect((await thread(llm, child)).sessionStatus).toBe('done');
    // A chat that is not a session has no banner.
    await llm.locator('#llma-thread-list .llma-thread-item[data-depth="0"]').click();
    await expect(llm.locator('#llma-session-banner')).toHaveCount(0);
});

test('a message the user sends to a working session stops it first and then lands after what it had', async ({ llm }) => {
    let parent = await newChat(llm);
    await setScenarios([
        { match: 'busy-task', rounds: [{ text: 'Partial work.', delayMs: 6000 }] },
        { match: 'change of plan', rounds: [{ text: 'New plan.' }] },
    ]);
    let child = await start(llm, parent, 'Busy', 'busy-task');
    await waitStatus(llm, child, 'running');
    await reopen(llm);
    await llm.locator('.llma-thread-child').click();
    await expect(llm.locator('#llma-session-banner button', { hasText: 'Stop' })).toBeVisible();
    await send(llm, 'change of plan');
    await expect(lastAssistant(llm)).toContainText('New plan.');
    let messages = (await thread(llm, child)).messages;
    expect(messages.at(-2).content).toBe('change of plan');
    expect(messages.at(-1).content).toBe('New plan.');
});

test('with the tools switched off, no session tools are offered', async ({ llm }) => {
    // Off by default: with the tools disabled globally a chat is not offered them, whatever its assistant lists.
    await resetSessionTools(llm);
    await setScenarios([{ regex: '.*', rounds: [{ text: 'ok' }] }]);
    await send(llm, 'hello');
    await expect(lastAssistant(llm)).toContainText('ok');
    let tools = (await mockRequests()).flatMap(r => (r.tools ?? []).map((t: any) => t.function.name));
    expect(tools.filter((n: string) => n.startsWith('session_'))).toEqual([]);
    await enableSessionTools(llm);
});

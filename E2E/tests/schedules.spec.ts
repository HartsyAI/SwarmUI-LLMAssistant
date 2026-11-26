import { test as base, expect, send, lastAssistant, setScenarios, mockRequests, api, turn, enableScheduleTools, resetScheduleTools, clearSchedules, reopen } from './fixtures';
import type { Page } from '@playwright/test';

// Schedules: prompts that run later, on a timetable, as sessions under a chat of their own.

const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await enableScheduleTools(llm);
        await use(llm);
        await clearSchedules(llm);
        await resetScheduleTools(llm);
    }
});

/** A schedule as the Schedules tab would save it: every hour, off unless asked, on the mock model. */
function schedule(title: string, prompt: string, extra: Record<string, unknown> = {}) {
    return { title, prompt, model: 'mock-model', trigger: { kind: 'every', everyMinutes: 60 }, approvalMode: 'auto', enabled: false, ...extra };
}

/** Saves a schedule and returns its id. */
async function save(page: Page, request: Record<string, unknown>): Promise<string> {
    let reply = await api(page, 'LLMAssistantSaveSchedule', { schedule: request });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    return reply.id;
}

/** The schedule as the server lists it. */
async function get(page: Page, id: string): Promise<any> {
    return ((await api(page, 'LLMAssistantGetSchedules')).schedules as any[]).find(s => s.id == id);
}

/** The chat as the server stores it. */
async function thread(page: Page, id: string): Promise<any> {
    return (await api(page, 'LLMAssistantGetThread', { threadId: id })).thread;
}

/** Runs a schedule now and returns what the server said. */
async function runNow(page: Page, id: string): Promise<any> {
    let reply = await api(page, 'LLMAssistantRunSchedule', { scheduleId: id });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    return reply;
}

/** Waits for a chat's session status, polling the server. */
async function waitStatus(page: Page, id: string, status: string, timeout = 30_000) {
    await expect.poll(async () => (await thread(page, id))?.sessionStatus, { timeout }).toBe(status);
}

/** Runs a tool the way the "/" picker does, from a chat. */
async function run(page: Page, chat: string, toolId: string, args: Record<string, unknown> = {}): Promise<any> {
    let reply = await api(page, 'LLMAssistantExecuteTool', { toolId, threadId: chat, arguments: args });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    return reply.result;
}

/** Makes a chat and returns its id. */
async function newChat(page: Page): Promise<string> {
    let created = await api(page, 'LLMAssistantCreateThread', { assistantId: 'default' });
    expect(created.success).toBe(true);
    return created.thread.id;
}

async function openSchedulesTab(page: Page) {
    await page.locator('#llma-settings-btn').click();
    await page.locator('.llma-modal-tab[data-tab="schedules"]').click();
}

test('the Schedules tab makes a schedule, shows when it runs, and runs it now', async ({ llm }) => {
    await setScenarios([{ match: 'ui-task', rounds: [{ text: 'Summary: nothing new.' }] }]);
    await openSchedulesTab(llm);
    await expect(llm.locator('#llma-schedules-list')).toContainText('No schedules yet');
    await llm.locator('#llma-schedules-add').click();

    await llm.locator('#llma-sched-title').fill('Morning digest');
    await llm.locator('#llma-sched-prompt').fill('ui-task: summarize the news');
    await llm.locator('#llma-sched-model').selectOption('mock-model');
    // "Every" is the starting kind; the other kinds show their own fields.
    await expect(llm.locator('#llma-sched-every')).toBeVisible();
    await expect(llm.locator('#llma-sched-cron')).toBeHidden();
    await llm.locator('#llma-sched-kind').selectOption('weekly');
    await expect(llm.locator('#llma-sched-time')).toBeVisible();
    await expect(llm.locator('#llma-sched-weekday')).toBeVisible();
    await llm.locator('#llma-sched-time').fill('09:30');
    await llm.locator('#llma-sched-weekday').selectOption('1');
    await llm.locator('#llma-sched-zone').fill('UTC');
    await llm.locator('#llma-sched-preview').click();
    await expect(llm.locator('#llma-sched-preview-out')).toContainText('Next runs');
    // A trigger the server refuses says why, in the editor.
    await llm.locator('#llma-sched-kind').selectOption('cron');
    await llm.locator('#llma-sched-cron').fill('* * * * *');
    await llm.locator('#llma-sched-preview').click();
    await expect(llm.locator('#llma-sched-preview-out')).toContainText('at least 5 minutes');
    // A one-time schedule takes a date and time in this browser's zone.
    await llm.locator('#llma-sched-kind').selectOption('once');
    await expect(llm.locator('#llma-sched-once')).toBeVisible();
    await expect(llm.locator('#llma-sched-time')).toBeHidden();
    await llm.locator('#llma-sched-once').fill('2020-01-01 09:00');
    await llm.locator('#llma-sched-preview').click();
    await expect(llm.locator('#llma-sched-preview-out')).toContainText('It never runs');
    await llm.locator('#llma-sched-kind').selectOption('weekly');
    await llm.locator('#llma-sched-save').click();

    let row = llm.locator('#llma-schedules-list li');
    await expect(row).toHaveCount(1);
    await expect(row).toContainText('Morning digest');
    await expect(row).toContainText('cron 30 9 * * 1 (UTC)');
    await expect(row).toContainText('Next:');
    await expect(row).toContainText('Never run');
    let stored = (await api(llm, 'LLMAssistantGetSchedules')).schedules[0];
    expect(stored).toMatchObject({ enabled: true, source: 'user', approvalMode: 'ask', model: 'mock-model', timezone: 'UTC' });
    expect(stored.trigger).toEqual({ kind: 'cron', cron: '30 9 * * 1' });

    // Run now: a chat for the schedule appears with the run under it, and the run reports back.
    await row.locator('.llma-schedule-run').click();
    await expect.poll(async () => (await get(llm, stored.id)).runCount).toBe(1);
    let after = await get(llm, stored.id);
    await expect.poll(async () => (await thread(llm, after.lastThreadId)).sessionStatus, { timeout: 30_000 }).toBe('done');
    expect((await thread(llm, after.lastThreadId)).lastReport).toBe('Summary: nothing new.');
    // It is the "Schedule: ..." chat that holds it, and that chat is marked.
    let host = await thread(llm, after.threadId);
    expect(host).toMatchObject({ title: 'Schedule: Morning digest', scheduleId: stored.id });
    expect((await thread(llm, after.lastThreadId)).parentThreadId).toBe(after.threadId);
    // The next run is where it was: Run now does not move it.
    expect(after.nextRunAt).toBe(stored.nextRunAt);

    await reopen(llm);
    await expect(llm.locator('.llma-thread-item[data-depth="0"] .llma-thread-name', { hasText: 'Schedule: Morning digest' })).toBeVisible();
    await expect(llm.locator('.llma-thread-item[data-depth="0"]', { hasText: 'Schedule: Morning digest' }).locator('.badge', { hasText: 'schedule' })).toBeVisible();
    await expect(llm.locator('.llma-thread-child .llma-thread-name')).toContainText('Morning digest · ');
    await expect(llm.locator('.llma-thread-child .llma-session-dot[data-status="done"]')).toHaveCount(1);

    // Back in the tab: the row says how the last run went, and switching it off clears its next time.
    await openSchedulesTab(llm);
    await expect(llm.locator('#llma-schedules-list li')).toContainText('Done');
    await llm.locator('#llma-schedules-list .llma-schedule-switch').uncheck();
    await expect(llm.locator('#llma-schedules-list li')).toContainText('Off');
    expect((await get(llm, stored.id)).nextRunAt).toBeUndefined();
});

test('a schedule that is due starts its own run, and a one-time schedule switches itself off', async ({ llm }) => {
    await setScenarios([{ match: 'due-task', rounds: [{ text: 'Ran on time.' }] }]);
    let at = new Date(Date.now() + 4000).toISOString();
    let id = await save(llm, schedule('Once soon', 'due-task', { trigger: { kind: 'at', at }, enabled: true }));
    expect((await get(llm, id)).nextRunAt).toBeTruthy();
    // The scheduler looks every few seconds.
    await expect.poll(async () => (await get(llm, id)).runCount, { timeout: 45_000 }).toBe(1);
    let done = await get(llm, id);
    expect(done.enabled).toBe(false);
    expect(done.nextRunAt).toBeUndefined();
    await waitStatus(llm, done.lastThreadId, 'done');
    expect((await thread(llm, done.lastThreadId)).lastReport).toBe('Ran on time.');
    // It does not run again.
    await new Promise(r => setTimeout(r, 11_000));
    expect((await get(llm, id)).runCount).toBe(1);
});

test('a due time that comes while the last run is still working is skipped, and the schedule says why', async ({ llm }) => {
    await setScenarios([{ match: 'slow-sched', rounds: [{ text: 'Late.', delayMs: 20_000 }] }]);
    let id = await save(llm, schedule('Slow one', 'slow-sched'));
    let first = await runNow(llm, id);
    expect(first.started).toBe(true);
    await waitStatus(llm, first.threadId, 'running');
    let second = await runNow(llm, id);
    expect(second).toMatchObject({ started: false, reason: 'The previous run is still working.' });
    let listed = await get(llm, id);
    expect(listed.lastSkipped).toBe('The previous run is still working.');
    expect(listed.runCount).toBe(1);
    expect(listed.lastStatus).toBe('running');
    // Only one run exists.
    expect(((await api(llm, 'LLMAssistantGetThreads')).threads as any[]).filter(t => t.parentThreadId == listed.threadId)).toHaveLength(1);
});

test('only the last few runs are kept', async ({ llm }) => {
    await setScenarios([{ match: 'keep-task', rounds: [{ text: 'ok' }] }]);
    let id = await save(llm, schedule('Often', 'keep-task'));
    for (let i = 1; i <= 8; i++) {
        let started = await runNow(llm, id);
        expect(started.started, JSON.stringify(started)).toBe(true);
        await waitStatus(llm, started.threadId, 'done');
    }
    let listed = await get(llm, id);
    expect(listed.runCount).toBe(8);
    let runs = ((await api(llm, 'LLMAssistantGetThreads')).threads as any[]).filter(t => t.parentThreadId == listed.threadId);
    // Six kept (the oldest were deleted as new ones started), and the newest is among them.
    expect(runs.length).toBe(6);
    expect(runs.map(r => r.id)).toContain(listed.lastThreadId);
});

test('a run has only the tools saved on the schedule, never the schedule tools, and cannot make schedules', async ({ llm }) => {
    await setScenarios([{ match: 'tools-task', rounds: [{ toolCalls: [{ name: 'schedule_create', arguments: { title: 'Loop', prompt: 'again', everyMinutes: 10 } }] }, { text: 'Tried.' }] }]);
    let id = await save(llm, schedule('Narrow', 'tools-task', { toolIds: ['web_search', 'schedule_create', 'schedule_cancel', 'session_start'] }));
    let stored = await get(llm, id);
    // Saved: the tools it asked for, minus the schedule tools.
    expect(stored.toolIds).toEqual(['session_start', 'web_search']);
    let started = await runNow(llm, id);
    await waitStatus(llm, started.threadId, 'done');
    let run = await thread(llm, started.threadId);
    // A run is as deep as sessions go, so it has no session or schedule tool either.
    expect(run.sessionToolIds).toEqual(['web_search']);
    let call = run.messages.find((m: any) => m.role == 'assistant').toolCalls[0];
    expect(call.result.success).toBe(false);
    expect(call.result.error).toContain('not available to this session');
    expect((await api(llm, 'LLMAssistantGetSchedules')).schedules).toHaveLength(1);
});

test('what the assistant makes is saved off and is its own: the user turns it on, and it cannot see or cancel the user\'s', async ({ llm }) => {
    let chat = await newChat(llm);
    let mine = await save(llm, schedule('The user\'s own', 'x'));
    // As a tool call from the chat: the schedule is made but not switched on.
    let made = await run(llm, chat, 'schedule_create', { title: 'From the assistant', prompt: 'check the feed', everyMinutes: 30, model: 'mock-model' });
    expect(made).toMatchObject({ success: true, enabled: false });
    expect(made.note).toContain('switched off');
    expect(made.when).toBe('every 30 minutes');
    expect(made.nextRunAt).toBeUndefined();
    let stored = await get(llm, made.id);
    expect(stored).toMatchObject({ source: 'assistant', createdBy: chat, enabled: false });

    // It sees only what its chat made.
    let listed = await run(llm, chat, 'schedule_list');
    expect(listed.schedules.map((s: any) => s.title)).toEqual(['From the assistant']);
    // A schedule of the user's, or an unknown id, cannot be cancelled by it; its own can.
    expect((await run(llm, chat, 'schedule_cancel', { id: mine })).error).toContain('There is no schedule');
    expect((await run(llm, chat, 'schedule_cancel', { id: 'nope' })).success).toBe(false);
    expect(await get(llm, mine)).toBeTruthy();
    // Another chat does not see it either.
    let other = await newChat(llm);
    expect((await run(llm, other, 'schedule_list')).schedules).toEqual([]);
    expect((await run(llm, other, 'schedule_cancel', { id: made.id })).success).toBe(false);

    // The user sees it in the tab, marked, and turning it on is their act: it keeps its source and gets a next time.
    await reopen(llm);
    await openSchedulesTab(llm);
    let row = llm.locator('#llma-schedules-list li', { hasText: 'From the assistant' });
    await expect(row.locator('.badge', { hasText: 'From the assistant' })).toBeVisible();
    await expect(row).toContainText('Off');
    await row.locator('.llma-schedule-switch').check();
    await expect(row).toContainText('Next:');
    expect(await get(llm, made.id)).toMatchObject({ source: 'assistant', enabled: true });

    expect((await run(llm, chat, 'schedule_cancel', { id: made.id })).success).toBe(true);
    expect(await get(llm, made.id)).toBeUndefined();
});

test('the model makes a schedule in a chat turn, and it is saved off', async ({ llm }) => {
    await newChat(llm);
    await setScenarios([turn('plan a daily job', [{ name: 'schedule_create', arguments: { title: 'Daily check', prompt: 'look at the logs', cron: '0 8 * * *', timezone: 'Europe/Paris', approvalMode: 'auto', model: 'mock-model' } }], 'Saved, waiting for you to turn it on.')]);
    await reopen(llm);
    await llm.locator('.llma-thread-item').first().click();
    await send(llm, 'plan a daily job');
    await expect(lastAssistant(llm)).toContainText('waiting for you');
    let made = ((await api(llm, 'LLMAssistantGetSchedules')).schedules as any[]).find(s => s.title == 'Daily check');
    expect(made).toBeTruthy();
    expect(made).toMatchObject({ enabled: false, source: 'assistant', timezone: 'Europe/Paris', approvalMode: 'auto' });
});

test('a schedule the model makes is no looser about approvals than the chat it was made in', async ({ llm }) => {
    let chat = await newChat(llm);
    await api(llm, 'LLMAssistantSetThreadApprovalMode', { threadId: chat, mode: 'ask' });
    let made = await run(llm, chat, 'schedule_create', { title: 'Careful', prompt: 'x', everyMinutes: 60, approvalMode: 'auto', model: 'mock-model' });
    expect((await get(llm, made.id)).approvalMode).toBe('ask');
    let plan = await run(llm, chat, 'schedule_create', { title: 'Stricter', prompt: 'x', everyMinutes: 60, approvalMode: 'plan', model: 'mock-model' });
    expect((await get(llm, plan.id)).approvalMode).toBe('plan');
});

test('bad schedules are refused with a reason, and a user has a limit', async ({ llm }) => {
    let refuse = async (extra: Record<string, unknown>) => (await api(llm, 'LLMAssistantSaveSchedule', { schedule: schedule('Bad', 'x', extra) })).error;
    expect(await refuse({ trigger: { kind: 'every', everyMinutes: 1 } })).toContain('minutes');
    expect(await refuse({ trigger: { kind: 'cron', cron: '* * * * *' } })).toContain('at least 5 minutes');
    expect(await refuse({ trigger: { kind: 'cron', cron: '0 0 * *' } })).toContain('five fields');
    expect(await refuse({ trigger: { kind: 'cron', cron: '0 9 * * *' }, timezone: 'Mars/Olympus' })).toContain('Mars/Olympus');
    expect(await refuse({ trigger: { kind: 'at', at: '2020-01-01T00:00:00Z' }, enabled: true })).toContain('already passed');
    expect(await refuse({ trigger: { kind: 'weekly' } })).toContain('every, cron or at');
    expect(await refuse({ prompt: '' })).toContain('required');
    expect(await refuse({ assistantId: 'no-such-assistant' })).toContain('There is no assistant');
    expect((await api(llm, 'LLMAssistantSaveSchedule', { schedule: { ...schedule('Edit', 'x'), id: 'nope' } })).error).toContain('no such schedule');
    expect((await api(llm, 'LLMAssistantSaveSchedule', { schedule: 'not json' })).error).toContain('object or a JSON string');
    expect((await api(llm, 'LLMAssistantRunSchedule', { scheduleId: 'nope' })).success).toBe(false);
    expect((await api(llm, 'LLMAssistantDeleteSchedule', { scheduleId: 'nope' })).success).toBe(false);
    expect((await api(llm, 'LLMAssistantGetSchedules')).schedules).toEqual([]);

    for (let i = 1; i <= 20; i++) {
        await save(llm, schedule(`S${i}`, 'x'));
    }
    let twentyFirst = await api(llm, 'LLMAssistantSaveSchedule', { schedule: schedule('One too many', 'x') });
    expect(twentyFirst.success).toBe(false);
    expect(twentyFirst.error).toContain('at most 20');
});

test('the preview says when a trigger runs, in its own time zone', async ({ llm }) => {
    let reply = await api(llm, 'LLMAssistantPreviewSchedule', { trigger: { kind: 'cron', cron: '0 9 * * *' }, timezone: 'America/New_York', count: 3 });
    expect(reply.success).toBe(true);
    expect(reply.zone).toBe('America/New_York');
    expect(reply.times).toHaveLength(3);
    for (let time of reply.times) {
        expect(time.local).toMatch(/ 09:00$/);
    }
    let bad = await api(llm, 'LLMAssistantPreviewSchedule', { trigger: { kind: 'cron', cron: '* * * * *' } });
    expect(bad.success).toBe(false);
    expect((await api(llm, 'LLMAssistantPreviewSchedule', { trigger: { kind: 'at', at: '2020-01-01T00:00:00Z' } })).times).toEqual([]);
});

test('a run that waits for the user does not hold one of the places for working sessions', async ({ llm }) => {
    let chat = await newChat(llm);
    await setScenarios([
        { match: 'wait-task', rounds: [{ toolCalls: [{ name: 'file_write', arguments: { path: 'sched.txt', content: 'x', overwrite: true } }] }, { text: 'Wrote.' }] },
        { regex: 'busy-', rounds: [{ text: 'ok', delayMs: 12_000 }] },
    ]);
    let id = await save(llm, schedule('Needs me', 'wait-task', { approvalMode: 'ask' }));
    let started = await runNow(llm, id);
    await waitStatus(llm, started.threadId, 'awaiting_approval');
    // Three more sessions take all three places; the one waiting for the user does not count, so none of them has to queue.
    for (let i = 1; i <= 3; i++) {
        await run(llm, chat, 'session_start', { title: `Busy ${i}`, prompt: `busy-${i}`, model: 'mock-model' });
    }
    await expect.poll(async () => ((await api(llm, 'LLMAssistantGetThreads')).threads as any[]).filter(t => t.sessionStatus == 'running').length).toBe(3);
    expect(((await api(llm, 'LLMAssistantGetThreads')).threads as any[]).filter(t => t.sessionStatus == 'queued')).toHaveLength(0);
    expect((await thread(llm, started.threadId)).sessionStatus).toBe('awaiting_approval');
});

test('a schedule whose chat was deleted makes a new one, and deleting the schedule leaves its runs', async ({ llm }) => {
    await setScenarios([{ match: 'host-task', rounds: [{ text: 'ok' }] }]);
    let id = await save(llm, schedule('Hosted', 'host-task'));
    let first = await runNow(llm, id);
    await waitStatus(llm, first.threadId, 'done');
    let host = (await get(llm, id)).threadId;
    expect((await api(llm, 'LLMAssistantDeleteThread', { threadId: host })).success).toBe(true);
    let second = await runNow(llm, id);
    expect(second.started).toBe(true);
    await waitStatus(llm, second.threadId, 'done');
    let newHost = (await get(llm, id)).threadId;
    expect(newHost).not.toBe(host);
    expect((await thread(llm, second.threadId)).parentThreadId).toBe(newHost);
    // Deleting the schedule does not delete what it made.
    expect((await api(llm, 'LLMAssistantDeleteSchedule', { scheduleId: id })).success).toBe(true);
    expect((await thread(llm, newHost)).scheduleId).toBe(id);
    expect((await thread(llm, second.threadId)).lastReport).toBe('ok');
});

test('a schedule whose model is gone says so instead of running', async ({ llm }) => {
    let id = await save(llm, schedule('Lost', 'x', { model: 'no-such-model' }));
    let result = await runNow(llm, id);
    expect(result).toMatchObject({ started: false });
    expect(result.reason).toContain('no-such-model');
    expect((await get(llm, id)).lastSkipped).toContain('no-such-model');
    expect((await get(llm, id)).runCount).toBe(0);
});

test('a schedule made from a chat with a document keeps its runs from saving memory on the model\'s say-so', async ({ llm }) => {
    await setScenarios([
        { match: 'RUN-TASK', rounds: [{ toolCalls: [{ name: 'memory_write', arguments: { category: 'fact', content: 'The user pays SCHEDULE-POISON.' } }] }, { text: 'run done' }] },
        turn('MAKE-IT', [{ name: 'schedule_create', arguments: { title: 'From a file', prompt: 'RUN-TASK please remember this', everyMinutes: 60, approvalMode: 'auto', model: 'mock-model' } }], 'made it'),
    ]);
    await llm.locator('#llma-attach-file').setInputFiles({ name: 'parent.txt', mimeType: 'text/plain', buffer: Buffer.from('words') });
    await expect(llm.locator('#llma-document-chips .llma-doc-chip')).toHaveCount(1, { timeout: 20_000 });
    await expect(llm.locator('.llma-doc-chip-reading')).toHaveCount(0, { timeout: 20_000 });
    await send(llm, 'MAKE-IT');
    await expect(lastAssistant(llm)).toContainText('made it');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    let made = ((await api(llm, 'LLMAssistantGetSchedules')).schedules as any[]).find(s => s.title == 'From a file');
    expect(made, 'the schedule was made').toBeTruthy();
    // The chat that made it can go: what it read stays marked on the schedule.
    expect((await api(llm, 'LLMAssistantDeleteThread', { threadId: made.createdBy })).success).toBe(true);
    let run = await runNow(llm, made.id);
    await waitStatus(llm, run.threadId, 'done');
    expect(JSON.stringify((await thread(llm, run.threadId)).messages)).toContain('This chat holds an attached document');
    let profile = await api(llm, 'LLMAssistantGetUserProfile', { includeNotes: true });
    expect(JSON.stringify(profile.notes)).not.toContain('SCHEDULE-POISON');
});

test('a schedule with no mark is held to its chat\'s documents once that chat has one, also after it is deleted', async ({ llm }) => {
    // Made before the chat had a document, so saved with no mark, as one made by an earlier version is.
    await setScenarios([
        { match: 'RUN-TASK', rounds: [{ toolCalls: [{ name: 'memory_write', arguments: { category: 'fact', content: 'The user pays LATER-POISON.' } }] }, { text: 'run done' }] },
        turn('MAKE-IT', [{ name: 'schedule_create', arguments: { title: 'Before the file', prompt: 'RUN-TASK please remember this', everyMinutes: 60, approvalMode: 'auto', model: 'mock-model' } }], 'made it'),
        { regex: '.*', rounds: [{ text: 'read it' }] },
    ]);
    await send(llm, 'MAKE-IT');
    await expect(lastAssistant(llm)).toContainText('made it');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    let made = ((await api(llm, 'LLMAssistantGetSchedules')).schedules as any[]).find(s => s.title == 'Before the file');
    expect(made, 'the schedule was made').toBeTruthy();
    await llm.locator('#llma-attach-file').setInputFiles({ name: 'later.txt', mimeType: 'text/plain', buffer: Buffer.from('words') });
    await expect(llm.locator('#llma-document-chips .llma-doc-chip')).toHaveCount(1, { timeout: 20_000 });
    await expect(llm.locator('.llma-doc-chip-reading')).toHaveCount(0, { timeout: 20_000 });
    await send(llm, 'here is a file');
    await expect(lastAssistant(llm)).toContainText('read it');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    for (let creatorThere of [true, false]) {
        if (!creatorThere) {
            expect((await api(llm, 'LLMAssistantDeleteThread', { threadId: made.createdBy })).success).toBe(true);
        }
        let run = await runNow(llm, made.id);
        await waitStatus(llm, run.threadId, 'done');
        expect(JSON.stringify((await thread(llm, run.threadId)).messages), `creator there: ${creatorThere}`).toContain('This chat holds an attached document');
    }
    let profile = await api(llm, 'LLMAssistantGetUserProfile', { includeNotes: true });
    expect(JSON.stringify(profile.notes)).not.toContain('LATER-POISON');
});

test('a run that is skipped marks a schedule too, so deleting its chat before the next run loses nothing', async ({ llm }) => {
    // Made before the chat had a document, with a model no backend has, so the first run is skipped.
    await setScenarios([
        { match: 'RUN-TASK', rounds: [{ toolCalls: [{ name: 'memory_write', arguments: { category: 'fact', content: 'The user pays SKIPPED-POISON.' } }] }, { text: 'run done' }] },
        turn('MAKE-IT', [{ name: 'schedule_create', arguments: { title: 'Skipped first', prompt: 'RUN-TASK please remember this', everyMinutes: 60, approvalMode: 'auto', model: 'no-such-model' } }], 'made it'),
        { regex: '.*', rounds: [{ text: 'read it' }] },
    ]);
    await send(llm, 'MAKE-IT');
    await expect(lastAssistant(llm)).toContainText('made it');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    let made = ((await api(llm, 'LLMAssistantGetSchedules')).schedules as any[]).find(s => s.title == 'Skipped first');
    expect(made, 'the schedule was made').toBeTruthy();
    await llm.locator('#llma-attach-file').setInputFiles({ name: 'later.txt', mimeType: 'text/plain', buffer: Buffer.from('words') });
    await expect(llm.locator('#llma-document-chips .llma-doc-chip')).toHaveCount(1, { timeout: 20_000 });
    await expect(llm.locator('.llma-doc-chip-reading')).toHaveCount(0, { timeout: 20_000 });
    await send(llm, 'here is a file');
    await expect(lastAssistant(llm)).toContainText('read it');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    expect(await runNow(llm, made.id)).toMatchObject({ started: false, reason: expect.stringContaining('not available') });
    expect((await api(llm, 'LLMAssistantDeleteThread', { threadId: made.createdBy })).success).toBe(true);
    await save(llm, { ...schedule('Skipped first', 'RUN-TASK please remember this', { enabled: false }), id: made.id });
    let run = await runNow(llm, made.id);
    await waitStatus(llm, run.threadId, 'done');
    expect(JSON.stringify((await thread(llm, run.threadId)).messages)).toContain('This chat holds an attached document');
    let profile = await api(llm, 'LLMAssistantGetUserProfile', { includeNotes: true });
    expect(JSON.stringify(profile.notes)).not.toContain('SKIPPED-POISON');
});

import { test as base, expect, setScenarios, mockRequests, clearRequests, api, clearMemory } from './fixtures';
import type { Page } from '@playwright/test';

// The memory extractor over the API alone: no UI is touched. Settings, the extraction itself on an existing chat (made by the headless route), its opt-outs, history, undo and Stop.

const MARKER = 'Memory extraction request.';

const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await use(llm);
        await api(llm, 'LLMAssistantSetMemorySettings', { enabled: false, model: '', neverSave: [] });
        let current = (await api(llm, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant;
        current.memoryExtraction = true;
        await api(llm, 'LLMAssistantSaveAssistant', { assistant: current, scope: 'personal' });
        await clearMemory(llm);
    }
});

const on = { enabled: true, model: 'mock-model-b', neverSave: [] as string[] };
const addNurse = [{ op: 'add', kind: 'fact', text: 'Works as a nurse in Lyon.' }];
const extractor = (ops: unknown, extra: Record<string, unknown> = {}) => ({ model: 'mock-model-b', match: MARKER, rounds: [{ text: JSON.stringify({ ops }), ...extra }] });
const personal = 'I moved to Lyon last month and I work as a nurse.';

async function getThread(page: Page, threadId: string): Promise<any> {
    let thread = (await api(page, 'LLMAssistantGetThread', { threadId })).thread;
    return typeof thread === 'string' ? JSON.parse(thread) : thread;
}

/** A chat with one finished turn, made through the headless route (no socket, no UI). */
async function chatWith(page: Page, message = personal): Promise<{ threadId: string; userId: string; replyId: string }> {
    await setScenarios([extractor(addNurse), { match: 'I moved', rounds: [{ text: 'Lyon is lovely.' }] }, { match: 'two cats', rounds: [{ text: 'Lovely.' }] }]);
    let turn = await api(page, 'LLMAssistantRunTurn', { message });
    expect(turn.success, JSON.stringify(turn)).toBe(true);
    let messages = (await getThread(page, turn.threadId)).messages;
    return { threadId: turn.threadId, userId: messages[0].id, replyId: messages[1].id };
}

async function extractionRequests(): Promise<any[]> {
    return (await mockRequests()).filter(r => {
        let users = (r.messages ?? []).filter((m: any) => m.role === 'user');
        return users.length > 0 && String(users[users.length - 1].content).includes(MARKER);
    });
}

async function notes(page: Page): Promise<any[]> {
    return (await api(page, 'LLMAssistantGetUserProfile', { includeNotes: true })).notes;
}

test('the settings are read and set by route, validated, partial, and round-trip through the general settings', async ({ llm }) => {
    let first = await api(llm, 'LLMAssistantGetMemorySettings');
    expect(first).toMatchObject({ success: true, settings: { enabled: false, model: '', neverSave: [] } });
    expect(first.limits).toMatchObject({ maxNeverSave: 50, maxNeverSaveChars: 200, maxPerHour: 20 });

    let saved = await api(llm, 'LLMAssistantSetMemorySettings', { enabled: true, model: '  mock-model-b ', neverSave: ['divorce', '/card [0-9]+/', 'Divorce'] });
    expect(saved).toMatchObject({ success: true, settings: { enabled: true, model: 'mock-model-b', neverSave: ['divorce', '/card [0-9]+/'] } });
    // Only what is sent changes.
    let partial = await api(llm, 'LLMAssistantSetMemorySettings', { model: 'other-model' });
    expect(partial.settings).toEqual({ enabled: true, model: 'other-model', neverSave: ['divorce', '/card [0-9]+/'] });
    // The general settings route shows the same block (what the page reads).
    let general = (await api(llm, 'LLMAssistantGetSettings')).settings;
    let block = (typeof general === 'string' ? JSON.parse(general) : general).memoryExtraction;
    expect(block).toEqual({ enabled: true, model: 'other-model', neverSave: ['divorce', '/card [0-9]+/'] });

    // Bad input is refused with a reason and changes nothing.
    for (let [body, text] of [
        [{ enabled: 'yes' }, 'enabled must be'],
        [{ model: 5 }, 'model must be'],
        [{ model: 'x'.repeat(201) }, 'model must be'],
        [{ neverSave: 'divorce' }, 'neverSave must be an array'],
        [{ neverSave: ['ok', 5] }, 'neverSave must be an array'],
        [{ neverSave: [''] }, 'cannot be empty'],
        [{ neverSave: ['/(unclosed/'] }, 'valid regular expression'],
        [{ neverSave: ['x'.repeat(201)] }, 'at most 200'],
        [{ neverSave: Array.from({ length: 51 }, (_, i) => `w${i}`) }, 'at most 50']
    ] as [Record<string, unknown>, string][]) {
        let refused = await api(llm, 'LLMAssistantSetMemorySettings', body);
        expect(refused.success, JSON.stringify(body)).toBe(false);
        expect(refused.error).toContain(text);
    }
    expect((await api(llm, 'LLMAssistantGetMemorySettings')).settings).toEqual({ enabled: true, model: 'other-model', neverSave: ['divorce', '/card [0-9]+/'] });
});

test('the shared layer cannot switch the extractor on for a user', async ({ llm }) => {
    // The user's own block is set to the defaults first (a settings reset keeps it, on purpose), so only the shared write below could change what they see.
    expect((await api(llm, 'LLMAssistantSetMemorySettings', { enabled: false, model: '', neverSave: [] })).success).toBe(true);

    let shared = await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ memoryExtraction: { enabled: true, model: 'shared-model', neverSave: ['shared'] } }), scope: 'shared' });
    expect(shared.success, JSON.stringify(shared)).toBe(true);
    // The write was dropped: the merged view shows nothing of it, and the route reads the user's own block.
    let general = (await api(llm, 'LLMAssistantGetSettings')).settings;
    expect(JSON.stringify((typeof general === 'string' ? JSON.parse(general) : general).memoryExtraction ?? {})).not.toContain('shared');
    expect((await api(llm, 'LLMAssistantGetMemorySettings')).settings).toEqual({ enabled: false, model: '', neverSave: [] });
    let chat = await chatWith(llm);
    let out = await api(llm, 'LLMAssistantExtractMemory', { threadId: chat.threadId });
    expect(out).toMatchObject({ success: true, status: 'skipped' });
    expect(out.detail).toContain('switched off');
});

test('a general settings save with a stale copy of the extractor block cannot turn it back on', async ({ llm }) => {
    await api(llm, 'LLMAssistantSetMemorySettings', { enabled: true, model: 'new', neverSave: ['new'] });
    // The page (or any client) still holds the old block, and saves its settings for an unrelated reason after the user turned the switch off by route.
    await api(llm, 'LLMAssistantSetMemorySettings', { enabled: false });
    let saved = await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ memoryExtraction: { enabled: true, model: 'stale', neverSave: ['stale'] }, parameters: { temperature: 1 } }) });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
    expect((await api(llm, 'LLMAssistantGetMemorySettings')).settings).toEqual({ enabled: false, model: 'new', neverSave: ['new'] });
    // The same through a shared-scope save.
    await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ memoryExtraction: { enabled: true } }), scope: 'shared' });
    expect((await api(llm, 'LLMAssistantGetMemorySettings')).settings.enabled).toBe(false);
});

test('extracting a chat by route saves checked notes, lists them, and undoes them, all without the interface', async ({ llm }) => {
    let chat = await chatWith(llm);
    // Off by default: the same call is a skip with the reason, and no model is called.
    let off = await api(llm, 'LLMAssistantExtractMemory', { threadId: chat.threadId });
    expect(off).toMatchObject({ success: true, status: 'skipped' });
    expect(await extractionRequests()).toHaveLength(0);

    await api(llm, 'LLMAssistantSetMemorySettings', on);
    let done = await api(llm, 'LLMAssistantExtractMemory', { threadId: chat.threadId });
    expect(done, JSON.stringify(done)).toMatchObject({ success: true, status: 'saved' });
    expect(done.changes.added.map((n: any) => n.text)).toEqual(['Works as a nurse in Lyon.']);
    expect(done.changes.threadId).toBe(chat.threadId);
    let requests = await extractionRequests();
    expect(requests).toHaveLength(1);
    expect(requests[0].model).toBe('mock-model-b');
    expect(String(requests[0].messages[1].content)).toContain(personal);
    expect((await notes(llm)).map((n: any) => n.text)).toEqual(['Works as a nurse in Lyon.']);

    // The same message is not extracted twice.
    let again = await api(llm, 'LLMAssistantExtractMemory', { threadId: chat.threadId });
    expect(again).toMatchObject({ success: true, status: 'skipped' });
    expect(again.detail).toContain('already extracted');

    // History has the same change set the call returned, and undo takes it back, once.
    let history = (await api(llm, 'LLMAssistantGetMemoryChanges')).changes;
    expect(history).toHaveLength(1);
    expect(history[0]).toEqual(done.changes);
    let undone = await api(llm, 'LLMAssistantUndoMemoryChange', { changeId: done.changes.id });
    expect(undone).toMatchObject({ success: true, found: true, undone: 1, skipped: 0, refused: '' });
    expect(await notes(llm)).toHaveLength(0);
    expect((await api(llm, 'LLMAssistantGetMemoryChanges')).changes).toHaveLength(0);
    expect(await api(llm, 'LLMAssistantUndoMemoryChange', { changeId: done.changes.id })).toMatchObject({ success: true, found: false, undone: 0 });
    // A missing required argument is refused by the API layer itself (an error, no success).
    let missing = await api(llm, 'LLMAssistantUndoMemoryChange', {});
    expect(missing.success).not.toBe(true);
    expect(missing.error).toBeTruthy();
});

test('bad requests are refused with a reason', async ({ llm }) => {
    await api(llm, 'LLMAssistantSetMemorySettings', on);
    let chat = await chatWith(llm);
    let missing = await api(llm, 'LLMAssistantExtractMemory', {});
    expect(missing.success).not.toBe(true);
    expect(missing.error).toBeTruthy();
    let unknown = await api(llm, 'LLMAssistantExtractMemory', { threadId: 'no-such-chat' });
    expect(unknown.success).toBe(false);
    expect(unknown.error).toContain('not found');
    let wrongMessage = await api(llm, 'LLMAssistantExtractMemory', { threadId: chat.threadId, messageId: 'nope' });
    expect(wrongMessage.success).toBe(false);
    expect(wrongMessage.error).toContain('messageId');
    let assistantMessage = await api(llm, 'LLMAssistantExtractMemory', { threadId: chat.threadId, messageId: chat.replyId });
    expect(assistantMessage.success).toBe(false);
    expect(await extractionRequests()).toHaveLength(0);
    expect(await notes(llm)).toHaveLength(0);
});

test('a chosen message is extracted, not only the latest one', async ({ llm }) => {
    await api(llm, 'LLMAssistantSetMemorySettings', on);
    let chat = await chatWith(llm);
    await api(llm, 'LLMAssistantRunTurn', { threadId: chat.threadId, message: 'I also have two cats at home and I love them.' });
    let first = await api(llm, 'LLMAssistantExtractMemory', { threadId: chat.threadId, messageId: chat.userId });
    expect(first, JSON.stringify(first)).toMatchObject({ success: true, status: 'saved' });
    let sent = String((await extractionRequests())[0].messages[1].content);
    expect(sent).toContain(personal);
    expect(sent).not.toContain('two cats');
});

test('the chat opt-out, the assistant opt-out and the never-save list each make the route skip, with the reason', async ({ llm }) => {
    await api(llm, 'LLMAssistantSetMemorySettings', { ...on, neverSave: ['nurse'] });
    let chat = await chatWith(llm);
    // Never-save: the turn mentions "nurse", so it is not sent anywhere.
    let never = await api(llm, 'LLMAssistantExtractMemory', { threadId: chat.threadId });
    expect(never).toMatchObject({ success: true, status: 'skipped' });
    expect(never.detail).toContain('never saved');

    await api(llm, 'LLMAssistantSetMemorySettings', { neverSave: [] });
    let optedOut = await api(llm, 'LLMAssistantSetThreadMemory', { threadId: chat.threadId, enabled: false });
    expect(optedOut).toMatchObject({ success: true, memoryExtraction: false });
    let chatOff = await api(llm, 'LLMAssistantExtractMemory', { threadId: chat.threadId });
    expect(chatOff).toMatchObject({ success: true, status: 'skipped' });
    expect(chatOff.detail).toContain("don't remember");
    expect((await getThread(llm, chat.threadId)).memoryExtraction).toBe(false);
    expect(await api(llm, 'LLMAssistantSetThreadMemory', { threadId: chat.threadId })).toMatchObject({ success: true, memoryExtraction: null });
    expect((await api(llm, 'LLMAssistantSetThreadMemory', { threadId: 'no-such-chat', enabled: false })).success).toBe(false);

    // The assistant's own switch is carried by the assistant routes.
    let current = (await api(llm, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant;
    let saved = await api(llm, 'LLMAssistantSaveAssistant', { assistant: { ...current, memoryExtraction: false }, scope: 'personal' });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
    expect((await api(llm, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant.memoryExtraction).toBe(false);
    let assistantOff = await api(llm, 'LLMAssistantExtractMemory', { threadId: chat.threadId });
    expect(assistantOff).toMatchObject({ success: true, status: 'skipped' });
    expect(assistantOff.detail).toContain('assistant');
    expect(await extractionRequests()).toHaveLength(0);
    expect(await notes(llm)).toHaveLength(0);
});

test('two calls on one chat at once: the second is skipped as already running, and after Stop a new call runs', async ({ llm }) => {
    await api(llm, 'LLMAssistantSetMemorySettings', on);
    let chat = await chatWith(llm);
    await setScenarios([extractor(addNurse, { delayMs: 6000 })]);
    await clearRequests();
    let running = api(llm, 'LLMAssistantExtractMemory', { threadId: chat.threadId });
    await expect.poll(async () => (await extractionRequests()).length, { timeout: 20_000 }).toBe(1);
    let second = await api(llm, 'LLMAssistantExtractMemory', { threadId: chat.threadId });
    expect(second).toMatchObject({ success: true, status: 'skipped' });
    expect(second.detail).toContain('already running');
    expect(await extractionRequests()).toHaveLength(1);

    // Stop cancels the first one: nothing saved, and its message is not used up (nor its hourly slot).
    expect(await api(llm, 'LLMAssistantStopGeneration', { threadId: chat.threadId })).toMatchObject({ success: true, cancelled: true });
    expect(await running).toMatchObject({ success: true, status: 'cancelled' });
    expect(await notes(llm)).toHaveLength(0);
    await setScenarios([extractor(addNurse)]);
    let followUp = await api(llm, 'LLMAssistantExtractMemory', { threadId: chat.threadId });
    expect(followUp, JSON.stringify(followUp)).toMatchObject({ success: true, status: 'saved' });
    expect((await notes(llm)).map((n: any) => n.text)).toEqual(['Works as a nurse in Lyon.']);
});

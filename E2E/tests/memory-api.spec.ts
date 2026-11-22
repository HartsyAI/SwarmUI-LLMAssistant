import { test as base, expect, api, clearMemory } from './fixtures';
import type { Page } from '@playwright/test';

// The memory feature through the API alone: every route called directly with the e2e api() helper, no UI. Each route is checked for its reply shape, its input validation, that it needs a session,
// and that it takes no user address (a smuggled one is ignored). The UI flows are in memory-controls.spec.ts; both go through the same routes.
// Not covered here: a role without llm_chat, or without llm_shared_write, being refused. The harness has one (admin) user, so those two checks are by reading the route registration only.

const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await use(llm);
        await api(llm, 'LLMAssistantSetMemoryAdmin', { extractionDisabled: false });
        await api(llm, 'LLMAssistantSetMemoryControls', { savingPaused: false, recallPaused: false, unusedDays: 0 });
        await api(llm, 'LLMAssistantSetAssistantMemory', { assistantId: 'default', enabled: true, scope: 'shared' });
        await clearMemory(llm);
    }
});

const ROUTES = ['LLMAssistantListMemoryNotes', 'LLMAssistantGetMemoryNote', 'LLMAssistantAddMemoryNote', 'LLMAssistantUpdateMemoryNote', 'LLMAssistantPinMemoryNote', 'LLMAssistantDeleteMemoryNotes',
    'LLMAssistantExportMemory', 'LLMAssistantPreviewMemoryImport', 'LLMAssistantImportMemory', 'LLMAssistantGetMemoryControls', 'LLMAssistantSetMemoryControls', 'LLMAssistantSetMemoryAdmin',
    'LLMAssistantSetAssistantMemory', 'LLMAssistantGetMemoryChanges', 'LLMAssistantUndoMemoryChange', 'LLMAssistantGetUserProfile', 'LLMAssistantClearUserProfile', 'LLMAssistantSetThreadMemory'];

/** Calls a route with no session at all (what a script that forgot to log in sends). */
async function withoutSession(page: Page, route: string): Promise<any> {
    return await page.evaluate(async (r) => {
        let response = await fetch(`/API/${r}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
        return await response.json();
    }, route);
}

async function add(page: Page, text: string, extra: Record<string, unknown> = {}) {
    let reply = await api(page, 'LLMAssistantAddMemoryNote', { text, ...extra });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    return reply.note;
}

test('every memory route needs a session', async ({ llm }) => {
    for (let route of ROUTES) {
        let reply = await withoutSession(llm, route);
        expect(reply.success, route).not.toBe(true);
        expect(JSON.stringify(reply), route).toMatch(/session/i);
    }
});

test('every memory route ignores a user address in the request', async ({ llm }) => {
    let mine = await add(llm, 'Only mine');
    let other = { userId: 'someone-else', user: 'someone-else', userID: 'someone-else' };
    expect((await api(llm, 'LLMAssistantListMemoryNotes', other)).notes.map((n: any) => n.text)).toEqual(['Only mine']);
    expect((await api(llm, 'LLMAssistantGetMemoryNote', { id: mine.id, ...other })).note.text).toBe('Only mine');
    expect((await api(llm, 'LLMAssistantExportMemory', other)).export.notes).toHaveLength(1);
    expect((await api(llm, 'LLMAssistantGetUserProfile', { includeNotes: true, ...other })).notes).toHaveLength(1);
    expect((await api(llm, 'LLMAssistantGetMemoryControls', other)).success).toBe(true);
    expect((await api(llm, 'LLMAssistantGetMemoryChanges', other)).success).toBe(true);
    expect((await api(llm, 'LLMAssistantPinMemoryNote', { id: mine.id, pinned: true, ...other })).note.pinned).toBe(true);
    expect((await api(llm, 'LLMAssistantUpdateMemoryNote', { id: mine.id, text: 'Still mine', ...other })).note.text).toBe('Still mine');
    expect((await api(llm, 'LLMAssistantDeleteMemoryNotes', { ids: ['not-mine'], ...other })).deleted).toBe(0);
    expect((await api(llm, 'LLMAssistantListMemoryNotes')).total).toBe(1);
});

test('add, get, update, pin and delete a note', async ({ llm }) => {
    let note = await add(llm, '  Likes jazz  ', { kind: 'preference' });
    expect(note).toMatchObject({ text: 'Likes jazz', kind: 'preference', origin: 'user', pinned: false, slot: '', negative: false, truncated: false });
    expect(note.id).toMatch(/^[0-9a-f]{12}$/);

    expect((await api(llm, 'LLMAssistantGetMemoryNote', { id: note.id })).note.text).toBe('Likes jazz');
    expect(await api(llm, 'LLMAssistantGetMemoryNote', { id: 'nope' })).toMatchObject({ success: false, code: 'notfound' });

    let updated = await api(llm, 'LLMAssistantUpdateMemoryNote', { id: note.id, text: 'Likes bebop', kind: 'dislike' });
    expect(updated.note).toMatchObject({ text: 'Likes bebop', kind: 'preference', negative: true, origin: 'user' });
    // A field left out stays as it is.
    expect((await api(llm, 'LLMAssistantUpdateMemoryNote', { id: note.id, kind: 'fact' })).note).toMatchObject({ text: 'Likes bebop', kind: 'fact', negative: false });

    expect((await api(llm, 'LLMAssistantPinMemoryNote', { id: note.id, pinned: true })).note.pinned).toBe(true);
    expect((await api(llm, 'LLMAssistantPinMemoryNote', { id: note.id, pinned: false })).note.pinned).toBe(false);

    expect(await api(llm, 'LLMAssistantDeleteMemoryNotes', { ids: [note.id, 'unknown'] })).toMatchObject({ success: true, deleted: 1 });
    expect((await api(llm, 'LLMAssistantListMemoryNotes')).total).toBe(0);
});

test('the note routes validate their input and say why they refuse', async ({ llm }) => {
    let note = await add(llm, 'First note');
    expect(await api(llm, 'LLMAssistantAddMemoryNote', { text: '   ' })).toMatchObject({ success: false, code: 'empty' });
    expect(await api(llm, 'LLMAssistantAddMemoryNote')).toMatchObject({ success: false, code: 'empty' });
    expect(await api(llm, 'LLMAssistantAddMemoryNote', { text: 'x', kind: 'banana' })).toMatchObject({ success: false, code: 'kind' });
    expect(await api(llm, 'LLMAssistantAddMemoryNote', { text: 'x', slot: 'shoes' })).toMatchObject({ success: false, code: 'slot' });
    expect(await api(llm, 'LLMAssistantAddMemoryNote', { text: 'first   NOTE' })).toMatchObject({ success: false, code: 'duplicate' });
    let big = (await api(llm, 'LLMAssistantAddMemoryNote', { text: 'z'.repeat(5000) })).note;
    expect((await api(llm, 'LLMAssistantGetMemoryNote', { id: big.id })).note.text).toHaveLength(2000);

    let name = await add(llm, 'Sam', { slot: 'name' });
    expect(name).toMatchObject({ slot: 'name', pinned: true });
    expect(await api(llm, 'LLMAssistantAddMemoryNote', { text: 'Samuel', slot: 'name' })).toMatchObject({ success: false, code: 'slotfilled' });
    // A slot note keeps its kind and its pin; its text can change.
    expect(await api(llm, 'LLMAssistantUpdateMemoryNote', { id: name.id, kind: 'style' })).toMatchObject({ success: false, code: 'slot' });
    expect(await api(llm, 'LLMAssistantPinMemoryNote', { id: name.id, pinned: false })).toMatchObject({ success: false, code: 'slot' });
    expect((await api(llm, 'LLMAssistantUpdateMemoryNote', { id: name.id, text: 'Samantha' })).note.text).toBe('Samantha');

    expect(await api(llm, 'LLMAssistantUpdateMemoryNote', { id: note.id, text: '  ' })).toMatchObject({ success: false, code: 'empty' });
    expect(await api(llm, 'LLMAssistantUpdateMemoryNote', { id: note.id, kind: 'banana' })).toMatchObject({ success: false, code: 'kind' });
    expect(await api(llm, 'LLMAssistantUpdateMemoryNote', { id: 'nope', text: 'x' })).toMatchObject({ success: false, code: 'notfound' });
    expect(await api(llm, 'LLMAssistantUpdateMemoryNote', { text: 'x' })).toMatchObject({ success: false, code: 'notfound' });
    expect(await api(llm, 'LLMAssistantUpdateMemoryNote', { id: 'a'.repeat(100), text: 'x' })).toMatchObject({ success: false, code: 'notfound' });
    let other = await add(llm, 'Second note');
    expect(await api(llm, 'LLMAssistantUpdateMemoryNote', { id: other.id, text: 'FIRST note' })).toMatchObject({ success: false, code: 'duplicate' });

    expect(await api(llm, 'LLMAssistantPinMemoryNote', { id: note.id })).toMatchObject({ success: false });
    expect(await api(llm, 'LLMAssistantPinMemoryNote', { id: note.id, pinned: 'yes' })).toMatchObject({ success: false });
    expect(await api(llm, 'LLMAssistantPinMemoryNote', { id: 'nope', pinned: true })).toMatchObject({ success: false, code: 'notfound' });
    expect(await api(llm, 'LLMAssistantDeleteMemoryNotes')).toMatchObject({ success: false });
    expect(await api(llm, 'LLMAssistantDeleteMemoryNotes', { ids: 'x' })).toMatchObject({ success: false });
    expect((await api(llm, 'LLMAssistantDeleteMemoryNotes', { ids: [] })).deleted).toBe(0);
});

test('pinning stops at 25 and adding never drops a note when memory is full', async ({ llm }) => {
    let data = JSON.stringify({ notes: Array.from({ length: 400 }, (_, i) => ({ text: `Note number ${i}`, pinned: i < 25 })) });
    let imported = await api(llm, 'LLMAssistantImportMemory', { data });
    expect(imported.preview.addCount).toBe(400);
    expect(await api(llm, 'LLMAssistantAddMemoryNote', { text: 'One too many' })).toMatchObject({ success: false, code: 'full' });
    let list = await api(llm, 'LLMAssistantListMemoryNotes', { limit: 200 });
    expect(list.total).toBe(400);
    let unpinned = list.notes.find((n: any) => !n.pinned);
    expect(await api(llm, 'LLMAssistantPinMemoryNote', { id: unpinned.id, pinned: true })).toMatchObject({ success: false, code: 'pinlimit' });
    await api(llm, 'LLMAssistantDeleteMemoryNotes', { ids: [unpinned.id] });
    expect(await api(llm, 'LLMAssistantAddMemoryNote', { text: 'Pinned one too many', pinned: true })).toMatchObject({ success: false, code: 'pinlimit' });
    expect((await api(llm, 'LLMAssistantAddMemoryNote', { text: 'Room now' })).success).toBe(true);
});

test('the list pages with a cursor, filters by kind and search, and caps its page', async ({ llm }) => {
    await add(llm, 'Sam', { slot: 'name' });
    for (let i = 0; i < 5; i++) {
        await add(llm, `Fact number ${i}`);
    }
    await add(llm, 'Cilantro', { kind: 'dislike' });
    await add(llm, 'Green tea', { kind: 'preference' });
    await add(llm, 'Short answers', { kind: 'style' });

    let first = await api(llm, 'LLMAssistantListMemoryNotes', { limit: 3 });
    expect(first).toMatchObject({ success: true, total: 9, matched: 9, nextCursor: '3', state: 'ok' });
    expect(first.limits).toMatchObject({ maxNotes: 400, maxNoteChars: 2000, maxPinned: 25 });
    let seen = first.notes.map((n: any) => n.id);
    let cursor = first.nextCursor;
    let pages = 1;
    while (cursor) {
        let page = await api(llm, 'LLMAssistantListMemoryNotes', { limit: 3, cursor });
        seen.push(...page.notes.map((n: any) => n.id));
        cursor = page.nextCursor;
        pages++;
    }
    expect(pages).toBe(3);
    expect(new Set(seen).size).toBe(9);

    let names = async (filter: Record<string, unknown>) => (await api(llm, 'LLMAssistantListMemoryNotes', filter)).notes.map((n: any) => n.text);
    expect(await names({ kind: 'profile' })).toEqual(['Sam']);
    expect(await names({ kind: 'dislike' })).toEqual(['Cilantro']);
    expect(await names({ kind: 'style' })).toEqual(['Short answers']);
    expect(await names({ query: 'NUMBER 3' })).toEqual(['Fact number 3']);
    expect((await api(llm, 'LLMAssistantListMemoryNotes', { kind: 'fact' })).matched).toBe(5);
    expect(await names({ kind: 'pinned' })).toEqual(['Sam']);
    expect(await names({ query: 'zzz' })).toEqual([]);

    expect(await api(llm, 'LLMAssistantListMemoryNotes', { kind: 'banana' })).toMatchObject({ success: false, code: 'filter' });
    expect(await api(llm, 'LLMAssistantListMemoryNotes', { cursor: 'abc' })).toMatchObject({ success: false, code: 'cursor' });
    expect(await api(llm, 'LLMAssistantListMemoryNotes', { cursor: '-1' })).toMatchObject({ success: false, code: 'cursor' });
    // A page is capped, whatever limit is asked for, and a long text is cut with a flag.
    expect((await api(llm, 'LLMAssistantListMemoryNotes', { limit: 100000 })).notes).toHaveLength(9);
    await add(llm, 'L'.repeat(1800));
    let long = (await api(llm, 'LLMAssistantListMemoryNotes', { query: 'LLLLL' })).notes[0];
    expect(long.text).toHaveLength(500);
    expect(long.truncated).toBe(true);
    expect((await api(llm, 'LLMAssistantGetMemoryNote', { id: long.id })).note.text).toHaveLength(1800);
});

test('export and import round trip through the API, with a preview that changes nothing', async ({ llm }) => {
    await add(llm, 'Likes tea', { kind: 'preference' });
    await add(llm, 'Sam', { slot: 'name' });
    let exported = (await api(llm, 'LLMAssistantExportMemory')).export;
    expect(exported.format).toBe('llma-memory');
    expect(exported.notes).toHaveLength(2);
    expect(JSON.stringify(exported)).not.toContain('threadId');

    let again = await api(llm, 'LLMAssistantPreviewMemoryImport', { data: JSON.stringify(exported) });
    expect(again.preview).toMatchObject({ addCount: 0, duplicates: 2 });
    let file = JSON.stringify({ notes: [{ text: 'Likes tea', kind: 'preference' }, { text: 'Samuel', slot: 'name' }, { text: 'Plays <b>chess</b>' }, { text: 'My password is hunter2' }, { text: '' }, 'junk'] });
    let preview = await api(llm, 'LLMAssistantPreviewMemoryImport', { data: file });
    expect(preview.preview).toMatchObject({ addCount: 1, duplicates: 1, conflicts: 1, blocked: 1, invalid: 2, noRoom: 0, ignored: 0 });
    expect(preview.preview.add[0].text).not.toContain('<');
    expect((await api(llm, 'LLMAssistantListMemoryNotes')).total).toBe(2);

    let applied = await api(llm, 'LLMAssistantImportMemory', { data: file });
    expect(applied.preview.addCount).toBe(1);
    expect((await api(llm, 'LLMAssistantListMemoryNotes')).total).toBe(3);
    expect((await api(llm, 'LLMAssistantListMemoryNotes', { kind: 'profile' })).notes[0].text).toBe('Sam');

    for (let route of ['LLMAssistantPreviewMemoryImport', 'LLMAssistantImportMemory']) {
        expect(await api(llm, route, { data: '' })).toMatchObject({ success: false, code: 'empty' });
        expect(await api(llm, route)).toMatchObject({ success: false, code: 'empty' });
        expect(await api(llm, route, { data: '{ nope' })).toMatchObject({ success: false, code: 'not-json' });
        expect(await api(llm, route, { data: '{"x":1}' })).toMatchObject({ success: false, code: 'no-notes' });
        expect(await api(llm, route, { data: ' '.repeat(1_500_001) })).toMatchObject({ success: false, code: 'too-large' });
    }
    expect((await api(llm, 'LLMAssistantListMemoryNotes')).total).toBe(3);
});

test('pause, retention and the admin switch through the API', async ({ llm }) => {
    expect(await api(llm, 'LLMAssistantGetMemoryControls')).toMatchObject({
        success: true, pause: { saving: false, recall: false }, retention: { unusedDays: 0, min: 7, max: 3650 }, admin: { extractionDisabled: false, canWrite: true }, extractionAllowed: false
    });
    let paused = await api(llm, 'LLMAssistantSetMemoryControls', { savingPaused: true });
    expect(paused.pause).toEqual({ saving: true, recall: false });
    expect((await api(llm, 'LLMAssistantSetMemoryControls', { recallPaused: true })).pause).toEqual({ saving: true, recall: true });
    expect((await api(llm, 'LLMAssistantSetMemoryControls', { savingPaused: false, recallPaused: false })).pause).toEqual({ saving: false, recall: false });
    // A value of the wrong type is not a change; asking for nothing says so.
    expect(await api(llm, 'LLMAssistantSetMemoryControls', { savingPaused: 'yes' })).toMatchObject({ success: false });
    expect(await api(llm, 'LLMAssistantSetMemoryControls', {})).toMatchObject({ success: false });

    // Retention: 0 is off, anything else must be a whole number from 7 to 3650 (refused, not clamped), and a fresh note is not old enough to be forgotten.
    await add(llm, 'A fresh note');
    for (let bad of [3, 6, -1, 3651, 99999, 12.5, '30', true]) {
        let refused = await api(llm, 'LLMAssistantSetMemoryControls', { unusedDays: bad });
        expect(refused.success, JSON.stringify(bad)).toBe(false);
        expect(refused.error).toContain('from 7 to 3650');
    }
    expect((await api(llm, 'LLMAssistantGetMemoryControls')).retention.unusedDays).toBe(0);
    expect(await api(llm, 'LLMAssistantSetMemoryControls', { unusedDays: 7 })).toMatchObject({ success: true, retention: { unusedDays: 7 }, forgotten: 0 });
    expect((await api(llm, 'LLMAssistantSetMemoryControls', { unusedDays: 3650 })).retention.unusedDays).toBe(3650);
    expect((await api(llm, 'LLMAssistantSetMemoryControls', { unusedDays: 90 })).retention.unusedDays).toBe(90);
    expect((await api(llm, 'LLMAssistantSetMemoryControls', { unusedDays: 0 })).retention.unusedDays).toBe(0);
    expect((await api(llm, 'LLMAssistantListMemoryNotes')).total).toBe(1);

    // The admin switch (this user holds llm_shared_write): its input is checked, and a user's own settings save cannot set it.
    expect(await api(llm, 'LLMAssistantSetMemoryAdmin', {})).toMatchObject({ success: false });
    expect(await api(llm, 'LLMAssistantSetMemoryAdmin', { extractionDisabled: 'yes' })).toMatchObject({ success: false });
    expect((await api(llm, 'LLMAssistantSetMemoryAdmin', { extractionDisabled: true })).admin.extractionDisabled).toBe(true);
    await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ memoryAdmin: { extractionDisabled: false }, memoryPause: { saving: true }, memoryRetention: { unusedDays: 30 } }) });
    let after = await api(llm, 'LLMAssistantGetMemoryControls');
    expect(after.admin.extractionDisabled).toBe(true);
    expect(after.pause.saving).toBe(false);
    expect(after.retention.unusedDays).toBe(0);
    expect((await api(llm, 'LLMAssistantSetMemoryAdmin', { extractionDisabled: false })).admin.extractionDisabled).toBe(false);
});

test('the history and Undo through the API, and clearing memory', async ({ llm }) => {
    expect(await api(llm, 'LLMAssistantGetMemoryChanges')).toMatchObject({ success: true, changes: [] });
    expect(await api(llm, 'LLMAssistantUndoMemoryChange', { changeId: 'nope' })).toMatchObject({ success: true, found: false, undone: 0 });
    expect(await api(llm, 'LLMAssistantUndoMemoryChange', {})).toHaveProperty('error');
    await add(llm, 'To be cleared');
    expect(await api(llm, 'LLMAssistantClearUserProfile')).toMatchObject({ success: true });
    expect((await api(llm, 'LLMAssistantListMemoryNotes')).total).toBe(0);
});

test('the per-assistant switch writes only its own flag and never copies a shared assistant into the user\'s settings', async ({ llm }) => {
    // A shared assistant (the built-in one): the personal scope is refused, and nothing is copied into the user's own settings.
    let refused = await api(llm, 'LLMAssistantSetAssistantMemory', { assistantId: 'default', enabled: false });
    expect(refused.success).toBe(false);
    expect(refused.error).toContain('shared');
    expect((await api(llm, 'LLMAssistantGetAssistants')).assistants.find((a: any) => a.id === 'default')._scope).toBe('shared');
    expect(await api(llm, 'LLMAssistantSetAssistantMemory', { assistantId: 'default', enabled: false, scope: 'shared' })).toMatchObject({ success: true, id: 'default', enabled: false });
    let shared = (await api(llm, 'LLMAssistantGetAssistants')).assistants.find((a: any) => a.id === 'default');
    expect(shared._scope).toBe('shared');
    expect(shared._effectiveMemoryExtraction).toBe(false);
    expect((await api(llm, 'LLMAssistantSetAssistantMemory', { assistantId: 'default', enabled: true, scope: 'shared' })).enabled).toBe(true);
    expect((await api(llm, 'LLMAssistantGetAssistants')).assistants.find((a: any) => a.id === 'default')._effectiveMemoryExtraction).toBe(true);

    // The input is checked.
    expect(await api(llm, 'LLMAssistantSetAssistantMemory', { assistantId: 'default' })).toMatchObject({ success: false });
    expect(await api(llm, 'LLMAssistantSetAssistantMemory', { assistantId: 'default', enabled: 'no' })).toMatchObject({ success: false });
    expect(await api(llm, 'LLMAssistantSetAssistantMemory', { enabled: true })).toMatchObject({ success: false });
    expect(await api(llm, 'LLMAssistantSetAssistantMemory', { assistantId: 'no-such-assistant', enabled: true })).toMatchObject({ success: false });
    expect(await api(llm, 'LLMAssistantSetAssistantMemory', { assistantId: 'no-such-assistant', enabled: true, scope: 'shared' })).toMatchObject({ success: false });
    expect((await api(llm, 'LLMAssistantGetAssistants')).assistants.map((a: any) => a.id)).not.toContain('no-such-assistant');

    // A personal assistant is changed in the user's own settings, and only its flag changes.
    let made = await api(llm, 'LLMAssistantSaveAssistant', { assistant: { name: 'Memory Test Assistant', description: 'for the memory API spec', systemPrompt: 'Be brief.' }, scope: 'personal' });
    expect(made.success, JSON.stringify(made)).toBe(true);
    try {
        let before = (await api(llm, 'LLMAssistantGetAssistant', { assistantId: made.id })).assistant;
        expect(await api(llm, 'LLMAssistantSetAssistantMemory', { assistantId: made.id, enabled: false })).toMatchObject({ success: true, enabled: false });
        let after = (await api(llm, 'LLMAssistantGetAssistant', { assistantId: made.id })).assistant;
        expect(after.memoryExtraction).toBe(false);
        expect({ ...after, memoryExtraction: undefined, _effectiveMemoryExtraction: undefined }).toEqual({ ...before, memoryExtraction: undefined, _effectiveMemoryExtraction: undefined });
        // The shared scope does not reach a personal assistant.
        expect((await api(llm, 'LLMAssistantSetAssistantMemory', { assistantId: made.id, enabled: true, scope: 'shared' })).success).toBe(false);
    } finally {
        await api(llm, 'LLMAssistantDeleteAssistant', { assistantId: made.id });
    }
});

test('a settings reset keeps the memory controls', async ({ llm }) => {
    await api(llm, 'LLMAssistantSetMemoryControls', { savingPaused: true, recallPaused: true, unusedDays: 90 });
    await api(llm, 'LLMAssistantSetMemorySettings', { enabled: true, model: 'mock-model-b', neverSave: ['divorce'] });
    await api(llm, 'LLMAssistantSetMemoryAdmin', { extractionDisabled: true });
    expect((await api(llm, 'LLMAssistantResetSettings', {})).success).toBe(true);
    let after = await api(llm, 'LLMAssistantGetMemoryControls');
    expect(after.pause).toEqual({ saving: true, recall: true });
    expect(after.retention.unusedDays).toBe(90);
    expect((await api(llm, 'LLMAssistantGetMemorySettings')).settings).toMatchObject({ enabled: true, model: 'mock-model-b', neverSave: ['divorce'] });
    // The shared reset (this user may) keeps the admin's switch too, so it cannot switch the extractor back on for everyone.
    expect((await api(llm, 'LLMAssistantResetSettings', { scope: 'shared' })).success).toBe(true);
    expect((await api(llm, 'LLMAssistantGetMemoryControls')).admin.extractionDisabled).toBe(true);
    await api(llm, 'LLMAssistantSetMemorySettings', { enabled: false, model: '', neverSave: [] });
});

test('memory_read counts the notes it returns as used, and does nothing while recall is paused', async ({ llm }) => {
    await add(llm, 'Read by the model');
    expect((await api(llm, 'LLMAssistantListMemoryNotes')).notes[0].lastUsed).toBe('');
    await api(llm, 'LLMAssistantSetMemoryControls', { recallPaused: true });
    let paused = await api(llm, 'LLMAssistantExecuteTool', { toolId: 'memory_read', args: {} });
    expect(JSON.stringify(paused)).toContain('paused');
    expect((await api(llm, 'LLMAssistantListMemoryNotes')).notes[0].lastUsed).toBe('');
    await api(llm, 'LLMAssistantSetMemoryControls', { recallPaused: false });
    let read = await api(llm, 'LLMAssistantExecuteTool', { toolId: 'memory_read', args: {} });
    expect(read.success, JSON.stringify(read)).toBe(true);
    expect((await api(llm, 'LLMAssistantListMemoryNotes')).notes[0].lastUsed).not.toBe('');
});

test('the name, pronouns, bio and work notes do not count against the pin limit', async ({ llm }) => {
    let data = JSON.stringify({ notes: [...Array.from({ length: 30 }, (_, i) => ({ text: `Pinned ${i}`, pinned: true })), { text: 'Sam', slot: 'name' }, { text: 'Teaches', slot: 'bio' }] });
    let imported = await api(llm, 'LLMAssistantImportMemory', { data });
    expect(imported.preview).toMatchObject({ addCount: 32, unpinned: 5 });
    let slots = (await api(llm, 'LLMAssistantListMemoryNotes', { kind: 'profile' })).notes;
    expect(slots).toHaveLength(2);
    expect(slots.every((n: any) => n.pinned)).toBe(true);
    expect((await api(llm, 'LLMAssistantListMemoryNotes', { kind: 'pinned' })).matched).toBe(27);
});

test('the per-chat switch through the API', async ({ llm }) => {
    let created = await api(llm, 'LLMAssistantCreateThread', { rememberChat: true });
    expect(created.success, JSON.stringify(created)).toBe(true);
    let threadId = created.thread.id;
    expect((await api(llm, 'LLMAssistantSetThreadMemory', { threadId, enabled: false })).memoryExtraction).toBe(false);
    expect((await api(llm, 'LLMAssistantGetThread', { threadId })).thread.memoryExtraction).toBe(false);
    expect((await api(llm, 'LLMAssistantSetThreadMemory', { threadId })).memoryExtraction).toBeNull();
    expect((await api(llm, 'LLMAssistantGetThread', { threadId })).thread.memoryExtraction).toBeUndefined();
    expect(await api(llm, 'LLMAssistantSetThreadMemory', { threadId: 'no-such-chat', enabled: false })).toMatchObject({ success: false });
    await api(llm, 'LLMAssistantDeleteThread', { threadId });
});

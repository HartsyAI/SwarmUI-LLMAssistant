import { test as base, expect, send, lastAssistant, setScenarios, mockRequests, clearRequests, api, clearMemory, reopen } from './fixtures';
import type { Page } from '@playwright/test';
import * as fs from 'node:fs';

// The Memory tab as a manager (review, edit, pin, delete, search, export, import with a preview), the history with Undo, the pause switches (saving and recall), the admin switch that turns the
// automatic extractor off for everyone, and the top bar's pause button. The extractor runs on "mock-model-b" (set in Settings), so its requests are told apart from the chat's.

/** The pause button's highlighted state (a bare /on/ would also match "icon"). */
const PAUSED = /\bon\b/;
const MARKER = 'Memory extraction request.';
const on = { enabled: true, model: 'mock-model-b', neverSave: [] as string[] };

const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await use(llm);
        await api(llm, 'LLMAssistantSetMemoryAdmin', { extractionDisabled: false });
        await api(llm, 'LLMAssistantSetMemoryControls', { savingPaused: false, recallPaused: false, unusedDays: 0 });
        await api(llm, 'LLMAssistantSetMemorySettings', { enabled: false, model: '', neverSave: [] });
        await clearMemory(llm);
    }
});

async function notes(page: Page): Promise<any[]> {
    let reply = await api(page, 'LLMAssistantGetUserProfile', { includeNotes: true });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    return reply.notes;
}

/** Adds notes through the import route (origin "user"). */
async function seed(page: Page, entries: Array<Record<string, unknown>>) {
    let reply = await api(page, 'LLMAssistantImportMemory', { data: JSON.stringify({ notes: entries }) });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
}

async function openMemory(page: Page) {
    await page.locator('#llma-settings-btn').click();
    await page.locator('.llma-modal-tab[data-tab="memory"]').click();
    await expect(page.locator('#llma-memory-toolbar')).toContainText('Your notes');
}

async function closeSettings(page: Page) {
    await page.locator('#llma-settings-close').click();
}

const note = (page: Page, text: string) => page.locator('#llma-memory-list .llma-memory-note', { hasText: text });

/** The requests the extractor made. */
async function extractionRequests(): Promise<any[]> {
    return (await mockRequests()).filter(r => {
        let users = (r.messages ?? []).filter((m: any) => m.role === 'user');
        return users.length > 0 && String(users[users.length - 1].content).includes(MARKER);
    });
}

const extractor = (ops: unknown) => ({ model: 'mock-model-b', match: MARKER, rounds: [{ text: JSON.stringify({ ops }) }] });

async function systemPromptFor(userText: string): Promise<string> {
    let matching = (await mockRequests()).filter(r => {
        let users = (r.messages ?? []).filter((m: any) => m.role === 'user');
        // The chat's own title request carries the same message, with a system prompt of its own.
        return users.length > 0 && String(users[users.length - 1].content).includes(userText) && !JSON.stringify(r.messages).includes('You write extremely short titles');
    });
    expect(matching.length, `no request for ${userText}`).toBeGreaterThan(0);
    return String((matching[matching.length - 1].messages ?? []).find((m: any) => m.role === 'system')?.content ?? '');
}

test('the manager groups notes by kind, shows where they came from and when they were used, and filters and searches', async ({ llm }) => {
    await seed(llm, [
        { text: 'Prefers green tea', kind: 'preference' },
        { text: 'Hates long preambles', kind: 'dislike' },
        { text: 'Lives in Lyon', kind: 'fact' },
        { text: 'Writing a SwarmUI extension', kind: 'project' },
        { text: 'Short, plain answers', kind: 'style' },
        { text: 'Kaleb', slot: 'name' }
    ]);
    await openMemory(llm);
    let groups = llm.locator('#llma-memory-list .llma-memory-group');
    await expect(groups).toHaveCount(6);
    await expect(groups.nth(0)).toContainText('Name and profile');
    await expect(groups.nth(0)).toContainText('Kaleb');
    await expect(groups.nth(5)).toContainText('Style');
    await expect(note(llm, 'Short, plain answers')).toContainText('from you');
    await expect(note(llm, 'Short, plain answers')).toContainText('last used');
    await expect(llm.locator('#llma-memory-totals')).toContainText('6 /');

    await llm.locator('#llma-memory-search').fill('LYON');
    await expect(llm.locator('#llma-memory-list .llma-memory-note')).toHaveCount(1);
    await expect(llm.locator('#llma-memory-totals')).toContainText('1 shown');
    await llm.locator('#llma-memory-search').fill('');
    await llm.locator('#llma-memory-kind-filter').selectOption('dislike');
    await expect(llm.locator('#llma-memory-list .llma-memory-note')).toHaveCount(1);
    await expect(llm.locator('#llma-memory-list')).toContainText('Hates long preambles');
    await llm.locator('#llma-memory-kind-filter').selectOption('');
    await llm.locator('#llma-memory-search').fill('zzz');
    await expect(llm.locator('#llma-memory-list')).toContainText('No note matches.');
});

test('a note\'s text is shown as text, never as markup', async ({ llm }) => {
    await seed(llm, [{ text: 'Plain <img src=x onerror="window.__pwned=1"> & <b>bold</b>', kind: 'fact' }]);
    await openMemory(llm);
    await expect(llm.locator('#llma-memory-list .llma-memory-note')).toHaveCount(1);
    await expect(llm.locator('#llma-memory-list img, #llma-memory-list b')).toHaveCount(0);
    expect(await llm.evaluate(() => (window as any).__pwned)).toBeUndefined();
    // The server also cleans angle brackets out of an imported note.
    expect((await notes(llm))[0].text).not.toContain('<');
});

test('editing a note\'s text and kind, pinning, and deleting one note all work in place', async ({ llm }) => {
    await seed(llm, [{ text: 'Lives in Paris', kind: 'fact' }, { text: 'Likes jazz', kind: 'fact' }]);
    await openMemory(llm);

    await note(llm, 'Lives in Paris').locator('.llma-memory-edit').click();
    let editor = llm.locator('#llma-memory-list .llma-memory-editing');
    await expect(editor).toHaveCount(1);
    await editor.locator('.llma-memory-edit-text').fill('Lives in Lyon');
    await editor.locator('.llma-memory-edit-kind').selectOption('project');
    await editor.locator('.llma-memory-edit-save').click();
    await expect(note(llm, 'Lives in Lyon')).toBeVisible();
    await expect(note(llm, 'Lives in Lyon')).toContainText('project');
    let saved = (await notes(llm)).find((n: any) => n.text == 'Lives in Lyon');
    expect(saved).toMatchObject({ kind: 'project', origin: 'user' });

    // A refusal is shown in the editor, and nothing changes.
    await note(llm, 'Likes jazz').locator('.llma-memory-edit').click();
    await llm.locator('.llma-memory-edit-text').fill('   ');
    await llm.locator('.llma-memory-edit-save').click();
    await expect(llm.locator('.llma-memory-edit-error')).toContainText('cannot be empty');
    await llm.locator('.llma-memory-edit-cancel').click();
    await expect(llm.locator('#llma-memory-list .llma-memory-editing')).toHaveCount(0);

    await note(llm, 'Likes jazz').locator('.llma-memory-pin').click();
    await expect(note(llm, 'Likes jazz').locator('.llma-memory-pinned')).toBeVisible();
    expect((await notes(llm)).find((n: any) => n.text == 'Likes jazz').pinned).toBe(true);
    await note(llm, 'Likes jazz').locator('.llma-memory-pin').click();
    await expect(note(llm, 'Likes jazz').locator('.llma-memory-pinned')).toHaveCount(0);

    // A single delete asks first too: declining changes nothing.
    llm.once('dialog', d => d.dismiss());
    await note(llm, 'Likes jazz').locator('.llma-memory-delete').click();
    expect(await notes(llm)).toHaveLength(2);
    llm.once('dialog', d => d.accept());
    await note(llm, 'Likes jazz').locator('.llma-memory-delete').click();
    await expect(note(llm, 'Likes jazz')).toHaveCount(0);
    expect((await notes(llm)).map((n: any) => n.text)).toEqual(['Lives in Lyon']);
});

test('a long note is edited in full, not from the cut text of the list', async ({ llm }) => {
    let long = 'Long note. ' + 'word '.repeat(300) + 'THE-END';
    await seed(llm, [{ text: long, kind: 'fact' }]);
    await openMemory(llm);
    await note(llm, 'Long note.').locator('.llma-memory-edit').click();
    await expect(llm.locator('.llma-memory-edit-text')).toHaveValue(new RegExp('THE-END$'));
    await llm.locator('.llma-memory-edit-save').click();
    await expect(llm.locator('#llma-memory-list .llma-memory-editing')).toHaveCount(0);
    let id = (await notes(llm))[0].id;
    expect((await api(llm, 'LLMAssistantGetMemoryNote', { id })).note.text.endsWith('THE-END')).toBe(true);
});

test('delete selected asks first, and clear all asks first and empties memory', async ({ llm }) => {
    await seed(llm, [{ text: 'Keep me', kind: 'fact' }, { text: 'Drop one', kind: 'fact' }, { text: 'Drop two', kind: 'fact' }]);
    await openMemory(llm);
    await expect(llm.locator('#llma-memory-delete-selected')).toBeDisabled();
    await note(llm, 'Drop one').locator('.llma-memory-select').check();
    await note(llm, 'Drop two').locator('.llma-memory-select').check();
    await expect(llm.locator('#llma-memory-delete-selected')).toContainText('(2)');

    // Declining changes nothing.
    llm.once('dialog', d => d.dismiss());
    await llm.locator('#llma-memory-delete-selected').click();
    expect(await notes(llm)).toHaveLength(3);
    llm.once('dialog', d => d.accept());
    await llm.locator('#llma-memory-delete-selected').click();
    await expect(llm.locator('#llma-memory-list .llma-memory-note')).toHaveCount(1);
    expect((await notes(llm)).map((n: any) => n.text)).toEqual(['Keep me']);

    llm.once('dialog', d => d.dismiss());
    await llm.locator('#llma-memory-clear').click();
    expect(await notes(llm)).toHaveLength(1);
    llm.once('dialog', d => d.accept());
    await llm.locator('#llma-memory-clear').click();
    await expect(llm.locator('#llma-memory-list')).toContainText('Nothing is remembered yet');
    expect(await notes(llm)).toHaveLength(0);
});

test('export downloads the notes, and import shows a preview first, never replaces what is there, and adds only on Add', async ({ llm }) => {
    await seed(llm, [{ text: 'Likes tea', kind: 'preference' }, { text: 'Sam', slot: 'name' }]);
    await openMemory(llm);
    let [download] = await Promise.all([llm.waitForEvent('download'), llm.locator('#llma-memory-export').click()]);
    expect(download.suggestedFilename()).toMatch(/^llm-assistant-memory-.*\.json$/);
    let exported = JSON.parse(fs.readFileSync(await download.path(), 'utf8'));
    expect(exported.format).toBe('llma-memory');
    expect(exported.notes.map((n: any) => n.text).sort()).toEqual(['Likes tea', 'Sam']);

    let file = {
        notes: [
            { text: 'Likes tea', kind: 'preference' },
            { text: 'Samuel', slot: 'name' },
            { text: 'Plays <i>chess</i>', kind: 'fact' },
            { text: 'My password is hunter2', kind: 'fact' },
            { text: '', kind: 'fact' },
            { text: 'Hates spam', kind: 'dislike' }
        ]
    };
    await llm.locator('#llma-memory-import-file').setInputFiles({ name: 'memory.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(file)) });
    let preview = llm.locator('#llma-memory-import-preview .llma-memory-import');
    await expect(preview).toBeVisible();
    await expect(preview.locator('.llma-memory-import-add')).toHaveText('2');
    await expect(preview.locator('.llma-memory-import-duplicates')).toHaveText('1');
    await expect(preview.locator('.llma-memory-import-conflicts')).toHaveText('1');
    await expect(preview.locator('.llma-memory-import-blocked')).toHaveText('1');
    await expect(preview.locator('.llma-memory-import-invalid')).toHaveText('1');
    await expect(preview).toContainText('Hates spam');
    await expect(preview.locator('img, i')).toHaveCount(0);
    // Nothing has changed yet.
    expect(await notes(llm)).toHaveLength(2);

    await preview.locator('#llma-memory-import-cancel').click();
    await expect(llm.locator('#llma-memory-import-preview .llma-memory-import')).toHaveCount(0);
    expect(await notes(llm)).toHaveLength(2);

    await llm.locator('#llma-memory-import-file').setInputFiles({ name: 'memory.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(file)) });
    await llm.locator('#llma-memory-import-apply').click();
    await expect(note(llm, 'Hates spam')).toBeVisible();
    let after = await notes(llm);
    expect(after).toHaveLength(4);
    expect(after.find((n: any) => n.slot == 'name').text).toBe('Sam');
    expect(after.find((n: any) => n.text.startsWith('Plays')).origin).toBe('user');

    // A file that is not a memory export is refused with a message and changes nothing.
    await llm.locator('#llma-memory-import-file').setInputFiles({ name: 'bad.json', mimeType: 'application/json', buffer: Buffer.from('{ not json') });
    await expect(llm.locator('.llma-toast')).toContainText('not valid JSON');
    await expect(llm.locator('#llma-memory-import-preview .llma-memory-import')).toHaveCount(0);
    expect(await notes(llm)).toHaveLength(4);
});

test('history lists what the extractor removed, and Undo from it brings the note back', async ({ llm }) => {
    await api(llm, 'LLMAssistantSetMemorySettings', on);
    await reopen(llm);
    await setScenarios([{ match: 'remember the old flat', rounds: [{ toolCalls: [{ name: 'memory_write', arguments: { category: 'fact', content: 'Lives in the old flat' } }] }, { text: 'Noted.' }] }]);
    await send(llm, 'remember the old flat');
    await expect(lastAssistant(llm)).toContainText('Noted.', { timeout: 20_000 });
    expect(await notes(llm)).toHaveLength(1);

    await setScenarios([extractor([{ op: 'delete', id: 'n1' }]), { match: 'I no longer live', rounds: [{ text: 'Understood.' }] }]);
    await send(llm, 'I no longer live in the old flat, I sold it last week.');
    await expect.poll(async () => (await notes(llm)).length, { timeout: 30_000 }).toBe(0);
    await llm.locator('.llma-memory-notice-close').click({ timeout: 20_000 });

    await openMemory(llm);
    let change = llm.locator('#llma-memory-changes .llma-memory-change');
    await expect(change).toHaveCount(1);
    await expect(change).toContainText('removed');
    await expect(change).toContainText('Lives in the old flat');
    await change.locator('.llma-memory-undo').click();
    await expect(llm.locator('#llma-memory-changes .llma-memory-change')).toHaveCount(0);
    await expect(note(llm, 'Lives in the old flat')).toBeVisible();
    expect((await notes(llm)).map((n: any) => n.text)).toEqual(['Lives in the old flat']);
});

test('pausing saving stops the extractor and the memory tool, and resuming brings them back; edits in Settings still work', async ({ llm }) => {
    await api(llm, 'LLMAssistantSetMemorySettings', on);
    await reopen(llm);
    await openMemory(llm);
    await llm.locator('#llma-mempause-saving').check();
    await expect(llm.locator('.llma-memory-paused-badge')).toBeVisible();
    expect((await api(llm, 'LLMAssistantGetMemoryControls')).pause).toEqual({ saving: true, recall: false });
    await closeSettings(llm);
    await expect(llm.locator('#llma-memory-pause-btn')).toHaveClass(PAUSED);

    await clearRequests();
    await setScenarios([extractor([{ op: 'add', kind: 'fact', text: 'Works as a nurse in Lyon.' }]),
        { match: 'I moved to Lyon', rounds: [{ text: 'Lyon is lovely.' }] },
        { match: 'remember my tea', rounds: [{ toolCalls: [{ name: 'memory_write', arguments: { category: 'preference', content: 'Green tea' } }] }, { text: 'Tried.' }] }]);
    await send(llm, 'I moved to Lyon last month and I work as a nurse.');
    await expect(lastAssistant(llm)).toContainText('Lyon is lovely.', { timeout: 20_000 });
    await llm.waitForTimeout(1500);
    expect(await extractionRequests()).toHaveLength(0);
    expect(await notes(llm)).toHaveLength(0);
    await expect(llm.locator('.llma-memory-notice')).toHaveCount(0);

    await send(llm, 'remember my tea');
    await expect(lastAssistant(llm)).toContainText('Tried.', { timeout: 20_000 });
    expect(await notes(llm)).toHaveLength(0);
    let toolResults = (await mockRequests()).flatMap(r => (r.messages ?? []).filter((m: any) => m.role === 'tool')).map((m: any) => String(m.content));
    expect(toolResults.some(t => t.includes('paused memory saving'))).toBe(true);

    // Notes can still be changed by hand while saving is paused.
    await seed(llm, [{ text: 'Added by hand', kind: 'fact' }]);
    expect(await notes(llm)).toHaveLength(1);

    // Resume from the top bar: the extractor works again.
    await llm.locator('#llma-memory-pause-btn').click();
    await expect(llm.locator('#llma-memory-pause-btn')).not.toHaveClass(PAUSED);
    expect((await api(llm, 'LLMAssistantGetMemoryControls')).pause).toEqual({ saving: false, recall: false });
    await clearRequests();
    await send(llm, 'I moved to Lyon again and I work as a nurse there.');
    await expect.poll(async () => (await notes(llm)).some((n: any) => n.text == 'Works as a nurse in Lyon.'), { timeout: 30_000 }).toBe(true);
    expect((await extractionRequests()).length).toBeGreaterThan(0);
});

test('pausing recall keeps notes out of the prompt and out of memory_read, and nothing is deleted', async ({ llm }) => {
    await seed(llm, [{ text: 'Is fond of the quokka', kind: 'fact' }, { text: 'Kaleb', slot: 'name' }]);
    await setScenarios([{ match: 'first question', rounds: [{ text: 'One.' }] }, { match: 'second question', rounds: [{ text: 'Two.' }] },
        { match: 'check memory now', rounds: [{ toolCalls: [{ name: 'memory_read', arguments: {} }] }, { text: 'Checked.' }] }, { match: 'third question', rounds: [{ text: 'Three.' }] }]);
    await send(llm, 'first question');
    await expect(lastAssistant(llm)).toContainText('One.', { timeout: 20_000 });
    let before = await systemPromptFor('first question');
    expect(before).toContain('Is fond of the quokka');
    expect(before).toContain('Kaleb');

    await openMemory(llm);
    await llm.locator('#llma-mempause-recall').check();
    await expect(llm.locator('.llma-memory-paused-badge')).toBeVisible();
    await closeSettings(llm);
    await send(llm, 'second question');
    await expect(lastAssistant(llm)).toContainText('Two.', { timeout: 20_000 });
    let paused = await systemPromptFor('second question');
    expect(paused).not.toContain('<saved_notes>');
    expect(paused).not.toContain('quokka');
    expect(paused).not.toContain('Kaleb');

    await send(llm, 'check memory now');
    await expect(lastAssistant(llm)).toContainText('Checked.', { timeout: 20_000 });
    let results = (await mockRequests()).flatMap(r => (r.messages ?? []).filter((m: any) => m.role === 'tool')).map((m: any) => String(m.content));
    expect(results.some(t => t.includes('paused memory'))).toBe(true);
    expect(results.every(t => !t.includes('quokka'))).toBe(true);
    expect(await notes(llm)).toHaveLength(2);

    await llm.locator('#llma-memory-pause-btn').click();
    await send(llm, 'third question');
    await expect(lastAssistant(llm)).toContainText('Three.', { timeout: 20_000 });
    expect(await systemPromptFor('third question')).toContain('Is fond of the quokka');
});

test('an admin can switch automatic saving off for everyone, and a user cannot turn it back on', async ({ llm }) => {
    await api(llm, 'LLMAssistantSetMemorySettings', on);
    await reopen(llm);
    await expect(llm.locator('#llma-memory-toggle-btn')).toBeVisible();
    await openMemory(llm);
    await llm.locator('#llma-memadmin-off').check();
    await expect(llm.locator('.llma-memory-admin-lock')).toBeVisible();
    await expect(llm.locator('#llma-memx-enabled')).toBeDisabled();
    await expect(llm.locator('#llma-memory-where')).toContainText('administrator');
    await closeSettings(llm);
    await expect(llm.locator('#llma-memory-toggle-btn')).toBeHidden();

    let controls = await api(llm, 'LLMAssistantGetMemoryControls');
    expect(controls.admin.extractionDisabled).toBe(true);
    expect(controls.extractionAllowed).toBe(false);

    // The user's own switch is still on, and saving the settings again does not matter: nothing is extracted.
    await api(llm, 'LLMAssistantSetMemorySettings', on);
    await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ memoryAdmin: { extractionDisabled: false } }) });
    expect((await api(llm, 'LLMAssistantGetMemoryControls')).admin.extractionDisabled).toBe(true);
    await clearRequests();
    await setScenarios([extractor([{ op: 'add', kind: 'fact', text: 'Works as a nurse in Lyon.' }]), { match: 'I moved to Lyon', rounds: [{ text: 'Lyon is lovely.' }] }]);
    await send(llm, 'I moved to Lyon last month and I work as a nurse.');
    await expect(lastAssistant(llm)).toContainText('Lyon is lovely.', { timeout: 20_000 });
    await llm.waitForTimeout(1500);
    expect(await extractionRequests()).toHaveLength(0);
    expect(await notes(llm)).toHaveLength(0);

    // The memory tool still works when the user asks.
    await setScenarios([{ match: 'remember my tea', rounds: [{ toolCalls: [{ name: 'memory_write', arguments: { category: 'preference', content: 'Green tea' } }] }, { text: 'Noted.' }] }]);
    await send(llm, 'remember my tea');
    await expect(lastAssistant(llm)).toContainText('Noted.', { timeout: 20_000 });
    expect(await notes(llm)).toHaveLength(1);

    // Switching it back is the admin's to do.
    await openMemory(llm);
    await llm.locator('#llma-memadmin-off').uncheck();
    await expect(llm.locator('.llma-memory-admin-lock')).toHaveCount(0);
    await expect(llm.locator('#llma-memx-enabled')).toBeEnabled();
});

test('retention is off by default, is kept in range, and shows what it forgot', async ({ llm }) => {
    await openMemory(llm);
    await expect(llm.locator('#llma-memret-days')).toHaveValue('');
    await llm.locator('#llma-memret-days').fill('3');
    await llm.locator('#llma-memret-save').click();
    await expect(llm.locator('#llma-memret-out')).toContainText('Use a number from 7');
    expect((await api(llm, 'LLMAssistantGetMemoryControls')).retention.unusedDays).toBe(0);
    await llm.locator('#llma-memret-days').fill('90');
    await llm.locator('#llma-memret-save').click();
    await expect(llm.locator('#llma-memret-out')).toContainText('Saved.');
    expect((await api(llm, 'LLMAssistantGetMemoryControls')).retention.unusedDays).toBe(90);
    // Fresh notes are not old enough, so nothing is forgotten.
    await seed(llm, [{ text: 'A fresh note', kind: 'fact' }]);
    await llm.locator('#llma-memret-save').click();
    await expect(llm.locator('#llma-memret-out')).not.toContainText('forgotten');
    expect(await notes(llm)).toHaveLength(1);
    await llm.locator('#llma-memret-days').fill('');
    await llm.locator('#llma-memret-save').click();
    await expect(llm.locator('#llma-memret-out')).toContainText('Off.');
    expect((await api(llm, 'LLMAssistantGetMemoryControls')).retention.unusedDays).toBe(0);
});

test('the Memory tab fits a phone and does not scroll sideways', async ({ llm }) => {
    await seed(llm, [{ text: 'A fairly long note so that the row has to wrap on a narrow screen: ' + 'wordy '.repeat(20), kind: 'fact' }, { text: 'Kaleb', slot: 'name' }]);
    await llm.setViewportSize({ width: 375, height: 800 });
    await openMemory(llm);
    await expect(llm.locator('#llma-memory-list .llma-memory-note')).toHaveCount(2);
    let overflow = await llm.evaluate(() => {
        let body = document.querySelector('#llma-settings-memory')!.closest('.llma-modal-body') as HTMLElement;
        return body.scrollWidth - body.clientWidth;
    });
    expect(overflow).toBeLessThanOrEqual(1);
    await expect(note(llm, 'fairly long').locator('.llma-memory-delete')).toBeVisible();
    await llm.setViewportSize({ width: 1280, height: 800 });
});

test('a note can be added by hand, the list pages with Show more, and an assistant can be switched off from the Memory tab', async ({ llm }) => {
    await api(llm, 'LLMAssistantImportMemory', { data: JSON.stringify({ notes: Array.from({ length: 130 }, (_, i) => ({ text: `Bulk note ${i}`, kind: 'fact' })) }) });
    await api(llm, 'LLMAssistantSetMemorySettings', on);
    await reopen(llm);
    await openMemory(llm);
    await expect(llm.locator('#llma-memory-list .llma-memory-note')).toHaveCount(100);
    await llm.locator('.llma-memory-more').click();
    await expect(llm.locator('#llma-memory-list .llma-memory-note')).toHaveCount(130);
    await expect(llm.locator('.llma-memory-more')).toHaveCount(0);

    await llm.locator('#llma-memory-add-text').fill('Added from the form');
    await llm.locator('#llma-memory-add-kind').selectOption('project');
    await llm.locator('#llma-memory-add').click();
    await expect(llm.locator('#llma-memory-add-text')).toHaveValue('');
    await llm.locator('#llma-memory-search').fill('Added from the form');
    await expect(llm.locator('#llma-memory-list .llma-memory-note')).toHaveCount(1);
    await expect(note(llm, 'Added from the form')).toContainText('project');
    await expect(llm.locator('#llma-memory-totals')).toContainText('131 /');

    let box = llm.locator('.llma-memory-assistant[data-assistant-id="default"]');
    await expect(box).toBeChecked();
    await box.uncheck();
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant.memoryExtraction).toBe(false);
    await box.check();
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant.memoryExtraction).toBe(true);
});

test('"Show more" continues the list on screen, not a search typed since', async ({ llm }) => {
    await api(llm, 'LLMAssistantImportMemory', { data: JSON.stringify({ notes: Array.from({ length: 130 }, (_, i) => ({ text: `Bulk note ${i}`, kind: 'fact' })) }) });
    await openMemory(llm);
    await expect(llm.locator('#llma-memory-list .llma-memory-note')).toHaveCount(100);
    // A search typed while the list is still the unfiltered one (it is applied after a short pause): the next page is still of that list.
    await llm.evaluate(() => { (window as any).llmaMemorySettings.query = 'zzz-nothing'; });
    await llm.locator('.llma-memory-more').click();
    await expect(llm.locator('#llma-memory-list .llma-memory-note')).toHaveCount(130);
    expect(new Set(await llm.locator('#llma-memory-list .llma-memory-note').evaluateAll(els => els.map(e => (e as HTMLElement).dataset.noteId))).size).toBe(130);
});

import { test, expect, api, send, lastAssistant, openTab, reopen, setScenarios, mockRequests, clearRequests } from './fixtures';
import type { Page } from '@playwright/test';

// The first-run guide and the starter set. The llm fixture skips the guide and deletes every chat; these specs reset it to a new user's.

const ASSISTANTS = ['starter-general', 'starter-prompt-helper', 'starter-researcher'];
const SNIPPETS = ['enhance-prompt', 'video-prompt', 'random-prompt', 'write-instructions'];
const TOTAL = ASSISTANTS.length + SNIPPETS.length + 2;

/** Removes the starter assistants and snippets (the fixture removes chats). */
async function removeStarters(page: Page) {
    for (let assistantId of ASSISTANTS) {
        await api(page, 'LLMAssistantDeleteAssistant', { assistantId, scope: 'personal' });
    }
    for (let name of SNIPPETS) {
        await api(page, 'LLMAssistantDeletePromptAsset', { kind: 'command', name, scope: 'personal' });
    }
}

/** The ids the user has of each kind. */
async function owned(page: Page) {
    let assistants = (await api(page, 'LLMAssistantGetAssistants')).assistants.map((a: any) => a.id as string);
    let commands = (await api(page, 'LLMAssistantGetPromptAssets')).assets.filter((a: any) => a.kind == 'command').map((a: any) => a.name as string);
    let chats = (await api(page, 'LLMAssistantGetThreads')).threads.map((t: any) => t.title as string);
    return { assistants, commands, chats };
}

/** A new user opening the tab: no saved state, no chats. */
async function asNewUser(page: Page) {
    await removeStarters(page);
    let reset = await api(page, 'LLMAssistantSetOnboarding', { action: 'reset' });
    expect(reset).toMatchObject({ success: true, status: 'new', show: true });
    await reopen(page);
}

// Each test reloads the page once or twice on top of the fixture's own load, which on a busy machine passes the default limit.
test.beforeEach(() => test.slow());

test.afterEach(async ({ llm }) => {
    await removeStarters(llm);
    await api(llm, 'LLMAssistantSetOnboarding', { action: 'skip' });
});

test('a new user sees the guide over an empty chat, with the setup checked and the message box usable', async ({ llm }) => {
    await asNewUser(llm);
    let guide = llm.locator('#llma-onboarding');
    await expect(guide).toBeVisible();
    await expect(guide.locator('li[data-check]')).toHaveCount(4);
    await expect(guide.locator('li[data-check="backend"]')).toHaveAttribute('data-status', 'ok');
    await expect(guide.locator('li[data-check="model"]')).toHaveAttribute('data-status', 'ok');
    await expect(llm.locator('#llma-onboarding-nobackend')).toHaveCount(0);
    await expect(guide.locator('li[data-item]')).toHaveCount(TOTAL);
    await expect(llm.locator('#llma-empty-greeting')).toBeHidden();
    await expect(llm.locator('#llma-input')).toBeVisible();
    // The first message sent ends the first run: the guide does not come back over the next empty chat.
    await setScenarios([{ match: 'hello there', rounds: [{ text: 'Hello to you.' }] }]);
    await send(llm, 'hello there');
    await expect(lastAssistant(llm)).toContainText('Hello to you.');
    await expect(guide).toBeHidden();
    await llm.locator('#llma-new-thread-btn').click();
    await expect(llm.locator('#llma-empty-greeting')).toBeVisible();
    await expect(guide).toBeHidden();
    expect(await api(llm, 'LLMAssistantGetOnboarding', { checks: false })).toMatchObject({ status: 'completed', show: false });
});

test('the assistant gallery is never covered, by switching or by deleting the open chat', async ({ llm }) => {
    await asNewUser(llm);
    let guide = llm.locator('#llma-onboarding');
    await expect(guide).toBeVisible();
    // The panel may be folded away at this width; its button is clicked directly.
    await llm.locator('#llma-panel-inner [data-action="switch"]').evaluate((button: HTMLElement) => button.click());
    await expect(llm.locator('#llma-welcome')).toBeVisible();
    await expect(guide).toBeHidden();
    // Picking one leads to an empty chat, where the guide still is.
    await llm.locator('.llma-asst-card[data-assistant-id="default"]').click();
    await expect(guide).toBeVisible();
    await expect(llm.locator('#llma-input')).toBeVisible();
});

test('installing keeps the assistant on screen, and the next message goes to it', async ({ llm }) => {
    let other = { id: 'e2e-onboarding-other', name: 'Other one', instructions: { chat: 'Be other.' }, enabledToolIds: [] };
    expect((await api(llm, 'LLMAssistantSaveAssistant', { assistant: other, scope: 'personal' })).success).toBe(true);
    await asNewUser(llm);
    await expect(llm.locator('#llma-onboarding li[data-check]')).toHaveCount(4);
    // A chat opened with an assistant that is not the saved active one.
    await llm.evaluate(() => (0, eval)(`llmaStartChatWithAssistant('e2e-onboarding-other', { persist: false })`));
    await llm.locator('#llma-onboarding-install').click();
    await expect(llm.locator('#llma-onboarding-result')).toContainText(`Added: ${TOTAL}`);
    await setScenarios([{ match: 'who are you', rounds: [{ text: 'The other one.' }] }]);
    await send(llm, 'who are you');
    await expect(lastAssistant(llm)).toContainText('The other one.');
    let chat = (await api(llm, 'LLMAssistantGetThreads')).threads.find((t: any) => !t.title.startsWith('Example:'));
    expect(chat.assistantId).toBe('e2e-onboarding-other');
    await api(llm, 'LLMAssistantDeleteAssistant', { assistantId: 'e2e-onboarding-other', scope: 'personal' });
});

test('a skill by a snippet\'s name and an assistant by a starter\'s name count as there', async ({ llm }) => {
    await removeStarters(llm);
    expect((await api(llm, 'LLMAssistantSavePromptAsset', { kind: 'skill', name: 'video-prompt', body: 'My own skill.' })).success).toBe(true);
    let mine = { id: 'e2e-onboarding-mine', name: 'researcher', instructions: { chat: 'Mine.' }, enabledToolIds: [] };
    expect((await api(llm, 'LLMAssistantSaveAssistant', { assistant: mine, scope: 'personal' })).success).toBe(true);
    let result = await api(llm, 'LLMAssistantInstallStarterSet');
    expect(result.skipped.map((s: any) => `${s.id}:${s.code}`).sort()).toEqual(['starter-researcher:exists', 'video-prompt:exists']);
    let have = await owned(llm);
    expect(have.commands).not.toContain('video-prompt');
    expect(have.assistants).not.toContain('starter-researcher');
    await api(llm, 'LLMAssistantDeletePromptAsset', { kind: 'skill', name: 'video-prompt', scope: 'personal' });
    await api(llm, 'LLMAssistantDeleteAssistant', { assistantId: 'e2e-onboarding-mine', scope: 'personal' });
});

test('running the guide again while an install is still going leaves its buttons working', async ({ llm }) => {
    let errors: string[] = [];
    llm.on('pageerror', error => errors.push(error.message));
    await asNewUser(llm);
    await expect(llm.locator('#llma-onboarding li[data-check]')).toHaveCount(4);
    let release: () => void = () => {};
    let held = new Promise<void>(resolve => release = resolve);
    await llm.route('**/API/LLMAssistantInstallStarterSet', async route => {
        let response = await route.fetch();
        await held;
        await route.fulfill({ response });
    });
    // The rerun's report waits too, so the guide has none when the install answers.
    let answer: () => void = () => {};
    let waiting = new Promise<void>(resolve => answer = resolve);
    let first = true;
    await llm.route('**/API/LLMAssistantGetOnboarding', async route => {
        if (first) {
            first = false;
            await waiting;
        }
        await route.continue();
    });
    await llm.locator('#llma-onboarding-install').click();
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('#llma-onboarding-rerun').click();
    await expect(llm.locator('#llma-onboarding')).toContainText('Checking...');
    release();
    await expect(llm.locator('#llma-onboarding-result')).toContainText(`Added: ${TOTAL}`);
    answer();
    let checked = llm.waitForRequest(request => request.url().endsWith('LLMAssistantGetOnboarding') && request.postDataJSON().refresh === true);
    await llm.locator('#llma-onboarding-recheck').click();
    await checked;
    await expect(llm.locator('#llma-onboarding-recheck')).toBeEnabled();
    expect(errors).toEqual([]);
});

test('the API reports the checklist and the starter set, and refuses an unknown action', async ({ llm }) => {
    await removeStarters(llm);
    let report = await api(llm, 'LLMAssistantGetOnboarding');
    expect(report).toMatchObject({ success: true, status: 'skipped', show: false, canChat: true });
    expect(report.checks.map((c: any) => c.id)).toEqual(['backend', 'model', 'tools', 'embeddings']);
    expect(report.setup).toMatchObject({ page: 'Server > Backends', canView: true });
    expect(report.starter).toMatchObject({ total: TOTAL, installed: 0, missing: TOTAL });
    let brief = await api(llm, 'LLMAssistantGetOnboarding', { checks: false });
    expect(brief.checks).toBeUndefined();
    expect((await api(llm, 'LLMAssistantSetOnboarding', { action: 'erase' })).success).toBeFalsy();
    // Someone with chats is not new to the extension: a reset alone does not show it to them, running it does.
    await api(llm, 'LLMAssistantRunTurn', { message: 'make a chat' });
    expect(await api(llm, 'LLMAssistantSetOnboarding', { action: 'reset' })).toMatchObject({ status: 'new', show: false });
    // The first look at a new user who has chats records them as past the first run, so deleting the chats later does not start one.
    expect(await api(llm, 'LLMAssistantGetOnboarding', { checks: false })).toMatchObject({ status: 'completed', show: false });
    expect(await api(llm, 'LLMAssistantSetOnboarding', { action: 'run' })).toMatchObject({ status: 'pending', show: true });
    // A message ends a running guide too.
    await api(llm, 'LLMAssistantRunTurn', { message: 'another one' });
    expect(await api(llm, 'LLMAssistantGetOnboarding', { checks: false })).toMatchObject({ status: 'completed', show: false });
});

test('with no backend it says how to set one up, links to the backend page, and can be skipped to look around', async ({ llm }) => {
    // The server's own answer, changed to what it says when no backend is running: the harness's one backend is left alone.
    let canAdd = true;
    await llm.route('**/API/LLMAssistantGetOnboarding', async route => {
        let response = await route.fetch();
        let body = await response.json();
        if (body.checks) {
            body.canChat = false;
            body.checks[0] = { id: 'backend', title: 'LLM backend', status: 'fail', detail: 'Swarm has no LLM backend running.', fix: canAdd ? 'Add one under Server > Backends.' : 'Ask an administrator.' };
            body.setup.canAdd = canAdd;
            body.setup.canView = canAdd;
        }
        await route.fulfill({ response, json: body });
    });
    await asNewUser(llm);
    let guide = llm.locator('#llma-onboarding');
    await expect(llm.locator('#llma-onboarding-nobackend')).toContainText('Any backend Swarm offers will do');
    await expect(guide.locator('li[data-check="backend"]')).toContainText('Server > Backends');
    await expect(llm.locator('#llma-onboarding-done')).toHaveCount(0);
    // The starter set does not need a backend.
    await llm.locator('#llma-onboarding-install').click();
    await expect(llm.locator('#llma-onboarding-install')).toBeDisabled();
    await llm.locator('#llma-onboarding-backends').click();
    await expect(llm.locator('#serverbackendstabbutton')).toHaveClass(/active/);
    await openTab(llm);
    await llm.locator('#llma-onboarding-skip').click();
    await expect(guide).toBeHidden();
    await expect(llm.locator('#llma-input')).toBeVisible();
    await llm.locator('#llma-settings-btn').click();
    await expect(llm.locator('#llma-onboarding-rerun')).toBeVisible();
    await llm.locator('#llma-settings-close').click();
    // A user who cannot change backends is told to ask.
    canAdd = false;
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('#llma-onboarding-rerun').click();
    await expect(llm.locator('#llma-onboarding-nobackend')).toContainText('Ask an administrator');
    await expect(llm.locator('#llma-onboarding-backends')).toHaveCount(0);
});

test('skipping is remembered over a reload, and Settings runs it again', async ({ llm }) => {
    await asNewUser(llm);
    await llm.locator('#llma-onboarding-skip').click();
    await expect(llm.locator('#llma-onboarding')).toBeHidden();
    await expect(llm.locator('#llma-empty-greeting')).toBeVisible();
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetOnboarding', { checks: false })).status).toBe('skipped');
    await reopen(llm);
    await expect(llm.locator('#llma-empty-greeting')).toBeVisible();
    await expect(llm.locator('#llma-onboarding')).toBeHidden();
    // A settings reset does not bring it back.
    expect((await api(llm, 'LLMAssistantResetSettings')).success).toBe(true);
    expect((await api(llm, 'LLMAssistantGetOnboarding', { checks: false })).show).toBe(false);
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('#llma-onboarding-rerun').click();
    await expect(llm.locator('#llma-settings-overlay')).toBeHidden();
    await expect(llm.locator('#llma-onboarding')).toBeVisible();
    await llm.locator('#llma-onboarding-done').click();
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetOnboarding', { checks: false })).status).toBe('completed');
});

test('one click adds the starter set as the user\'s own items, without asking any model', async ({ llm }) => {
    await asNewUser(llm);
    await clearRequests();
    await llm.locator('#llma-onboarding-install').click();
    await expect(llm.locator('#llma-onboarding-result')).toContainText(`Added: ${TOTAL}`);
    await expect(llm.locator('#llma-onboarding li[data-installed="true"]')).toHaveCount(TOTAL);
    await expect(llm.locator('#llma-onboarding-install')).toBeDisabled();
    expect(await mockRequests()).toEqual([]);
    let have = await owned(llm);
    expect(have.assistants).toEqual(expect.arrayContaining(ASSISTANTS));
    expect(have.commands).toEqual(expect.arrayContaining(SNIPPETS));
    expect(have.chats.sort()).toEqual(['Example: getting started', 'Example: shaping an image prompt']);
    let general = (await api(llm, 'LLMAssistantGetAssistant', { assistantId: 'starter-general' })).assistant;
    expect(general._scope).toBe('personal');
    // The chats are listed, and open with their assistant.
    await expect(llm.locator('#llma-thread-list')).toContainText('Example: shaping an image prompt');
    // A tool an assistant lists is offered only while it is switched on: the knowledge search starts switched off.
    let dry = await api(llm, 'LLMAssistantRunTurn', { message: 'look this up', assistantId: 'starter-researcher', dryRun: true });
    expect(dry.success, JSON.stringify(dry)).toBe(true);
    let offered = JSON.stringify(dry.tools);
    expect(offered).toContain('swarm_docs');
    expect(offered).not.toContain('search_knowledge');
    // Installing in a first run does not close the guide by itself.
    await reopen(llm);
    await llm.locator('#llma-new-thread-btn').click();
    await expect(llm.locator('#llma-onboarding')).toBeVisible();
    await llm.locator('#llma-thread-list').getByText('Example: shaping an image prompt').click();
    await expect(llm.locator('#llma-onboarding')).toBeHidden();
    await expect(lastAssistant(llm)).toContainText('still night');
    let chat = (await api(llm, 'LLMAssistantGetThreads')).threads.find((t: any) => t.title == 'Example: shaping an image prompt');
    expect(chat.assistantId).toBe('starter-prompt-helper');
});

test('running the install again adds only what is missing and leaves edits alone', async ({ llm }) => {
    await removeStarters(llm);
    let first = await api(llm, 'LLMAssistantInstallStarterSet');
    expect(first.added).toHaveLength(TOTAL);
    let again = await api(llm, 'LLMAssistantInstallStarterSet');
    expect(again.added).toEqual([]);
    expect(again.skipped.map((s: any) => s.code)).toEqual(Array(TOTAL).fill('exists'));
    // Edit one of each kind, delete one of each kind.
    let general = (await api(llm, 'LLMAssistantGetAssistant', { assistantId: 'starter-general' })).assistant;
    expect((await api(llm, 'LLMAssistantSaveAssistant', { assistant: { ...general, name: 'My own name' }, scope: 'personal' })).success).toBe(true);
    expect((await api(llm, 'LLMAssistantSavePromptAsset', { kind: 'command', name: 'enhance-prompt', body: 'my own text' })).success).toBe(true);
    let threads = (await api(llm, 'LLMAssistantGetThreads')).threads;
    let edited = threads.find((t: any) => t.title == 'Example: getting started');
    let removed = threads.find((t: any) => t.id != edited.id);
    expect((await api(llm, 'LLMAssistantRenameThread', { threadId: edited.id, title: 'Renamed example' })).success).toBe(true);
    expect((await api(llm, 'LLMAssistantDeleteThread', { threadId: removed.id })).success).toBe(true);
    await api(llm, 'LLMAssistantDeleteAssistant', { assistantId: 'starter-researcher', scope: 'personal' });
    await api(llm, 'LLMAssistantDeletePromptAsset', { kind: 'command', name: 'video-prompt' });

    let third = await api(llm, 'LLMAssistantInstallStarterSet');
    expect(third.added.map((a: any) => a.id).sort()).toEqual([removed.id, 'starter-researcher', 'video-prompt'].sort());
    let have = await owned(llm);
    expect(have.chats.sort()).toEqual(['Example: shaping an image prompt', 'Renamed example']);
    expect((await api(llm, 'LLMAssistantGetAssistant', { assistantId: 'starter-general' })).assistant.name).toBe('My own name');
    expect((await api(llm, 'LLMAssistantGetPromptAsset', { kind: 'command', name: 'enhance-prompt', scope: 'personal' })).asset.body).toBe('my own text');
    expect(third.starter).toMatchObject({ installed: TOTAL, missing: 0 });
});

test('on a phone the guide fits the screen, scrolls to its buttons and installs', async ({ llm }) => {
    await llm.setViewportSize({ width: 375, height: 740 });
    await asNewUser(llm);
    let guide = llm.locator('#llma-onboarding');
    await expect(guide).toBeVisible();
    await expect(guide.locator('li[data-check]')).toHaveCount(4);
    expect(await guide.evaluate(e => e.scrollWidth - e.clientWidth)).toBeLessThanOrEqual(0);
    await expect(llm.locator('#llma-onboarding-skip')).toBeInViewport();
    await expect(llm.locator('#llma-input')).toBeInViewport();
    await llm.locator('#llma-onboarding-install').click();
    await expect(llm.locator('#llma-onboarding-result')).toContainText(`Added: ${TOTAL}`);
    await llm.locator('#llma-onboarding-skip').click();
    await expect(guide).toBeHidden();
    await llm.setViewportSize({ width: 1280, height: 720 });
});

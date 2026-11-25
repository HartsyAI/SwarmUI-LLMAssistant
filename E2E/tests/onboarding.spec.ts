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
    // A message sent while it shows takes its place; a new empty chat brings it back until it is dismissed.
    await setScenarios([{ match: 'hello there', rounds: [{ text: 'Hello to you.' }] }]);
    await send(llm, 'hello there');
    await expect(lastAssistant(llm)).toContainText('Hello to you.');
    await expect(guide).toBeHidden();
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
    expect(await api(llm, 'LLMAssistantSetOnboarding', { action: 'run' })).toMatchObject({ status: 'pending', show: true });
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

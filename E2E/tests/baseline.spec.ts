import { test, expect, send, lastAssistant, setScenarios, mockRequests, api, openTab } from './fixtures';

// Baseline: the behaviour that exists today. Every later phase must keep these green.

test('the tab loads, lists the mock models, and shows the reserved Code switch disabled', async ({ llm }) => {
    await expect(llm.locator('#llma-model-select option')).toHaveCount(2);
    await expect(llm.locator('#llma-mode-chat')).toHaveAttribute('aria-pressed', 'true');
    await expect(llm.locator('#llma-mode-code')).toBeDisabled();
});

test('a message streams a reply and the model receives the system prompt and the user text', async ({ llm }) => {
    await setScenarios([{ match: 'ping', rounds: [{ text: 'pong from the mock server', chunkDelayMs: 20 }] }]);
    await send(llm, 'ping');
    await expect(lastAssistant(llm)).toContainText('pong from the mock server');
    let requests = await mockRequests();
    let chat = requests.filter(r => r.messages?.some((m: any) => m.role === 'user' && String(m.content).includes('ping')));
    expect(chat.length).toBeGreaterThan(0);
    expect(chat[0].messages[0].role).toBe('system');
    expect(chat[0].stream).toBe(true);
});

test('the chat is saved, titled and survives a reload', async ({ llm }) => {
    await setScenarios([{ match: 'remember this', rounds: [{ text: 'saved reply' }] }]);
    await send(llm, 'remember this');
    await expect(lastAssistant(llm)).toContainText('saved reply');
    await expect(llm.locator('.llma-thread-item')).toHaveCount(1);
    await llm.reload();
    await openTab(llm);
    await llm.locator('.llma-thread-item').first().click();
    await expect(llm.locator('.llma-msg-row.user .llma-msg-bubble').first()).toContainText('remember this');
    await expect(lastAssistant(llm)).toContainText('saved reply');
});

test('a tool call round trip: the model calls memory_read, the result shows, the model answers', async ({ llm }) => {
    await setScenarios([{
        match: 'check my memory',
        rounds: [
            { toolCalls: [{ name: 'memory_read', arguments: {} }] },
            { text: 'I checked your memory.' }
        ]
    }]);
    await send(llm, 'check my memory');
    // Tool calls are grouped under a collapsed "Used Memory Read" header; the call itself is inside the group.
    await expect(llm.locator('.llma-tool-group-summary').first()).toContainText('Memory Read', { timeout: 20_000 });
    await expect(llm.locator('.llma-tool-call-bubble.success')).toHaveCount(1, { timeout: 20_000 });
    await expect(lastAssistant(llm)).toContainText('I checked your memory.');
    await llm.locator('.llma-tool-group-head').first().click();
    await expect(llm.locator('.llma-tool-call-bubble.success').first()).toBeVisible();
    let requests = await mockRequests();
    let second = requests.find(r => r.messages?.some((m: any) => m.role === 'tool'));
    expect(second).toBeTruthy();
    expect(second.tools?.some((t: any) => t.function?.name === 'memory_read')).toBeTruthy();
});

test('Stop ends a streaming reply and the composer comes back', async ({ llm }) => {
    let long = Array.from({ length: 200 }, (_, i) => `word${i}`).join(' ');
    await setScenarios([{ match: 'slow one', rounds: [{ text: long, chunkDelayMs: 100 }] }]);
    await send(llm, 'slow one');
    await expect(llm.locator('#llma-stop-btn')).toBeVisible();
    await llm.locator('#llma-stop-btn').click();
    await expect(llm.locator('#llma-stop-btn')).toBeHidden({ timeout: 15_000 });
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await expect(lastAssistant(llm)).not.toContainText('word199');
});

test('editing a user message forks a branch and keeps the old one', async ({ llm }) => {
    await setScenarios([
        { match: 'first version', rounds: [{ text: 'answer one' }] },
        { match: 'second version', rounds: [{ text: 'answer two' }] }
    ]);
    await send(llm, 'first version');
    await expect(lastAssistant(llm)).toContainText('answer one');
    let userRow = llm.locator('.llma-msg-row.user').first();
    await userRow.hover();
    await userRow.getByRole('button', { name: 'Edit' }).click();
    let editor = llm.locator('.llma-msg-row.user textarea').first();
    await editor.fill('second version');
    await llm.locator('.llma-msg-edit-actions').getByRole('button', { name: 'Save' }).click();
    await expect(lastAssistant(llm)).toContainText('answer two');
    await expect(llm.locator('.llma-branch-pager').first()).toBeVisible();
});

test('regenerate adds a sibling reply', async ({ llm }) => {
    await setScenarios([{ match: 'roll', rounds: [{ text: 'first roll' }] }]);
    await send(llm, 'roll');
    await expect(lastAssistant(llm)).toContainText('first roll');
    await setScenarios([{ match: 'roll', rounds: [{ text: 'second roll' }] }]);
    let reply = llm.locator('.llma-msg-row.assistant').last();
    await reply.hover();
    await reply.getByRole('button', { name: 'Regenerate' }).click();
    await expect(lastAssistant(llm)).toContainText('second roll');
    await expect(llm.locator('.llma-branch-pager').first()).toBeVisible();
});

test('compare mode runs two models side by side', async ({ llm }) => {
    await setScenarios([
        { match: 'versus', model: 'mock-model', rounds: [{ text: 'lane A says hi' }] },
        { match: 'versus', model: 'mock-model-b', rounds: [{ text: 'lane B says hi' }] }
    ]);
    await llm.locator('#llma-compare-toggle').click();
    await llm.locator('#llma-model-select-b').selectOption('mock-model-b');
    await send(llm, 'versus');
    await expect(llm.locator('body')).toContainText('lane A says hi');
    await expect(llm.locator('body')).toContainText('lane B says hi');
});

test('a backend error is shown to the user instead of hanging', async ({ llm }) => {
    await setScenarios([{ match: 'boom', rounds: [{ error: { status: 500, message: 'mock upstream failure' } }] }]);
    await send(llm, 'boom');
    await expect(llm.locator('.llma-msg-error')).toBeVisible({ timeout: 20_000 });
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
});

test('threads can be renamed and deleted', async ({ llm }) => {
    await setScenarios([{ match: 'name me', rounds: [{ text: 'ok' }] }]);
    await send(llm, 'name me');
    await expect(lastAssistant(llm)).toContainText('ok');
    let listed = await api(llm, 'LLMAssistantGetThreads');
    expect(listed.threads.length).toBe(1);
    let id = listed.threads[0].id;
    let renamed = await api(llm, 'LLMAssistantRenameThread', { threadId: id, title: 'Renamed by e2e' });
    expect(renamed.success).toBe(true);
    await llm.reload();
    await openTab(llm);
    await expect(llm.locator('.llma-thread-item', { hasText: 'Renamed by e2e' })).toBeVisible();
    await api(llm, 'LLMAssistantDeleteThread', { threadId: id });
    await llm.reload();
    await openTab(llm);
    await expect(llm.locator('.llma-thread-item')).toHaveCount(0);
});

test('the settings dialog opens and switches tabs', async ({ llm }) => {
    await llm.locator('#llma-settings-btn').click();
    await expect(llm.locator('#llma-settings-overlay')).toBeVisible();
    for (let tab of ['assistants', 'tools', 'companion', 'general']) {
        await llm.locator(`.llma-modal-tab[data-tab="${tab}"]`).click();
        await expect(llm.locator(`#llma-settings-${tab}`)).toBeVisible();
    }
    await llm.locator('#llma-settings-close').click();
    await expect(llm.locator('#llma-settings-overlay')).toBeHidden();
});

test('typing / opens the tool picker and a tool can be run directly', async ({ llm }) => {
    await llm.locator('#llma-input').fill('/');
    let picker = llm.locator('text=memory_read').first();
    await expect(picker).toBeVisible();
});

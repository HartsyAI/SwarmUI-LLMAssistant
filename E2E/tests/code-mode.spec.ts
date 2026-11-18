import { test, expect, send, lastAssistant, setScenarios, mockRequests, api, turn, toolResults, enableCodeToolsGlobally, reopen, setMode, resetCodeTools } from './fixtures';
import type { Page } from '@playwright/test';
import os from 'node:os';

// Code mode in the interface: the Chat / Code switch, the workspace picker, the Workspace panel (files and tasks), diff rows in the transcript,
// the plan card and the chat list filter. A chat in code mode brings the code tools itself, so most specs leave every assistant's tool list alone.

test.beforeEach(async ({ llm }) => {
    let listed = await api(llm, 'LLMAssistantGetWorkspaces');
    test.skip(!listed.canUseCode, 'This user does not hold llm_code_mode.');
    await enableCodeToolsGlobally(llm);
    await reopen(llm);
});

test.afterEach(async ({ llm }) => {
    await resetCodeTools(llm);
});

const codeSwitch = (page: Page) => page.locator('label[for="llma-mode-code"]');

/** Switches to Code mode before a first message. */
async function goCode(page: Page) {
    await codeSwitch(page).click();
    await expect(page.locator('#llma-mode-code')).toBeChecked();
}

test('the Code segment is on for a user who may use it, and switching shows the workspace controls', async ({ llm }) => {
    await expect(llm.locator('#llma-mode-code')).toBeEnabled();
    await expect(llm.locator('#llma-workspace-select')).toBeHidden();
    await goCode(llm);
    await expect(llm.locator('#llma-container')).toHaveClass(/llma-code-mode/);
    await expect(llm.locator('#llma-workspace-select')).toBeVisible();
    await expect(llm.locator('.llma-panel-header-label')).toHaveText('Workspace');
    await expect(llm.locator('.llma-code-tabs .nav-link')).toHaveText(['Files', 'Tasks']);
    // A coding session starts careful: changes and commands ask first.
    await expect(llm.locator('#llma-approval-ask')).toBeChecked();
    await llm.locator('label[for="llma-mode-chat"]').click();
    await expect(llm.locator('#llma-container')).not.toHaveClass(/llma-code-mode/);
    await expect(llm.locator('.llma-panel-header-label')).toHaveText('Assistant');
    await expect(llm.locator('#llma-workspace-select')).toBeHidden();
});

test('a chat started in Code mode is saved as one, shows in the list with a badge, and the filter splits the two kinds', async ({ llm }) => {
    await setScenarios([{ match: 'plain', rounds: [{ text: 'Plain reply.' }] }, { match: 'coding', rounds: [{ text: 'Code reply.' }] }]);
    await send(llm, 'plain');
    await expect(lastAssistant(llm)).toContainText('Plain reply.');
    await llm.locator('#llma-new-thread-btn').click();
    await goCode(llm);
    await send(llm, 'coding');
    await expect(lastAssistant(llm)).toContainText('Code reply.');

    let threads = (await api(llm, 'LLMAssistantGetThreads')).threads as any[];
    expect(threads.map(t => t.mode).sort()).toEqual(['chat', 'code']);
    let items = llm.locator('.llma-thread-item');
    await expect(items).toHaveCount(2);
    await expect(llm.locator('.llma-thread-item .llma-thread-mode-badge')).toHaveCount(1);

    await expect(llm.locator('#llma-thread-mode-filter')).toBeVisible();
    await llm.locator('label[for="llma-mode-filter-code"]').click();
    await expect(items).toHaveCount(1);
    await expect(items.first()).toContainText(/coding|Code|New/i);
    await llm.locator('label[for="llma-mode-filter-chat"]').click();
    await expect(items).toHaveCount(1);
    await expect(llm.locator('.llma-thread-mode-badge')).toHaveCount(0);
    await llm.locator('label[for="llma-mode-filter-all"]').click();
    await expect(items).toHaveCount(2);
});

test('opening a chat takes the mode, workspace panel and approval of that chat, and the mode of a chat can be changed and is kept', async ({ llm }) => {
    await setScenarios([{ match: 'hello', rounds: [{ text: 'Hi.' }] }]);
    await send(llm, 'hello');
    await expect(lastAssistant(llm)).toContainText('Hi.');
    let threadId = (await api(llm, 'LLMAssistantGetThreads')).threads[0].id;
    expect((await api(llm, 'LLMAssistantGetThread', { threadId })).thread.mode).toBe('chat');

    await goCode(llm);
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetThread', { threadId })).thread.mode).toBe('code');
    await reopen(llm);
    await llm.locator('.llma-thread-item').first().click();
    await expect(llm.locator('#llma-mode-code')).toBeChecked();
    await expect(llm.locator('#llma-workspace-select')).toBeVisible();

    await llm.locator('label[for="llma-mode-chat"]').click();
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetThread', { threadId })).thread.mode).toBe('chat');
    // The server refuses an unknown mode.
    expect((await api(llm, 'LLMAssistantSetThreadMode', { threadId, mode: 'agent' })).success).toBe(false);
});

test('a chat in Code mode brings the code tools, and a chat in Chat mode does not', async ({ llm }) => {
    await setScenarios([
        turn('write in code', [{ name: 'code_write', arguments: { path: 'made.txt', content: 'hello' } }], 'Written in code mode.'),
        turn('write in chat', [{ name: 'code_write', arguments: { path: 'nope.txt', content: 'x' } }], 'Tried in chat mode.')
    ]);
    await goCode(llm);
    await setMode(llm, 'auto');
    await send(llm, 'write in code');
    await expect(lastAssistant(llm)).toContainText('Written in code mode.');
    let [written] = await toolResults();
    expect(written).toMatchObject({ success: true, path: 'made.txt' });
    let first = (await mockRequests())[0];
    expect(first.tools.map((t: any) => t.function.name)).toEqual(expect.arrayContaining(['code_read', 'code_write', 'code_edit', 'code_glob', 'code_grep', 'code_bash', 'code_todo']));

    await llm.locator('#llma-new-thread-btn').click();
    await llm.locator('label[for="llma-mode-chat"]').click();
    await send(llm, 'write in chat');
    await expect(lastAssistant(llm)).toContainText('Tried in chat mode.');
    let requests = (await mockRequests()).filter(r => JSON.stringify(r.messages).includes('write in chat'));
    expect((requests[0].tools ?? []).some((t: any) => t.function.name.startsWith('code_'))).toBe(false);
    // The model made the call anyway (as a stale tool list might): the assistant does not list the tool, so it is refused.
    expect((await toolResults())[0].success).toBe(false);
    expect((await toolResults())[0].error).toContain('not enabled');
});

test('the Files tab shows what the assistant made, opens folders and files, and catches up after each turn', async ({ llm }) => {
    await setScenarios([turn('make files', [
        { name: 'code_write', arguments: { path: 'src/app.js', content: 'let name = "demo";\nconsole.log(name);\n' } },
        { name: 'code_write', arguments: { path: 'README.md', content: '# Demo\n' } },
    ], 'Files made.')]);
    await goCode(llm);
    await setMode(llm, 'auto');
    await expect(llm.locator('#llma-code-body')).toContainText('Send a message to start the session');
    await send(llm, 'make files');
    await expect(lastAssistant(llm)).toContainText('Files made.');

    let tree = llm.locator('.llma-code-tree');
    await expect(tree.locator('.llma-code-entry')).toHaveText([/src\//, /README\.md/]);
    await tree.locator('.llma-code-entry[data-path="src"]').click();
    await expect(tree.locator('.llma-code-entry[data-path="src/app.js"]')).toBeVisible();
    await tree.locator('.llma-code-entry[data-path="src/app.js"]').click();
    let viewer = llm.locator('.llma-code-viewer');
    await expect(viewer.locator('.llma-code-viewer-name')).toHaveText('src/app.js');
    await expect(viewer.locator('.llma-code-file')).toContainText('   1  let name = "demo";');
    await expect(viewer.locator('.llma-code-file')).toContainText('   2  console.log(name);');
    await viewer.locator('.llma-code-close').click();
    await expect(viewer.locator('.llma-code-file')).toHaveCount(0);

    // Nothing in what the panel was told names a host path.
    let listed = await api(llm, 'LLMAssistantListWorkspaceFiles', { threadId: (await api(llm, 'LLMAssistantGetThreads')).threads[0].id, path: 'src' });
    expect(JSON.stringify(listed)).not.toContain(os.tmpdir());
});

test('the file routes refuse paths outside the workspace and a chat that is not there', async ({ llm }) => {
    await setScenarios([{ match: 'x', rounds: [{ text: 'ok' }] }]);
    await goCode(llm);
    await send(llm, 'x');
    await expect(lastAssistant(llm)).toContainText('ok');
    let threadId = (await api(llm, 'LLMAssistantGetThreads')).threads[0].id;
    for (let bad of ['..', '../..', 'C:/Windows', '/etc']) {
        let listed = await api(llm, 'LLMAssistantListWorkspaceFiles', { threadId, path: bad });
        expect(listed.success, bad).toBe(false);
        expect(JSON.stringify(listed)).not.toContain(os.tmpdir());
        let read = await api(llm, 'LLMAssistantReadWorkspaceFile', { threadId, path: bad });
        expect(read.success, bad).toBe(false);
    }
    expect((await api(llm, 'LLMAssistantListWorkspaceFiles', { threadId: 'not-a-chat' })).success).toBe(false);
});

test('the Tasks tab follows the assistant\'s task list and keeps it when the chat is opened again', async ({ llm }) => {
    await setScenarios([turn('plan work', [{ name: 'code_todo', arguments: { todos: [
        { content: 'write the parser', status: 'completed' },
        { content: 'add tests', status: 'in_progress' },
        { content: 'update the docs', status: 'pending' }
    ] } }], 'Planned.')]);
    await goCode(llm);
    await setMode(llm, 'auto');
    await send(llm, 'plan work');
    await expect(lastAssistant(llm)).toContainText('Planned.');
    await expect(llm.locator('.llma-code-tabs [data-code-tab="tasks"] .badge')).toHaveText('3');
    await llm.locator('.llma-code-tabs [data-code-tab="tasks"]').click();
    let todos = llm.locator('.llma-code-todo');
    await expect(todos).toHaveCount(3);
    await expect(todos.nth(0).locator('.badge')).toHaveText('done');
    await expect(todos.nth(1).locator('.badge')).toHaveText('working');
    await expect(todos.nth(2).locator('.badge')).toHaveText('to do');
    await expect(todos.nth(0)).toHaveAttribute('data-status', 'completed');

    await reopen(llm);
    await llm.locator('.llma-thread-item').first().click();
    await llm.locator('.llma-code-tabs [data-code-tab="tasks"]').click();
    await expect(llm.locator('.llma-code-todo')).toHaveCount(3);
});

test('an edit shows as a diff in the transcript, and a failed code tool has no Retry', async ({ llm }) => {
    await setScenarios([turn('edit it', [
        { name: 'code_write', arguments: { path: 'f.txt', content: 'one\ntwo\nthree\n' } },
        { name: 'code_edit', arguments: { path: 'f.txt', oldText: 'two', newText: 'TWO' } },
        { name: 'code_read', arguments: { path: 'missing.txt' } },
    ], 'Edited.')]);
    await goCode(llm);
    await setMode(llm, 'auto');
    await send(llm, 'edit it');
    await expect(lastAssistant(llm)).toContainText('Edited.');
    // Open the activity group to see the steps.
    await llm.locator('.llma-tool-group-head').first().click();
    let diff = llm.locator('.llma-diff');
    await expect(diff).toHaveCount(1);
    await expect(diff.locator('.llma-diff-del')).toHaveText('-two');
    await expect(diff.locator('.llma-diff-add')).toHaveText('+TWO');
    await expect(diff.locator('.llma-diff-hunk')).toContainText('@@ line 2 @@');
    // The failed read says why and cannot be retried (a standalone retry would run outside the chat's workspace).
    await expect(llm.locator('.llma-tool-result-errtext')).toContainText('not found');
    await expect(llm.locator('.llma-tool-retry-btn')).toHaveCount(0);
});

test('Plan mode ends with a plan card; accepting it switches to Ask and tells the assistant to go ahead', async ({ llm }) => {
    await setScenarios([
        turn('make a plan', [{ name: 'code_todo', arguments: { todos: [{ content: 'step one', status: 'pending' }] } }], 'Here is the plan.'),
        { match: 'The plan is accepted', rounds: [{ text: 'Carrying it out.' }] }
    ]);
    await goCode(llm);
    await setMode(llm, 'plan');
    await expect(llm.locator('#llma-plan-card')).toBeHidden();
    await send(llm, 'make a plan');
    await expect(lastAssistant(llm)).toContainText('Here is the plan.');
    await expect(llm.locator('#llma-plan-card')).toBeVisible();

    await llm.locator('#llma-plan-keep').click();
    await expect(llm.locator('#llma-plan-card')).toBeHidden();
    await send(llm, 'make a plan');
    await expect(llm.locator('#llma-plan-card')).toBeVisible();

    await llm.locator('#llma-plan-accept').click();
    await expect(llm.locator('#llma-approval-ask')).toBeChecked();
    await expect(lastAssistant(llm)).toContainText('Carrying it out.');
    await expect(llm.locator('#llma-plan-card')).toBeHidden();
    let threadId = (await api(llm, 'LLMAssistantGetThreads')).threads[0].id;
    expect((await api(llm, 'LLMAssistantGetThread', { threadId })).thread.approvalMode).toBe('ask');
});

test('the plan card is not offered outside Plan mode', async ({ llm }) => {
    await setScenarios([{ match: 'hello', rounds: [{ text: 'Hi.' }] }]);
    await goCode(llm);
    await setMode(llm, 'auto');
    await send(llm, 'hello');
    await expect(lastAssistant(llm)).toContainText('Hi.');
    await expect(llm.locator('#llma-plan-card')).toBeHidden();
});

test('the workspace picker lists the user\'s workspaces, makes a new one, and points the chat at it', async ({ llm }) => {
    await setScenarios([
        turn('in default', [{ name: 'code_write', arguments: { path: 'only-in-default.txt', content: 'd' } }], 'Default written.'),
        turn('in project', [{ name: 'code_glob', arguments: { pattern: '*' } }], 'Project listed.')
    ]);
    await goCode(llm);
    await setMode(llm, 'auto');
    let picker = llm.locator('#llma-workspace-select');
    await expect(picker.locator('option')).toContainText(['default', 'New workspace…']);
    await expect(picker).toHaveValue('user:default');
    await send(llm, 'in default');
    await expect(lastAssistant(llm)).toContainText('Default written.');
    await expect(llm.locator('.llma-code-tree .llma-code-entry')).toHaveText([/only-in-default\.txt/]);

    llm.once('dialog', dialog => dialog.accept('my-project'));
    await picker.selectOption('__new__');
    await expect(picker).toHaveValue('user:my-project');
    let threadId = (await api(llm, 'LLMAssistantGetThreads')).threads[0].id;
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetThread', { threadId })).thread.workspace).toBe('user:my-project');
    await expect(llm.locator('.llma-code-tree')).toContainText('The workspace is empty');
    await send(llm, 'in project');
    await expect(lastAssistant(llm)).toContainText('Project listed.');
    expect((await toolResults())[0].files).toEqual([]);

    // A workspace picked before the chat exists is given to the chat when it is made.
    await llm.locator('#llma-new-thread-btn').click();
    await expect(picker).toBeVisible();
    await picker.selectOption('user:my-project');
    await setScenarios([{ match: 'first message', rounds: [{ text: 'Started.' }] }]);
    await send(llm, 'first message');
    await expect(lastAssistant(llm)).toContainText('Started.');
    let newest = (await api(llm, 'LLMAssistantGetThreads')).threads.find((t: any) => t.id !== threadId);
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetThread', { threadId: newest.id })).thread.workspace).toBe('user:my-project');
});

test('the mode switch and workspace controls fit a phone', async ({ llm }) => {
    await llm.setViewportSize({ width: 390, height: 844 });
    await goCode(llm);
    await expect(llm.locator('#llma-mode-code')).toBeChecked();
    await expect(llm.locator('#llma-workspace-select')).toBeVisible();
    let overflow = await llm.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
    expect(overflow).toBe(false);
});

test('a command step shows the command it ran', async ({ llm }) => {
    await setScenarios([turn('run it', [{ name: 'code_bash', arguments: { command: 'echo hello-from-bash' } }], 'Ran it.')]);
    await goCode(llm);
    await setMode(llm, 'auto');
    await send(llm, 'run it');
    await expect(lastAssistant(llm)).toContainText('Ran it.');
    // A lone step is shown open; only a group of several folds away.
    await expect(llm.locator('.llma-tool-result-fileinfo')).toContainText('$ echo hello-from-bash');
    await expect(llm.locator('.llma-tool-result-filecontent')).toContainText('hello-from-bash');
});

test('the file tree can be used from the keyboard', async ({ llm }) => {
    await setScenarios([turn('make one', [{ name: 'code_write', arguments: { path: 'src/k.txt', content: 'keys' } }], 'Made.')]);
    await goCode(llm);
    await setMode(llm, 'auto');
    await send(llm, 'make one');
    await expect(lastAssistant(llm)).toContainText('Made.');
    let folder = llm.locator('.llma-code-entry[data-path="src"]');
    await folder.focus();
    await folder.press('Enter');
    let file = llm.locator('.llma-code-entry[data-path="src/k.txt"]');
    await expect(file).toBeVisible();
    await file.focus();
    await file.press(' ');
    await expect(llm.locator('.llma-code-file')).toContainText('keys');
});

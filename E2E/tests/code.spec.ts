import { test, expect, send, lastAssistant, setScenarios, mockRequests, api, CODE, BUILTINS, turn, toolResults, enableCodeTools, reopen, setMode, resetCodeTools } from './fixtures';
import type { Page } from '@playwright/test';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

// Code mode's tools: a chat's workspace, the tools that read, change and search it, a task list, running commands, and the workspace's own
// instructions. All of it is off until a tool is switched on and the user holds llm_code_mode.

const extraRoot = path.join(os.tmpdir(), `llma-e2e-root-${process.pid}`);
test.beforeEach(async ({ llm }) => {
    let listed = await api(llm, 'LLMAssistantGetWorkspaces');
    test.skip(!listed.canUseCode, 'This user does not hold llm_code_mode.');
});

test.afterEach(async ({ llm }) => {
    await resetCodeTools(llm);
    for (let root of (await api(llm, 'LLMAssistantGetWorkspaceRoots')).roots ?? []) {
        await api(llm, 'LLMAssistantDeleteWorkspaceRoot', { id: root.id });
    }
    fs.rmSync(extraRoot, { recursive: true, force: true });
});

test('the code tools are off until someone switches them on', async ({ llm }) => {
    let tools = (await api(llm, 'LLMAssistantGetTools')).tools as any[];
    for (let id of CODE) {
        let tool = tools.find(t => t.id === id);
        expect(tool, id).toBeTruthy();
        expect(tool.enabled).toBe(false);
        expect(tool.isBuiltIn).toBe(true);
    }
    let assistant = (await api(llm, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant;
    for (let id of CODE) {
        expect(assistant.enabledToolIds).not.toContain(id);
    }
});

test('a chat writes, reads, edits, finds and searches files in its workspace', async ({ llm }) => {
    await enableCodeTools(llm);
    await reopen(llm);
    await setScenarios([turn('build it', [
        { name: 'code_write', arguments: { path: 'src/app.js', content: 'let name = "old";\nlet other = 1;\n' } },
        { name: 'code_edit', arguments: { path: 'src/app.js', oldText: 'let name = "old";', newText: 'let name = "new";' } },
        { name: 'code_read', arguments: { path: 'src/app.js' } },
        { name: 'code_glob', arguments: { pattern: '**/*.js' } },
        { name: 'code_grep', arguments: { pattern: 'name', path: 'src' } },
    ], 'All done.')]);
    await send(llm, 'build it');
    await expect(lastAssistant(llm)).toContainText('All done.');
    let [written, edited, read, globbed, grepped] = await toolResults();
    expect(written).toMatchObject({ success: true, path: 'src/app.js', created: true });
    expect(edited).toMatchObject({ success: true, replacements: 1 });
    expect(edited.diff).toContain('+let name = "new";');
    expect(read.content).toBe('let name = "new";\nlet other = 1;\n');
    expect(read.totalLines).toBe(2);
    expect(globbed.files).toEqual(['src/app.js']);
    expect(grepped.matches).toEqual([{ file: 'src/app.js', line: 1, text: 'let name = "new";' }]);
});

test('an edit that is ambiguous or misses is refused with what to do, and the file is unchanged', async ({ llm }) => {
    await enableCodeTools(llm);
    await reopen(llm);
    await setScenarios([turn('try edits', [
        { name: 'code_write', arguments: { path: 'f.txt', content: 'a\nb\na\n' } },
        { name: 'code_edit', arguments: { path: 'f.txt', oldText: 'a', newText: 'x' } },
        { name: 'code_edit', arguments: { path: 'f.txt', oldText: 'zzz', newText: 'x' } },
        { name: 'code_read', arguments: { path: 'f.txt' } },
    ], 'Edits tried.')]);
    await send(llm, 'try edits');
    await expect(lastAssistant(llm)).toContainText('Edits tried.');
    let results = await toolResults();
    expect(results[1].success).toBe(false);
    expect(results[1].error).toContain('2 times');
    expect(results[2].error).toContain('not found');
    expect(results[3].content).toBe('a\nb\na\n');
});

test('no path leads out of the workspace', async ({ llm }) => {
    await enableCodeTools(llm);
    await reopen(llm);
    await setScenarios([turn('escape', [
        { name: 'code_read', arguments: { path: '../../../../../../../../etc/passwd' } },
        { name: 'code_write', arguments: { path: '../outside.txt', content: 'x' } },
        { name: 'code_read', arguments: { path: 'C:/Windows/win.ini' } },
        { name: 'code_read', arguments: { path: '/etc/passwd' } },
        { name: 'code_write', arguments: { path: '.git/hooks/pre-commit', content: 'x' } },
        { name: 'code_bash', arguments: { command: 'echo hi', workingDirectory: '../..' } },
    ], 'Blocked.')]);
    await setMode(llm, 'auto');
    await send(llm, 'escape');
    await expect(lastAssistant(llm)).toContainText('Blocked.');
    let results = await toolResults();
    expect(results.every(r => r.success === false)).toBe(true);
    expect(results[0].error).toContain('..');
    expect(results[1].error).toContain('..');
    // A Windows drive path is outside the workspace on Windows and, on Linux, a relative name with a colon in it: refused either way.
    expect(results[2].error).toMatch(/outside|invalid character/);
    expect(results[3].error).toContain('outside');
    expect(results[4].error).toContain('.git');
    expect(results[5].error).toContain('..');
    // Error text names no host path.
    expect(JSON.stringify(results)).not.toContain(os.tmpdir());
});

test('Ask mode asks before changing a file or running a command, but reads and the task list go straight through', async ({ llm }) => {
    await enableCodeTools(llm);
    await reopen(llm);
    await setScenarios([
        turn('look', [{ name: 'code_glob', arguments: { pattern: '*' } }, { name: 'code_todo', arguments: { todos: [{ content: 'look around', status: 'in_progress' }] } }], 'Looked.'),
        turn('change', [{ name: 'code_write', arguments: { path: 'n.txt', content: 'n' } }], 'Changed.'),
        turn('run', [{ name: 'code_bash', arguments: { command: 'echo from-bash' } }], 'Ran.')
    ]);
    await setMode(llm, 'ask');
    await send(llm, 'look');
    await expect(lastAssistant(llm)).toContainText('Looked.');
    await expect(llm.locator('.llma-tool-approval')).toHaveCount(0);

    await send(llm, 'change');
    await expect(llm.locator('.llma-tool-approval')).toBeVisible();
    await expect(llm.locator('.llma-tool-approval')).toContainText('Write');
    await llm.locator('.llma-tool-approval button[data-decision="deny"]').click();
    await expect(lastAssistant(llm)).toContainText('Changed.');
    expect((await toolResults())[0].denied).toBe(true);

    await send(llm, 'run');
    await expect(llm.locator('.llma-tool-approval')).toBeVisible();
    await llm.locator('.llma-tool-approval button[data-decision="allow"]').click();
    await expect(lastAssistant(llm)).toContainText('Ran.');
    let [ran] = await toolResults();
    expect(ran.success).toBe(true);
    expect(ran.exitCode).toBe(0);
    expect(ran.stdout).toContain('from-bash');
});

test('Plan mode reads and plans but changes nothing', async ({ llm }) => {
    await enableCodeTools(llm);
    await reopen(llm);
    await setScenarios([turn('plan it', [
        { name: 'code_glob', arguments: { pattern: '*' } },
        { name: 'code_todo', arguments: { todos: [{ content: 'step one', status: 'pending' }, { content: 'step two', status: 'pending' }] } },
        { name: 'code_write', arguments: { path: 'no.txt', content: 'x' } },
        { name: 'code_bash', arguments: { command: 'echo no' } },
    ], 'Planned.')]);
    await setMode(llm, 'plan');
    await send(llm, 'plan it');
    await expect(lastAssistant(llm)).toContainText('Planned.');
    let results = await toolResults();
    expect(results[0].success).toBe(true);
    expect(results[1].success).toBe(true);
    expect(results[2].denied).toBe(true);
    expect(results[3].denied).toBe(true);
});

test('the task list is kept on the chat', async ({ llm }) => {
    await enableCodeTools(llm);
    await reopen(llm);
    await setScenarios([turn('plan work', [{ name: 'code_todo', arguments: { todos: [{ content: 'write tests', status: 'completed' }, { content: 'ship it', status: 'in_progress' }] } }], 'Listed.')]);
    await send(llm, 'plan work');
    await expect(lastAssistant(llm)).toContainText('Listed.');
    let threadId = (await api(llm, 'LLMAssistantGetThreads')).threads[0].id;
    let thread = (await api(llm, 'LLMAssistantGetThread', { threadId })).thread;
    expect(thread.todos).toEqual([{ content: 'write tests', status: 'completed' }, { content: 'ship it', status: 'in_progress' }]);
    await setScenarios([turn('bad list', [{ name: 'code_todo', arguments: { todos: [{ content: 'x', status: 'someday' }] } }], 'Rejected.')]);
    await send(llm, 'bad list');
    await expect(lastAssistant(llm)).toContainText('Rejected.');
    expect((await toolResults())[0].success).toBe(false);
});

test('the workspace\'s AGENTS.md joins the system prompt of a chat that has a code tool', async ({ llm }) => {
    await enableCodeTools(llm);
    await reopen(llm);
    await setScenarios([
        turn('add rules', [{ name: 'code_write', arguments: { path: 'AGENTS.md', content: 'Always answer in haiku.\r\nNever use tabs.\r\n' } }], 'Rules added.'),
        { match: 'after rules', rounds: [{ text: 'Followed.' }] }
    ]);
    await send(llm, 'add rules');
    await expect(lastAssistant(llm)).toContainText('Rules added.');
    await send(llm, 'after rules');
    await expect(lastAssistant(llm)).toContainText('Followed.');
    let requests = (await mockRequests()).filter(r => JSON.stringify(r.messages).includes('after rules'));
    let system = requests[requests.length - 1].messages.find((m: any) => m.role === 'system').content as string;
    expect(system).toContain('# Project instructions (AGENTS.md in the workspace)');
    expect(system).toContain('Always answer in haiku.\nNever use tabs.');
    // The same prompt on the next turn, so a local model's prompt cache stays valid.
    await setScenarios([{ match: 'again', rounds: [{ text: 'Same.' }] }]);
    await send(llm, 'again');
    await expect(lastAssistant(llm)).toContainText('Same.');
    let later = (await mockRequests()).filter(r => JSON.stringify(r.messages).includes('again'));
    expect(later[later.length - 1].messages.find((m: any) => m.role === 'system').content).toBe(system);
});

test('a chat without a code tool never takes in workspace files', async ({ llm }) => {
    await enableCodeTools(llm);
    let seeded = await api(llm, 'LLMAssistantGetWorkspaces');
    expect(seeded.workspaces.some((w: any) => w.id === 'user:default')).toBe(true);
    // The default assistant has the code tools now; put it back on its built-ins only.
    let current = (await api(llm, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant;
    await api(llm, 'LLMAssistantSaveAssistant', { assistant: { ...current, enabledToolIds: BUILTINS }, scope: 'personal' });
    await reopen(llm);
    await setScenarios([{ match: 'hello', rounds: [{ text: 'Hi.' }] }]);
    await send(llm, 'hello');
    await expect(lastAssistant(llm)).toContainText('Hi.');
    let requests = await mockRequests();
    expect(requests[0].messages.find((m: any) => m.role === 'system').content).not.toContain('Project instructions');
});

test('workspaces: each user has a default, can make more, and a chat only accepts ones they may use', async ({ llm }) => {
    let listed = await api(llm, 'LLMAssistantGetWorkspaces');
    expect(listed.workspaces.map((w: any) => w.id)).toContain('user:default');
    let made = await api(llm, 'LLMAssistantCreateWorkspace', { name: 'my-project' });
    expect(made).toMatchObject({ success: true, id: 'user:my-project' });
    expect((await api(llm, 'LLMAssistantCreateWorkspace', { name: 'Bad Name!' })).success).toBe(false);
    expect((await api(llm, 'LLMAssistantGetWorkspaces')).workspaces.map((w: any) => w.id)).toContain('user:my-project');

    await enableCodeTools(llm);
    await reopen(llm);
    await setScenarios([turn('first', [{ name: 'code_write', arguments: { path: 'where.txt', content: 'in my-project' } }], 'Written.')]);
    await send(llm, 'first');
    await expect(lastAssistant(llm)).toContainText('Written.');
    let threadId = (await api(llm, 'LLMAssistantGetThreads')).threads[0].id;
    // The first write went to the default workspace; pointing the chat at the other one gives it a different, empty folder.
    expect((await api(llm, 'LLMAssistantSetThreadWorkspace', { threadId, workspaceId: 'user:my-project' })).success).toBe(true);
    expect((await api(llm, 'LLMAssistantGetThread', { threadId })).thread.workspace).toBe('user:my-project');
    await setScenarios([turn('second', [{ name: 'code_read', arguments: { path: 'where.txt' } }, { name: 'code_glob', arguments: { pattern: '*' } }], 'Checked.')]);
    await send(llm, 'second');
    await expect(lastAssistant(llm)).toContainText('Checked.');
    let [read, globbed] = await toolResults();
    expect(read.success).toBe(false);
    expect(globbed.files).toEqual([]);

    for (let bad of ['user:someone-else', 'root:nope', '../../etc', 'user:../x']) {
        expect((await api(llm, 'LLMAssistantSetThreadWorkspace', { threadId, workspaceId: bad })).success, bad).toBe(false);
    }
    expect((await api(llm, 'LLMAssistantSetThreadWorkspace', { threadId, workspaceId: '' })).success).toBe(true);
});

test('an admin can allow a folder on the host, and only that folder', async ({ llm }) => {
    let rootsReply = await api(llm, 'LLMAssistantGetWorkspaceRoots');
    test.skip(!rootsReply.success, 'This user does not hold llm_workspace_roots.');
    fs.mkdirSync(path.join(extraRoot, 'src'), { recursive: true });
    fs.writeFileSync(path.join(extraRoot, 'src', 'hello.txt'), 'from the host');
    fs.writeFileSync(path.join(path.dirname(extraRoot), `llma-secret-${process.pid}.txt`), 'secret');

    expect((await api(llm, 'LLMAssistantSaveWorkspaceRoot', { id: 'project', path: 'relative/path' })).success).toBe(false);
    expect((await api(llm, 'LLMAssistantSaveWorkspaceRoot', { id: 'project', path: path.join(extraRoot, 'missing') })).success).toBe(false);
    expect((await api(llm, 'LLMAssistantSaveWorkspaceRoot', { id: 'project', path: path.parse(extraRoot).root })).success).toBe(false);
    let saved = await api(llm, 'LLMAssistantSaveWorkspaceRoot', { id: 'project', path: extraRoot, label: 'Host project' });
    expect(saved).toMatchObject({ success: true, id: 'project' });

    let listed = await api(llm, 'LLMAssistantGetWorkspaces');
    let offered = listed.workspaces.find((w: any) => w.id === 'root:project');
    expect(offered).toMatchObject({ label: 'Host project', userOwned: false });
    // The path is for the admin's own listing, not for the list every user gets.
    expect(JSON.stringify(listed)).not.toContain(extraRoot);
    expect(JSON.stringify(await api(llm, 'LLMAssistantGetSettings'))).not.toContain(extraRoot);

    await enableCodeTools(llm);
    await reopen(llm);
    await setScenarios([turn('host work', [
        { name: 'code_read', arguments: { path: 'src/hello.txt' } },
        { name: 'code_read', arguments: { path: `../llma-secret-${process.pid}.txt` } },
    ], 'Done.')]);
    await send(llm, 'host work');
    await expect(lastAssistant(llm)).toContainText('Done.');
    let threadId = (await api(llm, 'LLMAssistantGetThreads')).threads[0].id;
    expect((await api(llm, 'LLMAssistantSetThreadWorkspace', { threadId, workspaceId: 'root:project' })).success).toBe(true);
    await send(llm, 'host work');
    await expect(lastAssistant(llm)).toContainText('Done.');
    let [hello, secret] = await toolResults();
    // Each line comes back with a newline, as a file read line by line does.
    expect(hello.content).toBe('from the host\n');
    expect(secret.success).toBe(false);

    // Stop allowing it: the chat's tools say so instead of falling back to something else.
    expect((await api(llm, 'LLMAssistantDeleteWorkspaceRoot', { id: 'project' })).success).toBe(true);
    await send(llm, 'host work');
    await expect(lastAssistant(llm)).toContainText('Done.');
    expect((await toolResults())[0].error).toContain('not available');
    fs.rmSync(path.join(path.dirname(extraRoot), `llma-secret-${process.pid}.txt`), { force: true });
});

test('a settings save cannot write allowed folders', async ({ llm }) => {
    for (let scope of ['personal', 'shared']) {
        let saved = await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ workspaceRoots: { evil: { path: os.tmpdir(), label: 'evil' } } }), scope });
        expect(saved.success).toBe(true);
    }
    expect((await api(llm, 'LLMAssistantGetWorkspaces')).workspaces.some((w: any) => w.id === 'root:evil')).toBe(false);
    await api(llm, 'LLMAssistantResetSettings', { scope: 'shared' });
    await api(llm, 'LLMAssistantResetSettings');
});

test('a reset of the shared settings keeps the folders an admin allowed', async ({ llm }) => {
    fs.mkdirSync(extraRoot, { recursive: true });
    expect((await api(llm, 'LLMAssistantSaveWorkspaceRoot', { id: 'kept', path: extraRoot, label: 'Kept' })).success).toBe(true);
    let reply = await api(llm, 'LLMAssistantResetSettings', { scope: 'shared' });
    expect(reply.success).toBe(true);
    // The reply says nothing of the folders: who may reset is not always who may see their paths.
    expect(reply.settings.workspaceRoots).toBeUndefined();
    expect(JSON.stringify(reply)).not.toContain(path.basename(extraRoot));
    expect((await api(llm, 'LLMAssistantGetWorkspaceRoots')).roots.map((r: any) => r.id)).toContain('kept');
});

test('shell_exec still works, now through the shared process runner', async ({ llm }) => {
    // shell_exec starts in SwarmUI's Data folder, which the harness's private root does not have.
    fs.mkdirSync(path.join(os.tmpdir(), `llma-e2e-swarm-root-${process.env.E2E_SWARM_PORT || 17801}`, 'Data'), { recursive: true });
    await enableCodeTools(llm, ['shell_exec']);
    await reopen(llm);
    let sleeper = process.platform === 'win32' ? 'ping -n 30 127.0.0.1' : 'sleep 30';
    await setScenarios([turn('shell', [
        { name: 'shell_exec', arguments: { command: 'echo shell-works' } },
        { name: 'shell_exec', arguments: { command: sleeper, timeoutSeconds: 1 } },
        { name: 'shell_exec', arguments: { command: 'echo hi', workingDirectory: '../..' } },
    ], 'Shell done.')]);
    await setMode(llm, 'auto');
    await send(llm, 'shell');
    await expect(lastAssistant(llm)).toContainText('Shell done.', { timeout: 30_000 });
    let [echoed, slept, escaped] = await toolResults();
    expect(echoed, JSON.stringify(echoed)).toMatchObject({ success: true, exitCode: 0 });
    expect(echoed.stdout).toContain('shell-works');
    expect(slept).toMatchObject({ success: false, killed: true, timedOut: true });
    expect(escaped.success).toBe(false);
    expect(escaped.error).toContain('sandbox');
});

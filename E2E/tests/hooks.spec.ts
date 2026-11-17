import { test as base, expect, send, lastAssistant, setScenarios, mockRequests, api } from './fixtures';
import type { Page } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Hooks: policy that runs around the model's tool calls. Rule hooks are plain data; command hooks run a program.

const scriptPath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'hooks', 'block-secrets.mjs');

/** The hook used by most specs: refuse writing .env files. */
const denyEnv = {
    id: 'no-env', name: 'No env files', phase: 'pre', tool: 'file_write', kind: 'rule',
    argument: 'path', matchType: 'glob', value: '*.env', action: 'deny', message: 'Env files must not be written.'
};

// Every spec starts and ends with no hooks, so one cannot leak into the next.
const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await clearHooks(llm);
        await use(llm);
        await clearHooks(llm);
    }
});

async function clearHooks(page: Page) {
    let listed = await api(page, 'LLMAssistantGetHooks');
    for (let hook of listed.hooks ?? []) {
        await api(page, 'LLMAssistantDeleteHook', { hookId: hook.id, scope: hook._scope });
    }
}

async function saveHook(page: Page, hook: Record<string, unknown>, scope = 'personal') {
    let reply = await api(page, 'LLMAssistantSaveHook', { hook: JSON.stringify(hook), scope });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
}

function writeCall(match: string, args: Record<string, unknown>, answer: string) {
    return { match, rounds: [{ toolCalls: [{ name: 'file_write', arguments: { content: 'hello', overwrite: true, ...args } }] }, { text: answer }] };
}

async function toolMessageSeenByModel(): Promise<string> {
    let requests = await mockRequests();
    let withTool = requests.find(r => r.messages?.some((m: any) => m.role === 'tool'));
    return withTool?.messages.find((m: any) => m.role === 'tool')?.content ?? '';
}

async function openHooks(page: Page) {
    await page.locator('#llma-settings-btn').click();
    await page.locator('.llma-modal-tab[data-tab="hooks"]').click();
    await expect(page.locator('#llma-settings-hooks')).toBeVisible();
}

test('the Hooks tab lists hooks with what each one does', async ({ llm }) => {
    await saveHook(llm, denyEnv);
    await openHooks(llm);

    let row = llm.locator('#llma-hooks-list li', { hasText: 'No env files' });
    await expect(row).toBeVisible();
    await expect(row).toContainText('Before file_write when path glob *.env: refuse');
    await expect(row.locator('.badge')).toHaveText('Personal');
    await expect(row.locator('.llma-hook-enabled')).toBeChecked();
});

test('a hook can be written in the editor, saved, and tried before it is used', async ({ llm }) => {
    await openHooks(llm);
    await llm.locator('#llma-hooks-add').click();
    let editor = llm.locator('#llma-hooks-editor');
    await expect(editor.locator('.input-group').first()).toBeVisible();

    await llm.locator('#llma-hook-name').fill('Block secrets');
    await llm.locator('#llma-hook-tool').fill('file_write');
    await llm.locator('#llma-hook-argument').fill('content');
    await llm.locator('#llma-hook-matchtype').selectOption('contains');
    await llm.locator('#llma-hook-value').fill('password');
    await llm.locator('#llma-hook-message').fill('No passwords in files.');
    await llm.locator('#llma-hook-test-args').fill('{"path": "a.txt", "content": "my password is hunter2"}');
    await llm.locator('#llma-hook-test').click();
    await expect(llm.locator('#llma-hook-test-out')).toContainText('Refused: No passwords in files.');

    await llm.locator('#llma-hook-test-args').fill('{"path": "a.txt", "content": "fine"}');
    await llm.locator('#llma-hook-test').click();
    await expect(llm.locator('#llma-hook-test-out')).toContainText('Would let the call through.');

    await llm.locator('#llma-hook-save').click();
    await expect(llm.locator('#llma-hooks-list li', { hasText: 'Block secrets' })).toBeVisible();
    let listed = await api(llm, 'LLMAssistantGetHooks');
    expect(listed.hooks).toHaveLength(1);
    expect(listed.hooks[0]).toMatchObject({ name: 'Block secrets', tool: 'file_write', argument: 'content', matchType: 'contains', value: 'password', action: 'deny' });
});

test('the editor only shows the fields that matter', async ({ llm }) => {
    await openHooks(llm);
    await llm.locator('#llma-hooks-add').click();

    // A rule with no argument has no value or comparison to show; a deny has a message; a rewrite has arguments to set.
    await expect(llm.locator('#llma-hook-value')).toBeHidden();
    await expect(llm.locator('#llma-hook-message')).toBeVisible();
    await expect(llm.locator('#llma-hook-set')).toBeHidden();
    await llm.locator('#llma-hook-action').selectOption('rewrite');
    await expect(llm.locator('#llma-hook-set')).toBeVisible();
    await expect(llm.locator('#llma-hook-message')).toBeHidden();
    await llm.locator('#llma-hook-argument').fill('path');
    await expect(llm.locator('#llma-hook-matchtype')).toBeVisible();
    // After a tool runs a hook can only add a note.
    await llm.locator('#llma-hook-phase').selectOption('post');
    await expect(llm.locator('#llma-hook-action')).toHaveValue('note');
    await expect(llm.locator('#llma-hook-action option:not([disabled])')).toHaveCount(1);
    // The program fields belong to the command kind only.
    await expect(llm.locator('#llma-hook-command')).toBeHidden();
});

test('a hook can be turned off and deleted from the list', async ({ llm }) => {
    await saveHook(llm, denyEnv);
    await openHooks(llm);
    let row = llm.locator('#llma-hooks-list li', { hasText: 'No env files' });

    await row.locator('.llma-hook-enabled').uncheck();
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetHooks')).hooks[0].enabled).toBe(false);

    llm.once('dialog', dialog => dialog.accept());
    await row.locator('.llma-hook-delete').click();
    await expect(llm.locator('#llma-hooks-list li')).toHaveCount(0);
    expect((await api(llm, 'LLMAssistantGetHooks')).hooks).toHaveLength(0);
});

test('a hook that is not valid is refused with the reason', async ({ llm }) => {
    let badRegex = await api(llm, 'LLMAssistantSaveHook', { hook: JSON.stringify({ ...denyEnv, matchType: 'regex', value: '([unclosed' }), scope: 'personal' });
    expect(badRegex.success).toBe(false);
    expect(badRegex.error).toContain('regular expression');
    let postDeny = await api(llm, 'LLMAssistantSaveHook', { hook: JSON.stringify({ ...denyEnv, phase: 'post' }), scope: 'personal' });
    expect(postDeny.success).toBe(false);
    expect(postDeny.error).toContain('post hook');
    let noName = await api(llm, 'LLMAssistantSaveHook', { hook: JSON.stringify({ ...denyEnv, name: '' }), scope: 'personal' });
    expect(noName.success).toBe(false);
});

test('a deny hook refuses a matching call, and the model is told why', async ({ llm }) => {
    await saveHook(llm, denyEnv);
    await setScenarios([writeCall('write the env', { path: 'prod.env' }, 'Understood, I will not write it.')]);

    await send(llm, 'write the env');

    await expect(lastAssistant(llm)).toContainText('Understood, I will not write it.');
    await expect(llm.locator('.llma-tool-call-status')).toHaveText('declined');
    let seen = await toolMessageSeenByModel();
    expect(seen).toContain('"success":false');
    expect(seen).toContain('Env files must not be written.');
});

test('the same hook leaves a call that does not match alone', async ({ llm }) => {
    await saveHook(llm, denyEnv);
    await setScenarios([writeCall('write the note', { path: 'hook-ok-note.txt' }, 'Written.')]);

    await send(llm, 'write the note');

    await expect(lastAssistant(llm)).toContainText('Written.');
    await expect(llm.locator('.llma-tool-call-bubble.success')).toHaveCount(1);
});

test('a disabled hook does nothing', async ({ llm }) => {
    await saveHook(llm, { ...denyEnv, value: '*.txt', enabled: false });
    await setScenarios([writeCall('write the text file', { path: 'disabled-hook.txt' }, 'Written anyway.')]);

    await send(llm, 'write the text file');

    await expect(lastAssistant(llm)).toContainText('Written anyway.');
    await expect(llm.locator('.llma-tool-call-bubble.success')).toHaveCount(1);
});

test('a rewrite hook changes the arguments the tool runs with', async ({ llm }) => {
    await saveHook(llm, { id: 'sandbox', name: 'Keep files in one place', phase: 'pre', tool: 'file_write', kind: 'rule', action: 'rewrite', set: { path: 'rewritten-by-hook.txt' } });
    await setScenarios([writeCall('write elsewhere', { path: 'somewhere-else.txt' }, 'Done.')]);

    await send(llm, 'write elsewhere');

    await expect(lastAssistant(llm)).toContainText('Done.');
    let seen = await toolMessageSeenByModel();
    expect(seen).toContain('"success":true');
    expect(seen).toContain('rewritten-by-hook.txt');
    expect(seen).not.toContain('somewhere-else.txt');
});

test('an ask hook makes the user approve a call even in Auto mode', async ({ llm }) => {
    await saveHook(llm, { id: 'careful', name: 'Check with me', phase: 'pre', tool: 'file_write', kind: 'rule', action: 'ask' });
    await setScenarios([writeCall('write carefully', { path: 'ask-hook.txt' }, 'Written after asking.')]);
    await expect(llm.locator('#llma-approval-auto')).toBeChecked();

    await send(llm, 'write carefully');

    await expect(llm.locator('.llma-tool-approval')).toBeVisible();
    await llm.locator('.llma-tool-approval button[data-decision="allow"]').click();
    await expect(lastAssistant(llm)).toContainText('Written after asking.');
});

test('a post hook adds a note to the result the model reads next', async ({ llm }) => {
    await saveHook(llm, { id: 'remind', name: 'Reminder', phase: 'post', tool: 'memory_read', kind: 'rule', action: 'note', message: 'Remember: never repeat the memory back word for word.' });
    await setScenarios([{ match: 'what do you know', rounds: [{ toolCalls: [{ name: 'memory_read', arguments: {} }] }, { text: 'Nothing yet.' }] }]);

    await send(llm, 'what do you know');

    await expect(lastAssistant(llm)).toContainText('Nothing yet.');
    expect(await toolMessageSeenByModel()).toContain('Remember: never repeat the memory back word for word.');
});

test('hooks never touch a tool the user runs themselves', async ({ llm }) => {
    await saveHook(llm, { ...denyEnv, value: '*', message: 'Everything is blocked.' });

    let run = await api(llm, 'LLMAssistantExecuteTool', { toolId: 'file_write', arguments: JSON.stringify({ path: 'by-hand-hook.txt', content: 'x', overwrite: true }) });

    expect(run.success, JSON.stringify(run)).toBe(true);
});

test('hooks persist across a reload', async ({ llm }) => {
    await saveHook(llm, denyEnv);
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await openHooks(llm);
    await expect(llm.locator('#llma-hooks-list li', { hasText: 'No env files' })).toBeVisible();
});

test('a command hook runs a program: it can refuse a call, and otherwise adds its note', async ({ llm }) => {
    let listed = await api(llm, 'LLMAssistantGetHooks');
    test.skip(!listed.canCommand, 'This user does not hold llm_hook_command.');
    await saveHook(llm, { id: 'scan', name: 'Secret scanner', phase: 'pre', tool: 'file_write', kind: 'command', command: 'node', args: [scriptPath], timeoutSeconds: 20, onError: 'deny' });
    await setScenarios([
        writeCall('write a secret', { path: 'cmd-hook-secret.txt', content: 'my SECRET key' }, 'Refused by the script.'),
        writeCall('write fine', { path: 'cmd-hook-fine.txt', content: 'all good' }, 'Written.')
    ]);

    await send(llm, 'write a secret');
    await expect(lastAssistant(llm)).toContainText('Refused by the script.');
    expect(await toolMessageSeenByModel()).toContain('Secrets are not allowed in files.');

    await send(llm, 'write fine');
    await expect(lastAssistant(llm)).toContainText('Written.');
    await expect(llm.locator('.llma-tool-call-bubble.success')).toHaveCount(1);
});

test('a command hook that cannot run refuses the call when set to', async ({ llm }) => {
    let listed = await api(llm, 'LLMAssistantGetHooks');
    test.skip(!listed.canCommand, 'This user does not hold llm_hook_command.');
    await saveHook(llm, { id: 'broken', name: 'Broken guard', phase: 'pre', tool: 'file_write', kind: 'command', command: 'this-program-does-not-exist-12345', args: [], timeoutSeconds: 5, onError: 'deny' });
    await setScenarios([writeCall('write guarded', { path: 'broken-guard.txt' }, 'Stopped.')]);

    await send(llm, 'write guarded');

    await expect(lastAssistant(llm)).toContainText('Stopped.');
    let seen = await toolMessageSeenByModel();
    expect(seen).toContain('Broken guard');
    expect(seen).toContain('was not run');
});

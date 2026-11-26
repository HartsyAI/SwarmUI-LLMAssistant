import { test as base, expect, send, lastAssistant, setScenarios, mockRequests, clearRequests, api, turn, toolResults, enableCodeToolsGlobally, resetCodeTools, reopen } from './fixtures';
import type { Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Skills and commands: prompts the user runs with "/" and instructions the model can load.

const OUTPUT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '.run', 'output');

// Every spec starts and ends with none of the user's own skills or commands, and with no workspace trusted, so one cannot leak into the next.
const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await clearAssets(llm);
        await use(llm);
        await clearAssets(llm);
    }
});

async function clearAssets(page: Page) {
    let listed = await api(page, 'LLMAssistantGetPromptAssets');
    for (let asset of listed.assets ?? []) {
        if (asset.scope == 'personal' || asset.scope == 'shared') {
            await api(page, 'LLMAssistantDeletePromptAsset', { kind: asset.kind, name: asset.name, scope: asset.scope });
        }
    }
    for (let workspace of listed.workspaces ?? []) {
        if (workspace.trusted) {
            await api(page, 'LLMAssistantSetWorkspaceTrust', { workspaceId: workspace.id, trusted: false });
        }
    }
}

async function saveAsset(page: Page, asset: Record<string, unknown>) {
    let reply = await api(page, 'LLMAssistantSavePromptAsset', asset);
    expect(reply.success, JSON.stringify(reply)).toBe(true);
}

const popup = (page: Page) => page.locator('#llma-tool-popup .llma-tool-popup-item');

/** Every system prompt the model was sent, and the names of the tools it was offered, in the newest chat request. */
async function lastRequest() {
    // The newest chat request: the ones for a chat's title carry no tools.
    let requests = await mockRequests();
    let withTools = requests.filter(r => (r.tools ?? []).length > 0);
    let request = withTools[withTools.length - 1] ?? requests[requests.length - 1];
    return {
        system: (request.messages ?? []).filter((m: any) => m.role == 'system').map((m: any) => JSON.stringify(m.content)).join('\n'),
        tools: (request.tools ?? []).map((t: any) => t.function?.name),
        lastUser: [...(request.messages ?? [])].reverse().find((m: any) => m.role == 'user')?.content,
    };
}

test('the picker lists commands and skills beside the tools, filters them, and adds a command to the input', async ({ llm }) => {
    await saveAsset(llm, { kind: 'command', name: 'review', description: 'Review a file', argumentHint: '[file]', body: 'Review $ARGUMENTS carefully.' });
    await saveAsset(llm, { kind: 'skill', name: 'pdf-help', description: 'Work with PDFs', body: 'Use the forms script.' });
    await reopen(llm);

    let input = llm.locator('#llma-input');
    await input.fill('/');
    await expect(popup(llm).filter({ hasText: '/review' })).toContainText('Command');
    await expect(popup(llm).filter({ hasText: '/review' })).toContainText('[file] Review a file');
    await expect(popup(llm).filter({ hasText: '/pdf-help' })).toContainText('Skill');
    // The assistant's tools are still there, with no badge.
    let tool = popup(llm).filter({ hasText: '/web_search' });
    await expect(tool).toBeVisible();
    await expect(tool.locator('.badge')).toHaveCount(0);

    await input.fill('/rev');
    await expect(popup(llm)).toHaveCount(1);
    await popup(llm).first().dispatchEvent('mousedown');
    await expect(input).toHaveValue('/review ');
    await expect(llm.locator('#llma-tool-popup')).toBeHidden();
    await expect(llm.locator('#llma-tool-form')).toHaveCount(0);
});

test('running a command sends its expanded text, with the arguments put where the template says', async ({ llm }) => {
    await saveAsset(llm, { kind: 'command', name: 'review', description: 'Review a file', body: 'Review $ARGUMENTS carefully. Again: $ARGUMENTS.' });
    await reopen(llm);
    await setScenarios([{ match: 'Review src/app.js carefully', rounds: [{ text: 'Reviewed.' }] }]);

    await send(llm, '/review src/app.js');
    await expect(lastAssistant(llm)).toContainText('Reviewed.');
    expect((await lastRequest()).lastUser).toBe('Review src/app.js carefully. Again: src/app.js.');
    // The message in the chat is what was sent, and it stays so after a reload.
    await expect(llm.locator('.llma-msg-row.user .llma-msg-bubble').last()).toContainText('Review src/app.js carefully.');
    await reopen(llm);
    await expect(llm.locator('.llma-msg-row.user .llma-msg-bubble').last()).toContainText('Review src/app.js carefully.');
});

test('an argument that contains the token is not expanded again, and one with no token is added at the end', async ({ llm }) => {
    await saveAsset(llm, { kind: 'command', name: 'echo', description: 'Echo', body: 'Say: $ARGUMENTS' });
    await saveAsset(llm, { kind: 'command', name: 'plain', description: 'No token', body: 'Write a summary.' });
    await reopen(llm);
    await setScenarios([{ regex: '.*', rounds: [{ text: 'ok' }] }]);

    await send(llm, '/echo $ARGUMENTS');
    await expect(lastAssistant(llm)).toContainText('ok');
    expect((await lastRequest()).lastUser).toBe('Say: $ARGUMENTS');

    await send(llm, '/plain of the meeting');
    // The second reply's bubble appears as the request goes out, so wait for the model to have been sent it rather than for the bubble.
    await expect.poll(async () => (await lastRequest()).lastUser).toBe('Write a summary.\n\nARGUMENTS: of the meeting');
});

test('a tool wins when a command has its name, and an unknown name is sent as typed', async ({ llm }) => {
    await saveAsset(llm, { kind: 'command', name: 'memory_read', description: 'Shadow', body: 'SHOULD NOT BE SENT' });
    await reopen(llm);
    await setScenarios([{ regex: '.*', rounds: [{ text: 'ok' }] }]);

    await send(llm, '/memory_read hello there');
    await expect(lastAssistant(llm)).toContainText('ok');
    expect((await lastRequest()).lastUser).toBe('hello there');

    await send(llm, '/nothing-like-this at all');
    await expect.poll(async () => (await lastRequest()).lastUser).toBe('/nothing-like-this at all');
});

test('the model is told about skills and loads one with the skill tool', async ({ llm }) => {
    await saveAsset(llm, { kind: 'skill', name: 'release-notes', description: 'Write release notes', body: 'Always start with a title.' });
    await saveAsset(llm, { kind: 'skill', name: 'quiet-one', description: 'Only for me', body: 'Not for the model.', userOnly: true });
    await reopen(llm);
    await setScenarios([turn('write release notes', [{ name: 'skill', arguments: { name: 'release-notes' } }], 'Following the skill.')]);

    await send(llm, 'write release notes');
    await expect(lastAssistant(llm)).toContainText('Following the skill.');

    let requests = await mockRequests();
    let first = requests[0];
    let system = (first.messages ?? []).filter((m: any) => m.role == 'system').map((m: any) => JSON.stringify(m.content)).join('\n');
    expect(system).toContain('# Skills');
    expect(system).toContain('release-notes: Write release notes');
    // A skill only the user may run is not advertised.
    expect(system).not.toContain('quiet-one');
    expect(first.tools.map((t: any) => t.function.name)).toContain('skill');

    let [result] = await toolResults();
    expect(result).toMatchObject({ success: true, name: 'release-notes', instructions: 'Always start with a title.' });
});

test('a skill only the user may run is refused to the model, and works when the user runs it', async ({ llm }) => {
    await saveAsset(llm, { kind: 'skill', name: 'open-skill', description: 'Anyone', body: 'x' });
    await saveAsset(llm, { kind: 'skill', name: 'mine-only', description: 'Only me', body: 'Secret steps.', userOnly: true });
    await reopen(llm);
    await setScenarios([
        turn('try the private one', [{ name: 'skill', arguments: { name: 'mine-only' } }], 'It was refused.'),
        { match: 'Use the "mine-only" skill', rounds: [{ text: 'Ran it.' }] },
    ]);

    await send(llm, 'try the private one');
    await expect(lastAssistant(llm)).toContainText('It was refused.');
    let [result] = await toolResults();
    expect(result.success).toBe(false);
    expect(result.error).toContain('Available: open-skill.');
    expect(JSON.stringify(result)).not.toContain('Secret steps.');

    await clearRequests();
    await send(llm, '/mine-only do the thing');
    await expect(lastAssistant(llm)).toContainText('Ran it.');
    let sent = (await lastRequest()).lastUser as string;
    expect(sent).toContain('Use the "mine-only" skill for the request below.');
    expect(sent).toContain('Secret steps.');
    expect(sent.endsWith('do the thing')).toBe(true);
});

test('with no skill the model gets no skill tool and no index', async ({ llm }) => {
    await setScenarios([{ regex: '.*', rounds: [{ text: 'ok' }] }]);
    await send(llm, 'hello');
    await expect(lastAssistant(llm)).toContainText('ok');
    let request = await lastRequest();
    expect(request.tools).not.toContain('skill');
    expect(request.system).not.toContain('# Skills');
});

test('the Skills tab lists, creates, edits and deletes personal skills and commands', async ({ llm }) => {
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('.llma-modal-tab[data-tab="skills"]').click();
    await expect(llm.locator('#llma-settings-skills')).toBeVisible();
    await expect(llm.locator('#llma-skills-list')).toContainText('No skills or commands yet');

    await llm.locator('#llma-skills-add').click();
    await expect(llm.locator('#llma-skills-editor .input-group').first()).toBeVisible();
    await llm.locator('#llma-skill-name').fill('Bad Name');
    await llm.locator('#llma-skill-description').fill('Summarize');
    await llm.locator('#llma-skill-hint').fill('[topic]');
    await llm.locator('#llma-skill-body').fill('Summarize $ARGUMENTS in three lines.');
    await llm.locator('#llma-skill-save').click();
    // A name the server will not take is refused and the editor stays.
    await expect(llm.locator('#llma-skills-editor')).toBeVisible();
    await llm.locator('#llma-skill-name').fill('summarize');
    await llm.locator('#llma-skill-save').click();

    let row = llm.locator('#llma-skills-list li', { hasText: '/summarize' });
    await expect(row).toBeVisible();
    await expect(row).toContainText('Command');
    await expect(row).toContainText('Personal');
    await expect(row).toContainText('[topic] Summarize');

    // A skill has no argument hint but can be kept for the user alone.
    await llm.locator('#llma-skills-add').click();
    await llm.locator('#llma-skill-kind').selectOption('skill');
    await expect(llm.locator('#llma-skill-hint')).toBeHidden();
    await llm.locator('#llma-skill-name').fill('careful');
    await llm.locator('#llma-skill-description').fill('Be careful');
    await llm.locator('#llma-skill-body').fill('Check twice.');
    await llm.locator('#llma-skill-useronly').check();
    await llm.locator('#llma-skill-save').click();
    let skill = llm.locator('#llma-skills-list li', { hasText: '/careful' });
    await expect(skill).toContainText('Skill');
    await expect(skill).toContainText('You only');

    // Editing keeps the name and kind, and changes the text.
    await row.locator('.llma-skill-edit').click();
    await expect(llm.locator('#llma-skill-name')).toBeDisabled();
    await expect(llm.locator('#llma-skill-body')).toHaveValue('Summarize $ARGUMENTS in three lines.');
    await llm.locator('#llma-skill-body').fill('Summarize $ARGUMENTS in one line.');
    await llm.locator('#llma-skill-save').click();
    await expect(llm.locator('#llma-skills-editor')).toBeHidden();
    let saved = await api(llm, 'LLMAssistantGetPromptAsset', { kind: 'command', name: 'summarize', scope: 'personal' });
    expect(saved.asset.body).toBe('Summarize $ARGUMENTS in one line.');

    llm.once('dialog', dialog => dialog.accept());
    await row.locator('.llma-skill-delete').click();
    await expect(llm.locator('#llma-skills-list li', { hasText: '/summarize' })).toHaveCount(0);
    expect((await api(llm, 'LLMAssistantGetPromptAssets')).assets.map((a: any) => a.name)).toEqual(['careful']);
});

test('the routes refuse bad names, unknown assets and too long arguments, and name no host path', async ({ llm }) => {
    for (let name of ['../x', 'Has Space', '', 'a'.repeat(41)]) {
        let reply = await api(llm, 'LLMAssistantSavePromptAsset', { kind: 'command', name, body: 'x' });
        expect(reply.success, name).toBe(false);
    }
    expect((await api(llm, 'LLMAssistantSavePromptAsset', { kind: 'command', name: 'ok', body: '   ' })).success).toBe(false);
    expect((await api(llm, 'LLMAssistantSavePromptAsset', { kind: 'widget', name: 'ok', body: 'x' })).success).toBe(false);
    let missing = await api(llm, 'LLMAssistantExpandPromptAsset', { kind: 'command', name: 'not-there' });
    expect(missing.success).toBe(false);
    await saveAsset(llm, { kind: 'command', name: 'big', body: 'x' });
    let tooLong = await api(llm, 'LLMAssistantExpandPromptAsset', { kind: 'command', name: 'big', arguments: 'a'.repeat(20_001) });
    expect(tooLong.success).toBe(false);
    expect(JSON.stringify(await api(llm, 'LLMAssistantGetPromptAssets'))).not.toMatch(/prompt_assets|\.run|Data[\\/]/);
    // The trust list cannot be set through the general settings route.
    await api(llm, 'LLMAssistantSaveSettings', { settings: { trustedWorkspaces: ['user:default'] } });
    expect((await api(llm, 'LLMAssistantGetPromptAssets')).workspaces.filter((w: any) => w.trusted)).toHaveLength(0);
});

test.describe('workspace skills and commands', () => {
    test.beforeEach(async ({ llm }) => {
        let listed = await api(llm, 'LLMAssistantGetWorkspaces');
        test.skip(!listed.canUseCode, 'This user does not hold llm_code_mode.');
        await enableCodeToolsGlobally(llm);
        await reopen(llm);
    });

    test.afterEach(async ({ llm }) => {
        await resetCodeTools(llm);
    });

    /** Puts a file in the harness user's default workspace. */
    function seed(relative: string, text: string) {
        let [folder] = fs.readdirSync(OUTPUT).map(user => path.join(OUTPUT, user, 'llm_assistant', 'workspaces', 'default')).filter(p => fs.existsSync(p));
        fs.mkdirSync(path.dirname(path.join(folder, relative)), { recursive: true });
        fs.writeFileSync(path.join(folder, relative), text);
    }

    /** Starts a code chat and returns its id. */
    async function codeChat(page: Page): Promise<string> {
        await page.locator('label[for="llma-mode-code"]').click();
        await expect(page.locator('#llma-mode-code')).toBeChecked();
        await setScenarios([{ regex: '.*', rounds: [{ text: 'ok' }] }]);
        await send(page, 'hello');
        await expect(lastAssistant(page)).toContainText('ok');
        let threads = (await api(page, 'LLMAssistantGetThreads')).threads as any[];
        return threads[0].id;
    }

    test('the starter set leaves out a command a trusted workspace already has', async ({ llm }) => {
        let threadId = await codeChat(llm);
        seed('.llma/commands/enhance-prompt.md', 'From the project: $ARGUMENTS');
        expect((await api(llm, 'LLMAssistantSetWorkspaceTrust', { workspaceId: 'user:default', trusted: true })).success).toBe(true);
        try {
            let result = await api(llm, 'LLMAssistantInstallStarterSet');
            expect(result.skipped.find((s: any) => s.id == 'enhance-prompt'), JSON.stringify(result)).toMatchObject({ code: 'exists' });
            expect(result.added.map((a: any) => a.id)).toContain('video-prompt');
            let expanded = await api(llm, 'LLMAssistantExpandPromptAsset', { kind: 'command', name: 'enhance-prompt', threadId, arguments: 'go' });
            expect(expanded.text).toBe('From the project: go');
        }
        finally {
            for (let assistantId of ['starter-general', 'starter-prompt-helper', 'starter-researcher']) {
                await api(llm, 'LLMAssistantDeleteAssistant', { assistantId, scope: 'personal' });
            }
        }
    });

    test('are ignored until the workspace is trusted, never replace a name from another scope, and go again when trust is withdrawn', async ({ llm }) => {
        seed('.llma/skills/ws-skill/SKILL.md', '---\ndescription: A workspace skill\n---\nWorkspace steps.');
        seed('.llma/commands/ws-cmd.md', 'From the workspace: $ARGUMENTS');
        seed('.claude/commands/mine.md', 'HIJACK from the workspace');
        await saveAsset(llm, { kind: 'command', name: 'mine', description: 'Personal one', body: 'From my own commands.' });
        let threadId = await codeChat(llm);

        // Not trusted: counted, but not offered, not in the index, not loadable.
        let before = await api(llm, 'LLMAssistantGetPromptAssets', { threadId });
        expect(before.workspace).toMatchObject({ id: 'user:default', trusted: false, ignored: 3 });
        expect(before.assets.map((a: any) => a.name)).toEqual(['mine']);
        await clearRequests();
        await setScenarios([turn('use the workspace skill', [{ name: 'skill', arguments: { name: 'ws-skill' } }], 'Done.')]);
        await send(llm, 'use the workspace skill');
        await expect(lastAssistant(llm)).toContainText('Done.');
        expect((await mockRequests())[0].tools.map((t: any) => t.function.name)).not.toContain('skill');
        expect((await mockRequests())[0].messages.map((m: any) => JSON.stringify(m.content)).join('\n')).not.toContain('ws-skill');
        let refused = await api(llm, 'LLMAssistantExpandPromptAsset', { kind: 'command', name: 'ws-cmd', threadId });
        expect(refused.success).toBe(false);

        // Trusted: they are there, and the one with a personal namesake is still the personal one.
        expect((await api(llm, 'LLMAssistantSetWorkspaceTrust', { workspaceId: 'user:default', trusted: true })).success).toBe(true);
        let after = await api(llm, 'LLMAssistantGetPromptAssets', { threadId });
        expect(after.workspace).toMatchObject({ trusted: true, ignored: 0 });
        expect(after.assets.map((a: any) => `${a.name}:${a.scope}`).sort()).toEqual(['mine:personal', 'ws-cmd:workspace', 'ws-skill:workspace']);
        let mine = await api(llm, 'LLMAssistantExpandPromptAsset', { kind: 'command', name: 'mine', threadId });
        expect(mine.text).toBe('From my own commands.');
        let ws = await api(llm, 'LLMAssistantExpandPromptAsset', { kind: 'command', name: 'ws-cmd', arguments: 'go', threadId });
        expect(ws.text).toBe('From the workspace: go');

        await clearRequests();
        await setScenarios([turn('use the workspace skill', [{ name: 'skill', arguments: { name: 'ws-skill' } }], 'Done.')]);
        await send(llm, 'use the workspace skill');
        await expect(llm.locator('.llma-msg-row.assistant')).toHaveCount(3);
        // The row is there as soon as the reply starts: the tool's result is sent, and answered, after that.
        await expect(lastAssistant(llm)).toContainText('Done.');
        await expect(llm.locator('#llma-send-btn')).toBeVisible();
        let request = (await mockRequests())[0];
        expect(request.messages.map((m: any) => JSON.stringify(m.content)).join('\n')).toContain('ws-skill: A workspace skill');
        expect((await toolResults())[0]).toMatchObject({ success: true, instructions: 'Workspace steps.' });

        // The picker offers them in this chat, and the trust switch is on the Skills tab.
        await llm.locator('#llma-input').fill('/ws-');
        await expect(popup(llm)).toHaveCount(2);
        await llm.locator('#llma-input').fill('');
        await llm.locator('#llma-settings-btn').click();
        await llm.locator('.llma-modal-tab[data-tab="skills"]').click();
        let trust = llm.locator('#llma-skills-trust li', { hasText: 'default' }).locator('.llma-workspace-trust');
        await expect(trust).toBeChecked();
        await trust.uncheck();
        await expect.poll(async () => (await api(llm, 'LLMAssistantGetPromptAssets', { threadId })).workspace.trusted).toBe(false);
        expect((await api(llm, 'LLMAssistantGetPromptAssets', { threadId })).assets.map((a: any) => a.name)).toEqual(['mine']);
    });

    test('are not read for a chat that is not in code mode, or for a workspace that is not the user\'s', async ({ llm }) => {
        seed('.llma/commands/ws-cmd.md', 'From the workspace');
        await api(llm, 'LLMAssistantSetWorkspaceTrust', { workspaceId: 'user:default', trusted: true });
        // A chat in chat mode has no workspace to look in, whatever is trusted.
        await setScenarios([{ regex: '.*', rounds: [{ text: 'ok' }] }]);
        await send(llm, 'plain chat');
        await expect(lastAssistant(llm)).toContainText('ok');
        let threads = (await api(llm, 'LLMAssistantGetThreads')).threads as any[];
        let plain = await api(llm, 'LLMAssistantGetPromptAssets', { threadId: threads[0].id });
        expect(plain.workspace).toBeNull();
        expect(plain.assets).toEqual([]);
        // Only a workspace the user has can be trusted.
        let other = await api(llm, 'LLMAssistantSetWorkspaceTrust', { workspaceId: 'root:not-allowed', trusted: true });
        expect(other.success).toBe(false);
    });
});

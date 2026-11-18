import { test, expect, send, lastAssistant, setScenarios, api, turn, enableCodeToolsGlobally, reopen, setMode, resetCodeTools } from './fixtures';
import type { Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Code mode, second half: the Changes tab (what the assistant's file tools changed, as diffs, with revert) and the Terminal tab (a command's output live).

test.beforeEach(async ({ llm }) => {
    let listed = await api(llm, 'LLMAssistantGetWorkspaces');
    test.skip(!listed.canUseCode, 'This user does not hold llm_code_mode.');
    await enableCodeToolsGlobally(llm);
    await reopen(llm);
});

test.afterEach(async ({ llm }) => {
    await resetCodeTools(llm);
});

const OUTPUT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '.run', 'output');

/** The folders of the harness user's default workspace (the user folder is made when the workspaces are first listed). */
function defaultWorkspaces(): string[] {
    return fs.readdirSync(OUTPUT).map(user => path.join(OUTPUT, user, 'llm_assistant', 'workspaces', 'default')).filter(p => fs.existsSync(p));
}

/** Puts a file in the default workspace before the chat starts, so the assistant edits something that was already there. */
function seed(relative: string, text: string) {
    let [folder] = defaultWorkspaces();
    fs.mkdirSync(path.dirname(path.join(folder, relative)), { recursive: true });
    fs.writeFileSync(path.join(folder, relative), text);
    return path.join(folder, relative);
}

async function goCode(page: Page) {
    await page.locator('label[for="llma-mode-code"]').click();
    await expect(page.locator('#llma-mode-code')).toBeChecked();
}

const tab = (page: Page, name: string) => page.locator(`.llma-code-tabs [data-code-tab="${name}"]`);

/** A turn that edits a file that was already there and creates a new one, with approvals off so it just runs. */
async function editAndCreate(page: Page) {
    seed('app.txt', 'alpha\nbeta\ngamma\n');
    await setScenarios([turn('change things', [
        { name: 'code_edit', arguments: { path: 'app.txt', oldText: 'beta', newText: 'BETA' } },
        { name: 'code_write', arguments: { path: 'docs/new.txt', content: 'fresh\n' } },
    ], 'Changed.')]);
    await goCode(page);
    await setMode(page, 'auto');
    await send(page, 'change things');
    await expect(lastAssistant(page)).toContainText('Changed.');
}

test('the panel has Files, Changes, Terminal and Tasks tabs', async ({ llm }) => {
    await goCode(llm);
    await expect(llm.locator('.llma-code-tabs .nav-link')).toHaveText(['Files', 'Changes', 'Terminal', 'Tasks']);
});

test('the Changes tab lists what the assistant changed with line counts and opens a diff', async ({ llm }) => {
    await editAndCreate(llm);

    await expect(tab(llm, 'changes')).toContainText('2');
    await tab(llm, 'changes').click();
    let rows = llm.locator('.llma-change');
    await expect(rows).toHaveCount(2);
    let edited = rows.filter({ hasText: 'app.txt' });
    await expect(edited.locator('.badge')).toHaveText('edited');
    await expect(edited).toContainText('+1');
    let created = rows.filter({ hasText: 'docs/new.txt' });
    await expect(created.locator('.badge')).toHaveText('new');

    await edited.locator('.llma-change-toggle').click();
    await expect(edited.locator('.llma-diff-del')).toHaveText('-beta');
    await expect(edited.locator('.llma-diff-add')).toHaveText('+BETA');
    await edited.locator('.llma-change-toggle').click();
    await expect(edited.locator('.llma-diff')).toHaveCount(0);
});

test('reverting puts an edited file back and removes one the assistant created', async ({ llm }) => {
    await editAndCreate(llm);
    llm.on('dialog', dialog => dialog.accept());
    await tab(llm, 'changes').click();
    let [folder] = defaultWorkspaces();

    await llm.locator('.llma-change', { hasText: 'app.txt' }).locator('.llma-change-revert').click();
    await expect(llm.locator('.llma-change')).toHaveCount(1);
    expect(fs.readFileSync(path.join(folder, 'app.txt'), 'utf8')).toBe('alpha\nbeta\ngamma\n');

    await llm.locator('.llma-change-revert-all').click();
    await expect(llm.locator('.llma-change')).toHaveCount(0);
    await expect(llm.locator('#llma-code-body')).toContainText('No changes');
    expect(fs.existsSync(path.join(folder, 'docs', 'new.txt'))).toBe(false);
    await expect(tab(llm, 'changes')).not.toContainText('2');
});

test('a file changed since the assistant wrote it asks before it is reverted, and a no keeps the changes', async ({ llm }) => {
    await editAndCreate(llm);
    let [folder] = defaultWorkspaces();
    fs.writeFileSync(path.join(folder, 'app.txt'), 'alpha\nBETA\ngamma\nmy own line\n');
    let questions: string[] = [];
    llm.on('dialog', dialog => {
        questions.push(dialog.message());
        // The first question is "put it back?"; the second is the warning that someone else changed it.
        if (questions.length == 1) {
            dialog.accept();
        }
        else {
            dialog.dismiss();
        }
    });
    await tab(llm, 'changes').click();
    await expect(llm.locator('.llma-change', { hasText: 'app.txt' })).toContainText('Changed since the assistant last wrote it');

    await llm.locator('.llma-change', { hasText: 'app.txt' }).locator('.llma-change-revert').click();
    await expect.poll(() => questions.length).toBe(2);
    expect(questions[1]).toContain('changed since the assistant last wrote it');
    expect(fs.readFileSync(path.join(folder, 'app.txt'), 'utf8')).toContain('my own line');
    await expect(llm.locator('.llma-change')).toHaveCount(2);

    questions.length = 0;
    llm.removeAllListeners('dialog');
    llm.on('dialog', dialog => dialog.accept());
    await llm.locator('.llma-change', { hasText: 'app.txt' }).locator('.llma-change-revert').click();
    await expect(llm.locator('.llma-change')).toHaveCount(1);
    expect(fs.readFileSync(path.join(folder, 'app.txt'), 'utf8')).toBe('alpha\nbeta\ngamma\n');
});

test('the changes are still there after a reload, and go when the chat is deleted', async ({ llm }) => {
    await editAndCreate(llm);
    let threadId = (await api(llm, 'LLMAssistantGetThreads')).threads[0].id;
    let journals = defaultWorkspaces().map(w => path.join(w, '..', '..', 'changes', threadId));
    expect(journals.some(j => fs.existsSync(j))).toBe(true);

    await reopen(llm);
    await llm.locator('.llma-thread-item').first().click();
    await expect(tab(llm, 'changes')).toContainText('2');

    expect((await api(llm, 'LLMAssistantDeleteThread', { threadId })).success).toBe(true);
    expect(journals.some(j => fs.existsSync(j))).toBe(false);
});

test('the change routes refuse paths outside the workspace and a chat that is not there', async ({ llm }) => {
    await editAndCreate(llm);
    let threadId = (await api(llm, 'LLMAssistantGetThreads')).threads[0].id;
    for (let bad of ['..', '../x', 'C:/Windows', '/etc/passwd']) {
        let reverted = await api(llm, 'LLMAssistantRevertWorkspaceChange', { threadId, path: bad, force: true });
        expect(reverted.success, bad).toBe(false);
        expect((await api(llm, 'LLMAssistantGetWorkspaceChange', { threadId, path: bad })).success, bad).toBe(false);
    }
    expect((await api(llm, 'LLMAssistantListWorkspaceChanges', { threadId: 'not-a-chat' })).success).toBe(false);
    // Nothing the routes said names a host path.
    let listed = await api(llm, 'LLMAssistantListWorkspaceChanges', { threadId });
    expect(listed.changes).toHaveLength(2);
    expect(JSON.stringify(listed)).not.toContain(OUTPUT.replace(/\\/g, '\\\\'));
});

test('a command\'s output shows in the Terminal tab while it runs, and its exit when it ends', async ({ llm }) => {
    // Prints a line, waits, prints another: the first must be on screen while the command is still going.
    let command = process.platform == 'win32'
        ? 'echo first-line& ping -n 5 127.0.0.1 >nul& echo second-line 1>&2'
        : 'echo first-line; sleep 4; echo second-line 1>&2';
    await setScenarios([turn('run slow', [{ name: 'code_bash', arguments: { command } }], 'Done running.')]);
    await goCode(llm);
    await setMode(llm, 'auto');
    await tab(llm, 'terminal').click();
    await expect(llm.locator('#llma-code-body')).toContainText('No commands yet');

    await send(llm, 'run slow');
    let screen = llm.locator('.llma-terminal');
    await expect(screen.locator('.llma-terminal-cmd')).toContainText(command.split('&')[0].split(';')[0]);
    await expect(screen.locator('.llma-terminal-status')).toHaveText('running…');
    await expect(screen).toContainText('first-line', { timeout: 3000 });
    // (The command line itself names second-line, so look at the output rows: nothing was printed to standard error yet.)
    await expect(screen.locator('.llma-terminal-err')).toHaveCount(0);
    await expect(screen.locator('.llma-terminal-status')).toHaveText('[exit 0]', { timeout: 20_000 });
    // Standard error is set apart from standard output.
    await expect(screen.locator('.llma-terminal-err')).toHaveText('second-line');
    await expect(screen.locator('.llma-terminal-line')).toHaveText('first-line');
    await expect(lastAssistant(llm)).toContainText('Done running.');
});

test('the Terminal tab keeps a chat\'s commands after a reload, from the saved results', async ({ llm }) => {
    await setScenarios([turn('say hi', [{ name: 'code_bash', arguments: { command: 'echo saved-output' } }], 'Said hi.')]);
    await goCode(llm);
    await setMode(llm, 'auto');
    await send(llm, 'say hi');
    await expect(lastAssistant(llm)).toContainText('Said hi.');
    await expect(tab(llm, 'terminal')).toContainText('1');

    await reopen(llm);
    await llm.locator('.llma-thread-item').first().click();
    await tab(llm, 'terminal').click();
    await expect(llm.locator('.llma-terminal-cmd')).toHaveText('$ echo saved-output');
    await expect(llm.locator('.llma-terminal-line')).toHaveText('saved-output');
    await expect(llm.locator('.llma-terminal-status')).toHaveText('[exit 0]');
});

test('a command that is not allowed shows why in the Terminal and prints nothing', async ({ llm }) => {
    await setScenarios([turn('plan only', [{ name: 'code_bash', arguments: { command: 'echo nope' } }], 'It was refused.')]);
    await goCode(llm);
    await setMode(llm, 'plan');
    await send(llm, 'plan only');
    await expect(lastAssistant(llm)).toContainText('It was refused.');
    await tab(llm, 'terminal').click();
    await expect(llm.locator('.llma-terminal-cmd')).toHaveText('$ echo nope');
    await expect(llm.locator('.llma-terminal-line')).toHaveCount(0);
    await expect(llm.locator('.llma-terminal-status')).not.toHaveText('[exit 0]');
});

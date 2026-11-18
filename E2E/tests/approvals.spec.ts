import { test, expect, send, lastAssistant, setScenarios, mockRequests, api } from './fixtures';
import type { Page } from '@playwright/test';

// Per-call tool approval: Ask asks before anything that changes something, Plan refuses changes, Auto (the default) runs everything.

let counter = 0;

/** A scripted model that calls file_write (a tool that changes something), then answers. */
function writeScenario(match: string, answer: string, file: string) {
    return {
        match,
        rounds: [
            { toolCalls: [{ name: 'file_write', arguments: { path: file, content: 'hello from e2e', overwrite: true } }] },
            { text: answer }
        ]
    };
}

function uniqueFile() {
    counter += 1;
    return `approval-${Date.now()}-${counter}.txt`;
}

/** Picks Ask, Auto or Plan in the composer's control. */
async function setMode(page: Page, mode: 'ask' | 'auto' | 'plan') {
    await page.locator(`label[for="llma-approval-${mode}"]`).click();
    await expect(page.locator(`#llma-approval-${mode}`)).toBeChecked();
}

/** The tool message the model was sent after its call, from the request that carries it. */
async function toolMessageSeenByModel(): Promise<string> {
    let requests = await mockRequests();
    let withTool = requests.find(r => r.messages?.some((m: any) => m.role === 'tool'));
    return withTool?.messages.find((m: any) => m.role === 'tool')?.content ?? '';
}

const approvalRow = (page: Page) => page.locator('.llma-tool-approval');

test('the composer offers Ask, Auto and Plan, and starts on Auto', async ({ llm }) => {
    await expect(llm.locator('#llma-approval-group.btn-group')).toBeVisible();
    await expect(llm.locator('#llma-approval-auto')).toBeChecked();
    await expect(llm.locator('#llma-approval-group label')).toHaveText(['Ask', 'Auto', 'Plan']);
});

test('Auto: a tool that changes something runs without asking', async ({ llm }) => {
    await setScenarios([writeScenario('write auto', 'Written.', uniqueFile())]);
    await send(llm, 'write auto');
    await expect(lastAssistant(llm)).toContainText('Written.');
    await expect(approvalRow(llm)).toHaveCount(0);
    await expect(llm.locator('.llma-tool-call-bubble.success')).toHaveCount(1);
});

test('Ask: the turn waits for the user, and Allow once runs the tool', async ({ llm }) => {
    await setScenarios([writeScenario('write ask', 'Written after approval.', uniqueFile())]);
    await setMode(llm, 'ask');
    await send(llm, 'write ask');

    await expect(approvalRow(llm)).toBeVisible();
    await expect(approvalRow(llm)).toContainText('Allow File Write?');
    await expect(llm.locator('.llma-tool-call-status')).toHaveText('needs approval');
    // The model has been asked once and is not asked again until the user answers.
    expect((await mockRequests()).filter(r => r.messages?.some((m: any) => m.role === 'tool'))).toHaveLength(0);
    await expect(llm.locator('#llma-stop-btn')).toBeVisible();

    await llm.locator('.llma-tool-approval button[data-decision="allow"]').click();

    await expect(lastAssistant(llm)).toContainText('Written after approval.');
    await expect(llm.locator('.llma-tool-call-bubble.success')).toHaveCount(1);
    await expect(approvalRow(llm)).toHaveCount(0);
    expect(await toolMessageSeenByModel()).toContain('"success":true');
});

test('Ask: Deny gives the model a refusal it can act on, and the step shows it was declined', async ({ llm }) => {
    await setScenarios([writeScenario('write deny', 'Understood, I will not write it.', uniqueFile())]);
    await setMode(llm, 'ask');
    await send(llm, 'write deny');
    await expect(approvalRow(llm)).toBeVisible();

    await llm.locator('.llma-tool-approval button[data-decision="deny"]').click();

    await expect(lastAssistant(llm)).toContainText('Understood, I will not write it.');
    await expect(llm.locator('.llma-tool-call-status')).toHaveText('declined');
    await expect(llm.locator('.llma-tool-retry-btn')).toHaveCount(0);
    let seen = await toolMessageSeenByModel();
    expect(seen).toContain('"success":false');
    expect(seen).toContain('declined');
});

test('Ask: Allow for this chat skips the question next time, for that tool only', async ({ llm }) => {
    await setScenarios([
        writeScenario('first write', 'First done.', uniqueFile()),
        writeScenario('second write', 'Second done.', uniqueFile())
    ]);
    await setMode(llm, 'ask');
    await send(llm, 'first write');
    await expect(approvalRow(llm)).toBeVisible();
    await llm.locator('.llma-tool-approval button[data-decision="allow_chat"]').click();
    await expect(lastAssistant(llm)).toContainText('First done.');

    await send(llm, 'second write');
    await expect(lastAssistant(llm)).toContainText('Second done.');
    await expect(approvalRow(llm)).toHaveCount(0);
    await expect(llm.locator('.llma-tool-call-bubble.success')).toHaveCount(2);
});

test('Ask: a tool that only reads never asks', async ({ llm }) => {
    await setScenarios([{ match: 'read it', rounds: [{ toolCalls: [{ name: 'memory_read', arguments: {} }] }, { text: 'Read.' }] }]);
    await setMode(llm, 'ask');
    await send(llm, 'read it');
    await expect(lastAssistant(llm)).toContainText('Read.');
    await expect(approvalRow(llm)).toHaveCount(0);
});

test('Plan: a change is refused without asking, with a message that tells the model what to do instead', async ({ llm }) => {
    await setScenarios([writeScenario('write plan', 'Here is my plan instead.', uniqueFile())]);
    await setMode(llm, 'plan');
    await send(llm, 'write plan');

    await expect(lastAssistant(llm)).toContainText('Here is my plan instead.');
    await expect(approvalRow(llm)).toHaveCount(0);
    await expect(llm.locator('.llma-tool-call-status')).toHaveText('declined');
    expect(await toolMessageSeenByModel()).toContain('Plan mode');
});

test('Stop while a call waits for approval ends the turn cleanly, and the chat works afterwards', async ({ llm }) => {
    await setScenarios([
        writeScenario('write stop', 'Never shown.', uniqueFile()),
        { match: 'still alive', rounds: [{ text: 'Yes, still here.' }] }
    ]);
    await setMode(llm, 'ask');
    await send(llm, 'write stop');
    await expect(approvalRow(llm)).toBeVisible();

    await llm.locator('#llma-stop-btn').click();

    await expect(llm.locator('#llma-stop-btn')).toBeHidden({ timeout: 15_000 });
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await expect(approvalRow(llm)).toHaveCount(0);
    await send(llm, 'still alive');
    await expect(lastAssistant(llm)).toContainText('Yes, still here.');
});

test('the chosen mode is saved on the chat and shown again after a reload', async ({ llm }) => {
    await setScenarios([{ match: 'hello', rounds: [{ text: 'Hi.' }] }]);
    await setMode(llm, 'ask');
    await send(llm, 'hello');
    await expect(lastAssistant(llm)).toContainText('Hi.');
    let listed = await api(llm, 'LLMAssistantGetThreads');
    let thread = await api(llm, 'LLMAssistantGetThread', { threadId: listed.threads[0].id });
    expect(JSON.parse(typeof thread.thread === 'string' ? thread.thread : JSON.stringify(thread.thread)).approvalMode).toBe('ask');

    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await llm.locator('.llma-thread-item').first().click();
    await expect(llm.locator('#llma-approval-ask')).toBeChecked();

    await setMode(llm, 'plan');
    await expect.poll(async () => {
        let again = await api(llm, 'LLMAssistantGetThread', { threadId: listed.threads[0].id });
        return JSON.parse(typeof again.thread === 'string' ? again.thread : JSON.stringify(again.thread)).approvalMode;
    }).toBe('plan');
});

test('Compare mode: each lane asks for its own call', async ({ llm }) => {
    await setScenarios([
        { ...writeScenario('write both', 'Lane A wrote it.', uniqueFile()), model: 'mock-model' },
        { ...writeScenario('write both', 'Lane B wrote it.', uniqueFile()), model: 'mock-model-b' }
    ]);
    await setMode(llm, 'ask');
    await llm.locator('#llma-compare-toggle').click();
    await llm.locator('#llma-model-select-b').selectOption('mock-model-b');
    await send(llm, 'write both');

    await expect(approvalRow(llm)).toHaveCount(2);
    for (let i = 0; i < 2; i++) {
        await llm.locator('.llma-tool-approval button[data-decision="allow"]').first().click();
    }
    await expect(llm.locator('body')).toContainText('Lane A wrote it.');
    await expect(llm.locator('body')).toContainText('Lane B wrote it.');
});

test('a tool the user runs themselves is never gated, and a bogus answer is refused', async ({ llm }) => {
    await setMode(llm, 'ask');
    let run = await api(llm, 'LLMAssistantExecuteTool', { toolId: 'file_write', arguments: JSON.stringify({ path: uniqueFile(), content: 'by hand', overwrite: true }) });
    expect(run.success, JSON.stringify(run)).toBe(true);
    let bogus = await api(llm, 'LLMAssistantResolveToolApproval', { approvalId: 'not-a-real-id', decision: 'allow' });
    expect(bogus.resolved).toBe(false);
    let badDecision = await api(llm, 'LLMAssistantResolveToolApproval', { approvalId: 'x', decision: 'maybe' });
    expect(badDecision.success).toBe(false);
});

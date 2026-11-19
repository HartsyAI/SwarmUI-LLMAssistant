import { test as base, expect } from '@playwright/test';
import type { Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export { expect };

const MOCK = `http://localhost:${process.env.E2E_MOCK_PORT || 17802}`;

/** One scripted reply round; see E2E/MockLLM/Program.cs for the full shape. */
export type MockRound = {
    text?: string;
    reasoning?: string;
    toolCalls?: { name: string; arguments?: unknown; id?: string }[];
    usage?: { prompt_tokens: number; completion_tokens: number };
    delayMs?: number;
    chunkDelayMs?: number;
    error?: { status: number; message: string };
};

/** A rule: when the last user message contains `match`, answer with these rounds in order. */
export type MockRule = { match?: string; regex?: string; model?: string; rounds: MockRound[] };

/** Replaces the mock's scenario list. */
export async function setScenarios(rules: MockRule[]) {
    let response = await fetch(`${MOCK}/__scenarios`, { method: 'PUT', body: JSON.stringify(rules) });
    expect(response.ok).toBeTruthy();
}

/** Every chat-completions request body the mock has seen, oldest first. */
export async function mockRequests(): Promise<any[]> {
    let response = await fetch(`${MOCK}/__requests`);
    return (await response.json()).map((r: any) => r.body);
}

/** Forgets the mock's recorded requests. */
export async function clearRequests() {
    await fetch(`${MOCK}/__requests`, { method: 'DELETE' });
}

/** Calls a SwarmUI API route from inside the page, with the page's own session. */
export async function api(page: Page, route: string, body: Record<string, unknown> = {}): Promise<any> {
    return await page.evaluate(async ([r, b]) => {
        let response = await fetch(`/API/${r}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            // core declares `session_id` with `let`, so it is a script-scope global, not a window property.
            body: JSON.stringify({ session_id: (0, eval)('session_id'), ...(b as object) })
        });
        return await response.json();
    }, [route, body] as const);
}

/** Types a message into the composer and sends it with Enter. */
export async function send(page: Page, text: string) {
    let input = page.locator('#llma-input');
    await input.fill(text);
    await input.press('Enter');
}

/** The visible text of the newest assistant message. */
export function lastAssistant(page: Page) {
    return page.locator('.llma-msg-row.assistant .llma-msg-bubble').last();
}

/** Opens the LLM Assistant main tab. The harness has no text-to-image backend, so core shows its red "No backends present"
 * bar over the tab strip and swallows real clicks; the tab link is activated directly instead. */
export async function openTab(page: Page) {
    await page.waitForSelector('#maintab_llmassistant', { state: 'attached' });
    await page.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
}

export const CODE = ['code_read', 'code_write', 'code_edit', 'code_glob', 'code_grep', 'code_bash', 'code_todo'];
export const BUILTINS = ['generate_image', 'create_image_preset', 'caption_image', 'fuse_image_descriptions', 'batch_caption_folder', 'web_search', 'file_read', 'file_write', 'http_request', 'shell_exec', 'memory_write', 'memory_read', 'swarm_docs', 'set_led_profile', 'set_volume', 'mute_mic'];

/** One scripted turn: the model makes these calls in order, one per round, then answers. */
export function turn(match: string, calls: { name: string; arguments: unknown }[], answer: string) {
    return { match, rounds: [...calls.map(call => ({ toolCalls: [call] })), { text: answer }] };
}

/** The results the model was sent for its tool calls, in order, from the last request. */
export async function toolResults(): Promise<any[]> {
    // The newest request that carries tool results (the one after the title request is not it).
    let requests = (await mockRequests()).filter(r => (r.messages ?? []).some((m: any) => m.role === 'tool'));
    let last = requests[requests.length - 1];
    // Earlier turns' results are in the history too; the newest turn's are the ones after the last user message.
    let messages: any[] = last?.messages ?? [];
    let lastUser = messages.map(m => m.role).lastIndexOf('user');
    return messages.slice(lastUser + 1).filter((m: any) => m.role === 'tool').map((m: any) => JSON.parse(m.content));
}

/** Switches on the code tools (and shell_exec) for everyone and gives them to the default assistant. */
export async function enableCodeTools(page: Page, extra: string[] = []) {
    let tools = (await api(page, 'LLMAssistantGetTools')).tools as any[];
    for (let id of [...CODE, ...extra]) {
        let tool = tools.find(t => t.id === id);
        let saved = await api(page, 'LLMAssistantSaveTool', { tool: { ...tool, enabled: true }, scope: 'shared' });
        expect(saved.success, `${id}: ${JSON.stringify(saved)}`).toBe(true);
    }
    let current = (await api(page, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant;
    let saved = await api(page, 'LLMAssistantSaveAssistant', { assistant: { ...current, enabledToolIds: [...BUILTINS, ...CODE] }, scope: 'personal' });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
}

export async function reopen(page: Page) {
    await page.reload();
    await page.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await expect(page.locator('#llma-model-select option[value="mock-model"]')).toHaveCount(1, { timeout: 30_000 });
}

export async function setMode(page: Page, mode: 'ask' | 'auto' | 'plan') {
    await page.locator(`label[for="llma-approval-${mode}"]`).click();
    await expect(page.locator(`#llma-approval-${mode}`)).toBeChecked();
}


/** Puts the code tools back off (and shell_exec, if a spec switched it on), and the default assistant back as it was. */
export async function resetCodeTools(page: Page) {
    let tools = (await api(page, 'LLMAssistantGetTools')).tools as any[];
    for (let id of [...CODE, 'shell_exec']) {
        let tool = tools.find(t => t.id === id);
        if (tool?.enabled) {
            await api(page, 'LLMAssistantSaveTool', { tool: { ...tool, enabled: false }, scope: 'shared' });
        }
    }
    await api(page, 'LLMAssistantDeleteAssistant', { assistantId: 'default', scope: 'personal' });
    clearWorkspaces();
}

/** Empties every user's workspaces (they are folders under the harness's throwaway Output), so one spec's files never show up in the next. */
export function clearWorkspaces() {
    let output = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '.run', 'output');
    if (!fs.existsSync(output)) {
        return;
    }
    for (let user of fs.readdirSync(output)) {
        fs.rmSync(path.join(output, user, 'llm_assistant', 'workspaces'), { recursive: true, force: true });
    }
}

export const SESSION = ['session_start', 'session_send', 'session_list', 'session_read', 'session_wait', 'session_stop', 'session_archive'];

/** Switches the session tools on for everyone and gives them to the default assistant. */
export async function enableSessionTools(page: Page) {
    let tools = (await api(page, 'LLMAssistantGetTools')).tools as any[];
    for (let id of SESSION) {
        let saved = await api(page, 'LLMAssistantSaveTool', { tool: { ...tools.find(t => t.id === id), enabled: true }, scope: 'shared' });
        expect(saved.success, `${id}: ${JSON.stringify(saved)}`).toBe(true);
    }
    let current = (await api(page, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant;
    let saved = await api(page, 'LLMAssistantSaveAssistant', { assistant: { ...current, enabledToolIds: [...BUILTINS, ...SESSION] }, scope: 'personal' });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
}

/** Puts the session tools back off and the default assistant back as it was. */
export async function resetSessionTools(page: Page) {
    let tools = (await api(page, 'LLMAssistantGetTools')).tools as any[];
    for (let id of SESSION) {
        let tool = tools.find(t => t.id === id);
        if (tool?.enabled) {
            await api(page, 'LLMAssistantSaveTool', { tool: { ...tool, enabled: false }, scope: 'shared' });
        }
    }
    await api(page, 'LLMAssistantDeleteAssistant', { assistantId: 'default', scope: 'personal' });
}

export const SCHEDULE = ['schedule_create', 'schedule_list', 'schedule_cancel'];

/** Switches the schedule and session tools on for everyone and gives them to the default assistant. */
export async function enableScheduleTools(page: Page) {
    await enableSessionTools(page);
    let tools = (await api(page, 'LLMAssistantGetTools')).tools as any[];
    for (let id of SCHEDULE) {
        let saved = await api(page, 'LLMAssistantSaveTool', { tool: { ...tools.find(t => t.id === id), enabled: true }, scope: 'shared' });
        expect(saved.success, `${id}: ${JSON.stringify(saved)}`).toBe(true);
    }
    let current = (await api(page, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant;
    let saved = await api(page, 'LLMAssistantSaveAssistant', { assistant: { ...current, enabledToolIds: [...BUILTINS, ...SESSION, ...SCHEDULE] }, scope: 'personal' });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
}

/** Puts the schedule and session tools back off and the default assistant back as it was. */
export async function resetScheduleTools(page: Page) {
    let tools = (await api(page, 'LLMAssistantGetTools')).tools as any[];
    for (let id of SCHEDULE) {
        let tool = tools.find(t => t.id === id);
        if (tool?.enabled) {
            await api(page, 'LLMAssistantSaveTool', { tool: { ...tool, enabled: false }, scope: 'shared' });
        }
    }
    await resetSessionTools(page);
}

/** Deletes every schedule the user has, so one that is still due cannot start a run in the next spec. */
export async function clearSchedules(page: Page) {
    let listed = await api(page, 'LLMAssistantGetSchedules');
    for (let schedule of listed.schedules ?? []) {
        await api(page, 'LLMAssistantDeleteSchedule', { scheduleId: schedule.id });
    }
}

/** Every embeddings request the mock has seen ({model, inputs}), oldest first. Kept apart from chat requests, so mockRequests() is unchanged. */
export async function embeddingRequests(): Promise<any[]> {
    return await (await fetch(`${MOCK}/__embeddings`)).json();
}

/** Forgets the mock's recorded embeddings requests. */
export async function clearEmbeddingRequests() {
    await fetch(`${MOCK}/__embeddings`, { method: 'DELETE' });
}

/** Makes the mock's embeddings answer with this status (0 makes them work again). */
export async function failEmbeddings(status: number) {
    await fetch(`${MOCK}/__embeddings/fail`, { method: 'PUT', body: JSON.stringify({ status }) });
}

/** Empties the user's knowledge library, switches embedding off, and makes the mock's embeddings work and forget what it saw, so one spec's documents never answer another's search. */
export async function clearKnowledge(page: Page) {
    await failEmbeddings(0);
    let listed = await api(page, 'LLMAssistantGetKnowledge');
    for (let doc of listed.documents ?? []) {
        await api(page, 'LLMAssistantDeleteKnowledge', { docId: doc.id });
    }
    if (listed.settings?.providerId || listed.settings?.model) {
        await api(page, 'LLMAssistantSetKnowledgeSettings', { providerId: '', model: '' });
    }
    await clearEmbeddingRequests();
}

/** Switches the knowledge search tool on for everyone and gives it to the default assistant. */
export async function enableKnowledgeTool(page: Page) {
    let tools = (await api(page, 'LLMAssistantGetTools')).tools as any[];
    let saved = await api(page, 'LLMAssistantSaveTool', { tool: { ...tools.find(t => t.id === 'search_knowledge'), enabled: true }, scope: 'shared' });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
    let current = (await api(page, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant;
    let assistant = await api(page, 'LLMAssistantSaveAssistant', { assistant: { ...current, enabledToolIds: [...BUILTINS, 'search_knowledge'] }, scope: 'personal' });
    expect(assistant.success, JSON.stringify(assistant)).toBe(true);
}

/** Puts the knowledge tool back off and the default assistant back as it was. */
export async function resetKnowledgeTool(page: Page) {
    let tools = (await api(page, 'LLMAssistantGetTools')).tools as any[];
    let tool = tools.find(t => t.id === 'search_knowledge');
    if (tool?.enabled) {
        await api(page, 'LLMAssistantSaveTool', { tool: { ...tool, enabled: false }, scope: 'shared' });
    }
    await api(page, 'LLMAssistantDeleteAssistant', { assistantId: 'default', scope: 'personal' });
}

/** Switches the code tools on for everyone but leaves every assistant's own list alone: a chat in code mode brings them itself. */
export async function enableCodeToolsGlobally(page: Page) {
    let tools = (await api(page, 'LLMAssistantGetTools')).tools as any[];
    for (let id of CODE) {
        let saved = await api(page, 'LLMAssistantSaveTool', { tool: { ...tools.find(t => t.id === id), enabled: true }, scope: 'shared' });
        expect(saved.success, `${id}: ${JSON.stringify(saved)}`).toBe(true);
    }
}

/** Test fixture: a page on the LLM Assistant tab with a clean thread list and a clean mock. */
export const test = base.extend<{ llm: Page }>({
    llm: async ({ page }, use) => {
        await setScenarios([]);
        await clearRequests();
        await page.goto('/Text2Image');
        await openTab(page);
        await expect(page.locator('#llma-model-select option[value="mock-model"]')).toHaveCount(1, { timeout: 30_000 });
        // The server keeps threads and UI state (active thread, compare mode) per user, so wipe both between tests.
        let listed = await api(page, 'LLMAssistantGetThreads');
        expect(listed.success).toBe(true);
        await clearSchedules(page);
        await clearKnowledge(page);
        // Deleting a chat deletes the sessions it started, so only the chats with no parent in the list are deleted.
        let ids = new Set((listed.threads ?? []).map((t: any) => t.id));
        for (let thread of listed.threads ?? []) {
            if (thread.parentThreadId && ids.has(thread.parentThreadId)) {
                continue;
            }
            let deleted = await api(page, 'LLMAssistantDeleteThread', { threadId: thread.id });
            expect(deleted.success).toBe(true);
        }
        let cleared = await api(page, 'LLMAssistantSetSessionState', { state: { activeThreadId: null, compareMode: null, compareModelB: null } });
        expect(cleared.success).toBe(true);
        await page.reload();
        await openTab(page);
        await expect(page.locator('#llma-model-select option[value="mock-model"]')).toHaveCount(1, { timeout: 30_000 });
        await use(page);
    }
});

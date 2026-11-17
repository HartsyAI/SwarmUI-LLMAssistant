import { test as base, expect } from '@playwright/test';
import type { Page } from '@playwright/test';

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
        for (let thread of listed.threads ?? []) {
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

import { test, expect, send, lastAssistant, setScenarios, mockRequests, api } from './fixtures';
import type { Page } from '@playwright/test';
import { spawn, type ChildProcess } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// MCP servers: tools from a program (stdio) or a URL (http) join the tool list, run through the same permission, approval and hook rules as any tool,
// and a server that fails is reported and recovers. The servers are E2E/MockMCP/server.mjs, a scripted MCP server on plain node.

const here = path.dirname(fileURLToPath(import.meta.url));
const serverScript = path.resolve(here, '..', 'MockMCP', 'server.mjs');
const callLog = path.join(os.tmpdir(), `llma-mcp-calls-${process.pid}.log`);
const HTTP_PORT = 17803;
const BUILTINS = ['generate_image', 'create_image_preset', 'caption_image', 'fuse_image_descriptions', 'batch_caption_folder', 'web_search', 'file_read', 'file_write', 'http_request', 'shell_exec', 'memory_write', 'memory_read', 'swarm_docs', 'set_led_profile', 'set_volume', 'mute_mic'];

let httpServer: ChildProcess | null = null;

test.beforeAll(async () => {
    httpServer = spawn(process.execPath, [serverScript, '--http', String(HTTP_PORT)], { stdio: 'ignore' });
    for (let i = 0; i < 50; i++) {
        try {
            await fetch(`http://127.0.0.1:${HTTP_PORT}/mcp`, { method: 'POST', body: '{}' });
            return;
        }
        catch {
            await new Promise(done => setTimeout(done, 100));
        }
    }
});

test.afterAll(() => {
    httpServer?.kill();
});

/** A stdio server running the mock; extra fields override. */
function stdioServer(id: string, extra: Record<string, unknown> = {}) {
    return { id, name: `Mock ${id}`, transport: 'stdio', command: 'node', args: [serverScript], env: { MOCK_MCP_LOG: callLog, MOCK_MCP_NAME: id }, timeoutSeconds: 60, ...extra };
}

/** Saves a server through the API and returns its reply. */
async function addServer(page: Page, server: Record<string, unknown>) {
    let reply = await api(page, 'LLMAssistantSaveMcpServer', { server: JSON.stringify(server) });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    return reply.server;
}

/** The server as the list route reports it, once it has left the "connecting" state. */
async function settled(page: Page, id: string) {
    let found: any = null;
    await expect.poll(async () => {
        found = (await api(page, 'LLMAssistantGetMcpServers')).servers.find((s: any) => s.id === id);
        return found?.state;
    }, { timeout: 30_000 }).not.toBe('connecting');
    return found;
}

/** Lets the default assistant use every tool an MCP server offers, which no assistant has until it is switched on. */
async function allowMcpTools(page: Page) {
    let tools = (await api(page, 'LLMAssistantGetTools')).tools as any[];
    let ids = [...BUILTINS, ...tools.filter(t => t.isDynamic).map(t => t.id)];
    let current = (await api(page, 'LLMAssistantGetAssistant', { assistantId: 'default' })).assistant;
    let saved = await api(page, 'LLMAssistantSaveAssistant', { assistant: { ...current, enabledToolIds: ids }, scope: 'personal' });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
}

async function setMode(page: Page, mode: 'ask' | 'auto' | 'plan') {
    await page.locator(`label[for="llma-approval-${mode}"]`).click();
    await expect(page.locator(`#llma-approval-${mode}`)).toBeChecked();
}

/** The calls the mock server saw, oldest first. */
function serverCalls(): { name: string; arguments: any }[] {
    return fs.existsSync(callLog) ? fs.readFileSync(callLog, 'utf8').split('\n').filter(l => l).map(l => JSON.parse(l)) : [];
}

/** The tool message the model was sent after its call. */
async function toolMessageSeenByModel(): Promise<string> {
    let requests = await mockRequests();
    let withTool = requests.find(r => r.messages?.some((m: any) => m.role === 'tool'));
    return withTool?.messages.find((m: any) => m.role === 'tool')?.content ?? '';
}

/** A scripted model that calls one tool, then answers. */
function callScenario(match: string, tool: string, args: unknown, answer: string) {
    return { match, rounds: [{ toolCalls: [{ name: tool, arguments: args }] }, { text: answer }] };
}

async function openMcp(page: Page) {
    await page.locator('#llma-settings-btn').click();
    await expect(page.locator('#llma-settings-overlay')).toBeVisible();
    await page.locator('.llma-modal-tab[data-tab="mcp"]').click();
}

test.beforeEach(async ({ llm }) => {
    fs.rmSync(callLog, { force: true });
    let listed = await api(llm, 'LLMAssistantGetMcpServers');
    test.skip(!listed.canManage, 'This user does not hold llm_mcp_manage.');
});

test.afterEach(async ({ llm }) => {
    for (let server of (await api(llm, 'LLMAssistantGetMcpServers')).servers ?? []) {
        await api(llm, 'LLMAssistantDeleteMcpServer', { serverId: server.id });
    }
    await api(llm, 'LLMAssistantDeleteAssistant', { assistantId: 'default', scope: 'personal' });
});

test('with no servers the tab says so and offers to add one', async ({ llm }) => {
    await openMcp(llm);
    await expect(llm.locator('#llma-mcp-list')).toContainText('No MCP servers yet');
    await expect(llm.locator('#llma-mcp-add')).toBeVisible();
});

test('a server is written in the editor, tried before it is saved, then saved and shown as ready with its tools', async ({ llm }) => {
    await openMcp(llm);
    await llm.locator('#llma-mcp-add').click();
    await llm.locator('#llma-mcp-id').fill('mock');
    await llm.locator('#llma-mcp-name').fill('Mock server');
    await llm.locator('#llma-mcp-command').fill('node');
    await llm.locator('#llma-mcp-args').fill(serverScript);
    await llm.locator('#llma-mcp-env').fill(`MOCK_MCP_LOG=${callLog}\nSECRET_TOKEN=hunter2`);
    await llm.locator('#llma-mcp-test').click();
    await expect(llm.locator('#llma-mcp-test-out')).toContainText('Connected (mock-mcp): 7 tools', { timeout: 30_000 });
    await expect(llm.locator('#llma-mcp-test-out')).toContainText('echo');
    // Trying it saved nothing.
    expect((await api(llm, 'LLMAssistantGetMcpServers')).servers).toHaveLength(0);

    await llm.locator('#llma-mcp-save').click();
    let row = llm.locator('#llma-mcp-list > ul > li', { hasText: 'Mock server' });
    await expect(row.locator('.llma-mcp-state')).toHaveText('Ready', { timeout: 30_000 });
    await expect(row.locator('.llma-mcp-tools-summary')).toHaveText('7 tools');
    await row.locator('.llma-mcp-tools-summary').click();
    await expect(row.locator('[data-tool-name="echo"] .badge')).toHaveText('read-only');
    await expect(row.locator('[data-tool-name="lookup"] .badge')).toHaveText('network');
    await expect(row.locator('[data-tool-name="write_note"] .badge')).toHaveText('changes things');
    await expect(row.locator('[data-tool-name="echo"] code')).toHaveText('mcp__mock__echo');
});

test('secrets are never sent back to the browser, and editing keeps them', async ({ llm }) => {
    await addServer(llm, stdioServer('mock', { env: { MOCK_MCP_LOG: callLog, SECRET_TOKEN: 'hunter2' } }));
    let listed = await api(llm, 'LLMAssistantGetMcpServers');
    expect(JSON.stringify(listed)).not.toContain('hunter2');
    // The general settings route is not a way around that.
    let general = await api(llm, 'LLMAssistantGetSettings');
    expect(JSON.stringify(general)).not.toContain('hunter2');
    expect(general.settings.mcpServers).toBeUndefined();
    expect(general.settings.hooks).toBeUndefined();
    expect(listed.servers[0].env.SECRET_TOKEN).toBe('••••••');

    await openMcp(llm);
    await llm.locator('#llma-mcp-list li', { hasText: 'Mock mock' }).locator('.llma-mcp-edit').click();
    await expect(llm.locator('#llma-mcp-env')).toHaveValue(/SECRET_TOKEN=•{6}/);
    await llm.locator('#llma-mcp-name').fill('Renamed');
    await llm.locator('#llma-mcp-save').click();
    await expect(llm.locator('#llma-mcp-list li', { hasText: 'Renamed' })).toBeVisible();
    // The program was still given the stored secret: the save did not replace it with the placeholder.
    await allowMcpTools(llm);
    let probe = await api(llm, 'LLMAssistantGetMcpServers');
    expect(probe.servers[0].name).toBe('Renamed');
});

test('a server\'s tools are in the tool list, labelled with the server, and cannot be edited there', async ({ llm }) => {
    await addServer(llm, stdioServer('mock'));
    await settled(llm, 'mock');
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('.llma-modal-tab[data-tab="tools"]').click();
    let row = llm.locator('.llma-tool-list-item[data-tool-id="mcp__mock__echo"]');
    await expect(row).toBeVisible();
    await expect(row).toContainText('Mock mock');
    await expect(row.locator('.llma-tool-edit')).toHaveCount(0);
    await expect(row.locator('.llma-tool-delete')).toHaveCount(0);
    // Built-in tools are still managed as before.
    await expect(llm.locator('.llma-tool-list-item[data-tool-id="file_read"] .llma-tool-edit')).toHaveCount(1);
});

test('the model calls a read-only server tool and the result reaches it', async ({ llm }) => {
    await addServer(llm, stdioServer('mock'));
    await settled(llm, 'mock');
    await allowMcpTools(llm);
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await setScenarios([callScenario('say echo', 'mcp__mock__echo', { text: 'hi there' }, 'It said hi.')]);
    await send(llm, 'say echo');
    await expect(lastAssistant(llm)).toContainText('It said hi.');
    expect(serverCalls()).toEqual([{ name: 'echo', arguments: { text: 'hi there' } }]);
    expect(await toolMessageSeenByModel()).toContain('echo: hi there');
    // The model is offered the tool under its id, with the server's own schema.
    let first = (await mockRequests())[0];
    let offered = first.tools.find((t: any) => t.function.name === 'mcp__mock__echo');
    expect(offered.function.parameters.required).toEqual(['text']);
});

test('Ask mode asks before a server tool that changes something or reaches out, but not before a read-only one', async ({ llm }) => {
    await addServer(llm, stdioServer('mock'));
    await settled(llm, 'mock');
    await allowMcpTools(llm);
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await setScenarios([
        callScenario('read it', 'mcp__mock__echo', { text: 'a' }, 'Read done.'),
        callScenario('write it', 'mcp__mock__write_note', { text: 'b' }, 'Write done.'),
        callScenario('look it up', 'mcp__mock__lookup', { query: 'c' }, 'Lookup done.')
    ]);
    await setMode(llm, 'ask');

    await send(llm, 'read it');
    await expect(lastAssistant(llm)).toContainText('Read done.');
    await expect(llm.locator('.llma-tool-approval')).toHaveCount(0);

    await send(llm, 'write it');
    await expect(llm.locator('.llma-tool-approval')).toBeVisible();
    await expect(llm.locator('.llma-tool-approval')).toContainText('Write Note');
    expect(serverCalls().map(c => c.name)).toEqual(['echo']);
    await llm.locator('.llma-tool-approval button[data-decision="allow"]').click();
    await expect(lastAssistant(llm)).toContainText('Write done.');
    expect(serverCalls().map(c => c.name)).toEqual(['echo', 'write_note']);

    await send(llm, 'look it up');
    await expect(llm.locator('.llma-tool-approval')).toBeVisible();
    await llm.locator('.llma-tool-approval button[data-decision="deny"]').click();
    await expect(lastAssistant(llm)).toContainText('Lookup done.');
    expect(serverCalls().map(c => c.name)).toEqual(['echo', 'write_note']);
});

test('a server marked Always ask is asked about even for its read-only tools', async ({ llm }) => {
    await addServer(llm, stdioServer('mock', { alwaysAsk: true }));
    await settled(llm, 'mock');
    await allowMcpTools(llm);
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await setScenarios([callScenario('read it', 'mcp__mock__echo', { text: 'a' }, 'Read done.')]);
    await setMode(llm, 'ask');
    await send(llm, 'read it');
    await expect(llm.locator('.llma-tool-approval')).toBeVisible();
    expect(serverCalls()).toEqual([]);
    await llm.locator('.llma-tool-approval button[data-decision="allow"]').click();
    await expect(lastAssistant(llm)).toContainText('Read done.');
});

test('resetting the shared settings takes the servers away from the model as well', async ({ llm }) => {
    await addServer(llm, stdioServer('mock'));
    await settled(llm, 'mock');
    expect((await api(llm, 'LLMAssistantGetTools')).tools.some((t: any) => t.isDynamic)).toBe(true);
    await api(llm, 'LLMAssistantResetSettings', { scope: 'shared' });
    expect((await api(llm, 'LLMAssistantGetMcpServers')).servers).toHaveLength(0);
    expect((await api(llm, 'LLMAssistantGetTools')).tools.some((t: any) => t.isDynamic)).toBe(false);
});

test('Plan mode lets a read-only server tool run and refuses the rest', async ({ llm }) => {
    await addServer(llm, stdioServer('mock'));
    await settled(llm, 'mock');
    await allowMcpTools(llm);
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await setScenarios([callScenario('read it', 'mcp__mock__echo', { text: 'a' }, 'Read done.'), callScenario('write it', 'mcp__mock__write_note', { text: 'b' }, 'Could not write.')]);
    await setMode(llm, 'plan');
    await send(llm, 'read it');
    await expect(lastAssistant(llm)).toContainText('Read done.');
    await send(llm, 'write it');
    await expect(lastAssistant(llm)).toContainText('Could not write.');
    expect(serverCalls().map(c => c.name)).toEqual(['echo']);
});

test('a hook can refuse a server tool by its id', async ({ llm }) => {
    await addServer(llm, stdioServer('mock'));
    await settled(llm, 'mock');
    await allowMcpTools(llm);
    let hook = await api(llm, 'LLMAssistantSaveHook', { hook: JSON.stringify({ id: 'no-mcp-write', name: 'No MCP writes', phase: 'pre', tool: 'mcp__*__write_note', kind: 'rule', action: 'deny', message: 'MCP writes are off.' }), scope: 'personal' });
    expect(hook.success).toBe(true);
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await setScenarios([callScenario('write it', 'mcp__mock__write_note', { text: 'b' }, 'Understood.')]);
    await send(llm, 'write it');
    await expect(lastAssistant(llm)).toContainText('Understood.');
    expect(await toolMessageSeenByModel()).toContain('MCP writes are off.');
    expect(serverCalls()).toEqual([]);
    await api(llm, 'LLMAssistantDeleteHook', { hookId: 'no-mcp-write', scope: 'personal' });
});

test('a tool the server reports as failed is a failure the model can read', async ({ llm }) => {
    await addServer(llm, stdioServer('mock'));
    await settled(llm, 'mock');
    await allowMcpTools(llm);
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await setScenarios([callScenario('break it', 'mcp__mock__fail', {}, 'It failed.')]);
    await send(llm, 'break it');
    await expect(lastAssistant(llm)).toContainText('It failed.');
    expect(await toolMessageSeenByModel()).toContain('this tool always fails');
    await expect(llm.locator('.llma-tool-call-bubble.error, .llma-tool-call-bubble.failed').first()).toBeVisible();
});

test('a call that runs past the server\'s time limit is given up on and the server stays up', async ({ llm }) => {
    await addServer(llm, stdioServer('mock', { timeoutSeconds: 1 }));
    await settled(llm, 'mock');
    await allowMcpTools(llm);
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await setScenarios([callScenario('wait', 'mcp__mock__slow', { ms: 5000 }, 'Too slow.')]);
    await send(llm, 'wait');
    await expect(lastAssistant(llm)).toContainText('Too slow.');
    expect(await toolMessageSeenByModel()).toContain('did not finish within 1 seconds');
    expect((await settled(llm, 'mock')).state).toBe('ready');
});

test('a server that dies is reported as failed, and Reconnect brings it back', async ({ llm }) => {
    await addServer(llm, stdioServer('mock'));
    await settled(llm, 'mock');
    await allowMcpTools(llm);
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await setScenarios([callScenario('kill it', 'mcp__mock__crash', {}, 'It went down.'), callScenario('again', 'mcp__mock__echo', { text: 'x' }, 'Back up.')]);
    await send(llm, 'kill it');
    await expect(lastAssistant(llm)).toContainText('It went down.');
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetMcpServers')).servers[0].state, { timeout: 15_000 }).toBe('failed');

    await openMcp(llm);
    let row = llm.locator('#llma-mcp-list > ul > li', { hasText: 'Mock mock' });
    await expect(row.locator('.llma-mcp-state')).toHaveText('Failed');
    await expect(row.locator('.llma-mcp-error')).toBeVisible();
    await row.locator('.llma-mcp-reconnect').click();
    await expect(row.locator('.llma-mcp-state')).toHaveText('Ready', { timeout: 30_000 });
    await llm.locator('#llma-settings-close').click();
    await send(llm, 'again');
    await expect(lastAssistant(llm)).toContainText('Back up.');
});

test('switching a tool off in the list takes it away from the model', async ({ llm }) => {
    await addServer(llm, stdioServer('mock'));
    await settled(llm, 'mock');
    await openMcp(llm);
    let row = llm.locator('#llma-mcp-list > ul > li', { hasText: 'Mock mock' });
    await row.locator('.llma-mcp-tools-summary').click();
    await row.locator('[data-tool-name="write_note"] .llma-mcp-tool-enabled').uncheck();
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetTools')).tools.some((t: any) => t.id === 'mcp__mock__write_note')).toBe(false);
    expect((await api(llm, 'LLMAssistantGetTools')).tools.some((t: any) => t.id === 'mcp__mock__echo')).toBe(true);
    let server = (await api(llm, 'LLMAssistantGetMcpServers')).servers[0];
    expect(server.disabledTools).toEqual(['write_note']);
    expect(server.tools.find((t: any) => t.name === 'write_note').enabled).toBe(false);
});

test('a switched-off server offers no tools and is not started', async ({ llm }) => {
    await addServer(llm, stdioServer('mock', { enabled: false }));
    let server = (await api(llm, 'LLMAssistantGetMcpServers')).servers[0];
    expect(server.state).toBe('disabled');
    expect((await api(llm, 'LLMAssistantGetTools')).tools.some((t: any) => t.isDynamic)).toBe(false);
});

test('tools the server adds later appear once it says so', async ({ llm }) => {
    await addServer(llm, stdioServer('mock'));
    await settled(llm, 'mock');
    await allowMcpTools(llm);
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await setScenarios([callScenario('grow', 'mcp__mock__add_tool', {}, 'Added.')]);
    await send(llm, 'grow');
    await expect(lastAssistant(llm)).toContainText('Added.');
    await expect.poll(async () => (await api(llm, 'LLMAssistantGetTools')).tools.some((t: any) => t.id === 'mcp__mock__extra'), { timeout: 15_000 }).toBe(true);
});

test('an http server needs its token, reports why it failed without it, and works with it', async ({ llm }) => {
    let url = `http://127.0.0.1:${HTTP_PORT}/mcp`;
    let bad = await addServer(llm, { id: 'web', name: 'Web mock', transport: 'http', url, headers: { Authorization: 'Bearer wrong' }, timeoutSeconds: 30 });
    expect((await settled(llm, 'web')).state).toBe('failed');
    expect((await settled(llm, 'web')).error).toContain('401');
    expect(bad).toBeTruthy();

    await addServer(llm, { id: 'web', name: 'Web mock', transport: 'http', url, headers: { Authorization: 'Bearer mock-token' }, timeoutSeconds: 30 });
    let ok = await settled(llm, 'web');
    expect(ok.state).toBe('ready');
    expect(ok.tools).toHaveLength(7);
    expect(JSON.stringify(await api(llm, 'LLMAssistantGetMcpServers'))).not.toContain('mock-token');

    await allowMcpTools(llm);
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await setScenarios([callScenario('over http', 'mcp__web__echo', { text: 'via http' }, 'Got it over http.')]);
    await send(llm, 'over http');
    await expect(lastAssistant(llm)).toContainText('Got it over http.');
    expect(await toolMessageSeenByModel()).toContain('echo: via http');
});

test('two servers keep their tools apart', async ({ llm }) => {
    await addServer(llm, stdioServer('one'));
    await addServer(llm, stdioServer('two'));
    await settled(llm, 'one');
    await settled(llm, 'two');
    let ids = (await api(llm, 'LLMAssistantGetTools')).tools.filter((t: any) => t.isDynamic).map((t: any) => t.id);
    expect(ids).toContain('mcp__one__echo');
    expect(ids).toContain('mcp__two__echo');
    expect(new Set(ids).size).toBe(ids.length);
});

test('a server is not offered to an assistant until the assistant switches its tools on', async ({ llm }) => {
    await addServer(llm, stdioServer('mock'));
    await settled(llm, 'mock');
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    await setScenarios([{ match: 'hello', rounds: [{ text: 'Hi.' }] }]);
    await send(llm, 'hello');
    await expect(lastAssistant(llm)).toContainText('Hi.');
    let first = (await mockRequests())[0];
    expect((first.tools ?? []).some((t: any) => t.function.name.startsWith('mcp__'))).toBe(false);
});

test('a stored tool cannot claim the MCP handler or an mcp__ id, and settings saves never write servers', async ({ llm }) => {
    let claim = await api(llm, 'LLMAssistantSaveTool', { tool: { id: 'sneaky', name: 'Sneaky', handlerType: 'builtin', handlerId: 'mcp', description: 'x', parameters: { type: 'object', properties: {} } }, scope: 'personal' });
    expect(claim.success).toBe(false);
    let reserved = await api(llm, 'LLMAssistantSaveTool', { tool: { id: 'mcp__mock__echo', name: 'Pose', handlerType: 'builtin', handlerId: 'file_read', description: 'x', parameters: { type: 'object', properties: {} } }, scope: 'personal' });
    expect(reserved.success).toBe(false);
    for (let scope of ['personal', 'shared']) {
        let saved = await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ mcpServers: { evil: stdioServer('evil') } }), scope });
        expect(saved.success).toBe(true);
    }
    expect((await api(llm, 'LLMAssistantGetMcpServers')).servers.map((s: any) => s.id)).not.toContain('evil');
    await api(llm, 'LLMAssistantResetSettings', { scope: 'shared' });
    await api(llm, 'LLMAssistantResetSettings');
});

test('a bad server is refused with the reason', async ({ llm }) => {
    let bad = await api(llm, 'LLMAssistantSaveMcpServer', { server: JSON.stringify({ id: 'Bad Id', name: 'x', transport: 'stdio', command: 'node' }) });
    expect(bad.success).toBe(false);
    expect(bad.error).toContain('id');
    let noProgram = await api(llm, 'LLMAssistantSaveMcpServer', { server: JSON.stringify({ id: 'p', name: 'x', transport: 'stdio', command: '' }) });
    expect(noProgram.error).toContain('program');
    let missing = await api(llm, 'LLMAssistantTestMcpServer', { server: JSON.stringify({ id: 'p', name: 'x', transport: 'stdio', command: 'this-program-does-not-exist-12345' }) });
    expect(missing.success).toBe(false);
    expect(missing.error).toContain('this-program-does-not-exist-12345');
});

test('a server that is saved but cannot start is shown as failed with the reason', async ({ llm }) => {
    await addServer(llm, { id: 'broken', name: 'Broken', transport: 'stdio', command: 'this-program-does-not-exist-12345', timeoutSeconds: 10 });
    let server = await settled(llm, 'broken');
    expect(server.state).toBe('failed');
    expect(server.error).toContain('this-program-does-not-exist-12345');
    await openMcp(llm);
    await expect(llm.locator('#llma-mcp-list li', { hasText: 'Broken' }).locator('.llma-mcp-state')).toHaveText('Failed');
});

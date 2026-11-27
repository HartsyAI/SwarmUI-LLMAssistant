import { test, expect, config, MAX_ROUNDS, PROMPT_ALLOWANCE } from '../lib/live';

/** Whether a saved call's arguments reached the server as a JSON object (arguments that did not parse are kept as text). */
const isObject = (value: unknown) => typeof value === 'object' && value !== null && !Array.isArray(value);

test('a tool call makes the round trip', async ({ live }) => {
    await live.whenIdle();
    let threadId = await live.createChat('tool call', { tools: true });
    // Reads SwarmUI's own docs folder: harmless, and it runs without approval in Ask mode.
    await live.requireTool(threadId, 'swarm_docs');
    let result = await live.chatTurn(threadId, 'Call the swarm_docs tool with action "list", then say in one sentence how many documents there are.',
        { forceToolId: 'swarm_docs', requests: MAX_ROUNDS, estimate: MAX_ROUNDS * (PROMPT_ALLOWANCE.tools + config.maxReplyTokens) });
    live.note(`Calls: ${result.toolCalls.map(c => `${c.name}(${JSON.stringify(c.arguments)})`).join(', ') || 'none'}. Approvals asked: ${result.approvals.map(a => a.name).join(', ') || 'none'}.`);
    live.expectFinished(result);
    expect(result.toolCalls.map(c => c.name), 'the tool was called').toContain('swarm_docs');
    expect(result.stoppedForRounds, 'the turn ended on its own').toBe(false);
    expect(result.done.truncated ?? false, 'the round limit was not reached').toBe(false);
    let saved = await live.savedReply(threadId);
    for (let call of saved.toolCalls) {
        expect(isObject(call.arguments), `arguments of ${call.name} are a JSON object: ${JSON.stringify(call.arguments)}`).toBe(true);
    }
    let results = result.toolResults.filter(r => r.name === 'swarm_docs');
    expect(results.some(r => r.result?.success !== false), `a successful result: ${JSON.stringify(results).slice(0, 300)}`).toBe(true);
    expect(result.done.usage?.rounds ?? 0, 'the model answered after the result').toBeGreaterThan(1);
    expect(result.visible.length, 'visible text').toBeGreaterThan(0);
});

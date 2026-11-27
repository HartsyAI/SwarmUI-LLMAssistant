import { test, expect, config, MAX_ROUNDS, PROMPT_ALLOWANCE } from '../lib/live';
import { updateState } from '../lib/state';

const turn = () => ({ requests: MAX_ROUNDS, estimate: MAX_ROUNDS * (PROMPT_ALLOWANCE.tools + config.maxReplyTokens) });

async function presetExists(live: any, title: string): Promise<boolean> {
    let mine = await live.client.ok('GetMyUserData');
    return (mine.presets ?? []).some((p: any) => p.title === title);
}

test('Ask mode: a denied call does not run', async ({ live }) => {
    await live.whenIdle();
    let threadId = await live.createChat('denied call', { tools: true });
    await live.requireTool(threadId, 'create_image_preset');
    let title = `llma-live-denied-${live.runId}`;
    // In case it ran after all, it is the run's to delete.
    updateState(s => s.created.presets.push(title));
    let result = await live.chatTurn(threadId, `Call the create_image_preset tool once with title "${title}" and params {"steps": 8}.`,
        { forceToolId: 'create_image_preset', ...turn() });
    live.note(`Approvals asked: ${result.approvals.map(a => `${a.name} (${a.risk}) -> ${a.decision}`).join(', ') || 'none'}.`);
    live.expectFinished(result);
    expect(result.approvals.map(a => a.name), 'approval was asked').toContain('create_image_preset');
    let denied = result.toolResults.find(r => r.name === 'create_image_preset');
    expect(denied?.result?.denied, `the model was told it was denied: ${JSON.stringify(denied)}`).toBe(true);
    expect(result.stoppedForRounds, 'the turn ended on its own').toBe(false);
    expect(await presetExists(live, title), 'nothing was saved').toBe(false);
});

test('Ask mode: an allowed call runs', async ({ live }) => {
    await live.whenIdle();
    let threadId = await live.createChat('allowed call', { tools: true });
    await live.requireTool(threadId, 'create_image_preset');
    let title = `llma-live-allowed-${live.runId}`;
    expect(await presetExists(live, title)).toBe(false);
    updateState(s => s.created.presets.push(title));
    let allowed = 0;
    let result = await live.chatTurn(threadId, `Call the create_image_preset tool once with title "${title}" and params {"steps": 8}. Then confirm in one sentence.`, {
        forceToolId: 'create_image_preset',
        // Only the one call that makes the run's own preset, once, and never over an existing one.
        approve: a => a.name === 'create_image_preset' && a.arguments?.title === title && !a.arguments?.overwrite && allowed++ === 0 ? 'allow' : 'deny',
        ...turn()
    });
    live.note(`Approvals asked: ${result.approvals.map(a => `${a.name} ${JSON.stringify(a.arguments)} -> ${a.decision}`).join(', ') || 'none'}.`);
    live.expectFinished(result);
    expect(result.approvals.some(a => a.decision === 'allow'), 'a call as asked for was waiting for approval').toBe(true);
    let results = result.toolResults.filter(r => r.name === 'create_image_preset');
    expect(results.some(r => r.result?.success === true), `the call ran: ${JSON.stringify(results).slice(0, 400)}`).toBe(true);
    expect(result.stoppedForRounds, 'the turn ended on its own').toBe(false);
    expect(await presetExists(live, title), 'the preset was saved').toBe(true);
});

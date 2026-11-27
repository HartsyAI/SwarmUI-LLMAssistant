import { test, expect, config } from '../lib/live';

test('a plain reply streams, ends and reports usage', async ({ live }) => {
    await live.whenIdle();
    let threadId = await live.createChat('plain reply', { tools: false });
    let result = await live.chatTurn(threadId, 'In two short sentences, say what a lighthouse is for.');
    live.expectFinished(result);
    expect(result.chunks, 'the reply came in pieces').toBeGreaterThan(1);
    expect(result.visible.length, 'visible text').toBeGreaterThan(0);
    expect(result.toolCalls).toEqual([]);
    let saved = await live.savedReply(threadId);
    expect(saved?.content?.length ?? 0, 'the saved reply').toBeGreaterThan(0);
    expect(saved.meta.model, 'the saved reply names the model').toBe(config.chatModel);
});

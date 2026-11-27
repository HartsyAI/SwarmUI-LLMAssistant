import { test, expect, config, PROMPT_ALLOWANCE } from '../lib/live';

const STOP_AFTER_CHUNKS = 12;
const LONG_REPLY = 900;

test('Stop ends a streaming reply, keeps what was written and frees the model', async ({ live }) => {
    await live.whenIdle();
    let threadId = await live.createChat('stop', { tools: false });
    let stoppedAt = 0;
    let stopReply: any = null;
    let result = await live.chatTurn(threadId, 'Write a very long story, at least twenty paragraphs, about a lighthouse keeper.', {
        maxTokens: LONG_REPLY,
        onChunk: async count => {
            if (count === STOP_AFTER_CHUNKS) {
                stoppedAt = Date.now();
                stopReply = await live.stop(threadId);
            }
        }
    });
    expect(stoppedAt, `the reply had at least ${STOP_AFTER_CHUNKS} chunks`).toBeGreaterThan(0);
    let endedAfter = (Date.now() - stoppedAt) / 1000;
    live.note(`Ended ${endedAfter.toFixed(1)}s after Stop, ${result.chunks} chunks, stopReason ${result.done?.stopReason ?? 'none'}, usage ${JSON.stringify(result.done?.usage ?? 'not reported')}.`);
    expect(stopReply?.cancelled, 'the stop found the reply').toBe(true);
    expect(result.errors).toEqual([]);
    expect(result.done, 'the turn ended with a done frame').not.toBeNull();
    expect(result.done.stopReason).toBe('cancelled');
    expect(endedAfter, 'it ended soon after').toBeLessThan(30);
    let saved = await live.savedReply(threadId);
    expect(saved?.rawContent?.length ?? 0, 'the partial reply was saved').toBeGreaterThan(0);
    expect(saved.meta.stopReason).toBe('cancelled');
    // The backend must take the next request: a generation still running underneath would hold it up.
    let held = live.reserve(1, PROMPT_ALLOWANCE.plain + 20);
    let started = Date.now();
    let next = await live.client.ok('LLMAssistantRunTurn', { threadId: await live.createChat('after stop', { tools: false }), message: 'Reply with the single word: ready', model: config.chatModel, maxTokens: 20, enableThinking: false }, config.turnTimeoutMs);
    held.settle(next.usage?.rounds, next.usage ? next.usage.promptTokens + next.usage.completionTokens : undefined);
    live.note(`The next request answered in ${((Date.now() - started) / 1000).toFixed(1)}s.`);
    expect(next.response.length).toBeGreaterThan(0);
});

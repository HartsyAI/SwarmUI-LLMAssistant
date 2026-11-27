import { test, expect, config, settleUsage, PROMPT_ALLOWANCE } from '../lib/live';

const STOP_AFTER_CHUNKS = 12;
/** A loaded model answers a few words well within this. */
const NEXT_SECONDS = 20;
/** The stopped reply's cap: left running, it could not end within the bound below 200 tokens a second. */
const LONG_REPLY = NEXT_SECONDS * 200;

test('Stop ends a streaming reply, keeps what was written and frees the model', async ({ live }) => {
    await live.whenIdle();
    let threadId = await live.createChat('stop', { tools: false });
    let stoppedAt = 0;
    let stopReply: any = null;
    let result = await live.chatTurn(threadId, 'Write every whole number from 1 to 5000, one per line, and nothing else.', {
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
    let nextChat = await live.createChat('after stop', { tools: false });
    // A model that was unloaded after the reply is loaded again by the next request, which no bound here allows for.
    let loaded = (await live.modelInfo())?.is_loaded === true;
    let held = live.reserve(1, PROMPT_ALLOWANCE.plain + 20);
    let started = Date.now();
    let next = await live.client.ok('LLMAssistantRunTurn', { threadId: nextChat, message: 'Reply with the single word: ready', model: config.chatModel, maxTokens: 20, enableThinking: false }, config.turnTimeoutMs);
    let took = (Date.now() - started) / 1000;
    settleUsage(held, next.usage);
    live.note(`The next request answered in ${took.toFixed(1)}s.`);
    expect(next.response.length).toBeGreaterThan(0);
    if (!loaded) {
        live.note('The model is not listed as loaded after the stopped reply, so how soon the next request answered proves nothing.');
        return;
    }
    expect(took, 'the next request was not held up').toBeLessThan(NEXT_SECONDS);
});

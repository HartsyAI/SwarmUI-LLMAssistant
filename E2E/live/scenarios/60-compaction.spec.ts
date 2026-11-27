import { test, expect, config, PROMPT_ALLOWANCE } from '../lib/live';
import { assistantParameter } from '../lib/assistants';
import { updateState } from '../lib/state';

/** The summarizer reads at most this much of each message (CompactionPlanner.BuildTranscript), which keeps this scenario cheap. */
const CLIP = 6000;
/** The most characters one imported message is given, under the import's own limit of 100,000. */
const MESSAGE_LIMIT = 95_000;

const FILLER = 'We went over the garden plan again: the beds along the south wall, the order of planting, and who waters on which day. ';

test('a chat near the window is summarized before the reply', async ({ live }) => {
    // The wait for the server, two dry runs and the import (two minutes each at most), then a turn allowed twice the usual time.
    test.setTimeout(config.idleTimeoutMs + 3 * 120_000 + 2 * config.turnTimeoutMs + 120_000);
    await live.whenIdle();
    let probe = await live.createChat('compaction probe', { tools: false });
    let dry = await live.client.ok('LLMAssistantRunTurn', { threadId: probe, message: 'hello', model: config.chatModel, dryRun: true }, 120_000);
    let settings = (await live.client.ok('LLMAssistantGetSettings')).settings;
    let parameters = settings.parameters ?? {};
    let window = dry.contextWindow as number;
    if (!window) {
        live.skip('No context window is known for the model, so a chat is never summarized on its own.');
    }
    // The server estimates four characters a token. Four long early messages carry the chat just past the threshold.
    let threshold = parameters.compactThreshold > 0 && parameters.compactThreshold <= 1 ? parameters.compactThreshold : 0.8;
    let each = Math.ceil(window * threshold * 4 * 1.05 / 4);
    if (each > MESSAGE_LIMIT) {
        live.skip(`A window of ${window} tokens needs a longer chat than this scenario builds. Set a smaller Context Window in your settings to run it.`);
    }
    let long = (opening: string, closing: string) => `${opening} ${FILLER.repeat(Math.ceil(each / FILLER.length)).slice(0, each - opening.length - closing.length - 2)} ${closing}`;
    let turns = [
        long('My project is called MARIGOLD and the budget is 4200 euros.', 'The gate code is HERON.'),
        long('Understood, I will keep the budget in mind.', 'That is all for now.'),
        long('Here are more notes.', 'End of the notes.'),
        long('Thanks, noted.', 'Anything else?'),
        'Which bed gets the tomatoes?', 'The one by the south wall.',
        'And the herbs?', 'Next to the kitchen door.'
    ];
    let messages = turns.map((content, i) => ({ id: `m${i}`, parentId: i === 0 ? null : `m${i - 1}`, role: i % 2 === 0 ? 'user' : 'assistant', content }));
    let imported = await live.client.ok('LLMAssistantImportThread', { content: JSON.stringify({ format: 'llmassistant-thread', version: 1, title: 'Live smoke compaction', messages }) }, 120_000);
    let threadId = (imported.thread?.id ?? imported.threadId) as string;
    updateState(s => s.created.threads.push(threadId));
    await live.prepareChat(threadId, 'compaction', false);
    let question = 'In one sentence: what is my project called and what is its budget?';
    let before = await live.client.ok('LLMAssistantRunTurn', { threadId, message: question, model: config.chatModel, dryRun: true }, 120_000);
    live.note(`Window ${window}, threshold ${threshold}, estimate ${before.estimatedPromptTokens} tokens; dry run says wouldCompact ${before.wouldCompact}.`);
    if (!before.wouldCompact) {
        let chat = (await live.client.ok('LLMAssistantGetThread', { threadId })).thread;
        let off = parameters.autoCompact === false ? 'your settings' : (await assistantParameter(live.client, chat.assistantId, 'autoCompact')) === false ? 'the assistant' : null;
        if (off) {
            live.skip(`Shorten Long Chats is off in ${off}, and the suite changes no settings.`);
        }
    }
    expect(before.wouldCompact, 'the dry run foresees the summary').toBe(true);
    // The summary reads about four clipped messages and writes at most 1,500 tokens. If it fails, the reply is sent the whole chat, so
    // that is what is held for the reply (the estimate with room for its error).
    let result = await live.chatTurn(threadId, question, { summary: { tokens: Math.ceil(4 * CLIP / 4) + 2500 }, estimate: Math.ceil(before.estimatedPromptTokens * 1.2) + PROMPT_ALLOWANCE.plain + config.maxReplyTokens, timeoutMs: 2 * config.turnTimeoutMs });
    let failed = result.frames.find(f => f.compactionFailed);
    expect(failed?.compactionFailed, 'the summary was written').toBeUndefined();
    expect(result.frames.some(f => f.compacting), 'a compacting frame').toBe(true);
    let compaction = result.frames.find(f => f.compaction)?.compaction;
    expect(compaction?.summary?.length ?? 0, 'a summary').toBeGreaterThan(0);
    // What survived is reported, not asserted: it is the model's judgment.
    let kept = (word: string) => compaction.summary.includes(word) ? 'kept' : 'lost';
    live.note(`Summary of ${compaction.messageCount} messages, ${compaction.summary.length} chars. Project name ${kept('MARIGOLD')}, budget ${kept('4200')} (both in the first ${CLIP} chars of a message); code at the end of that ${each}-char message ${kept('HERON')}.`);
    live.expectFinished(result);
    expect(result.done.usage?.promptTokens ?? 0, 'the reply was sent a shortened chat').toBeLessThan(window * threshold / 2);
});

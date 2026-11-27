import { test, expect, config, roughTokens, MAX_ROUNDS, PROMPT_ALLOWANCE } from '../lib/live';

/** Ordinary prose, so the message and not the wrapping around it makes up most of a plain request. */
const PROSE = 'The harbour wakes before the sun does. Boats knock against the quay, gulls argue over the night\'s scraps and the baker sets the first trays out to cool. '.repeat(10);
const MESSAGE = `${PROSE}\n\nDo not use any tool. Reply with the single word: noted`;

/** The estimate is "about right" when it is within this factor of what the model counted, either way. */
const FACTOR = 2;

for (let tools of [false, true]) {
    test(`a dry run's token estimate is close to the real turn's count (tools ${tools ? 'on' : 'off'})`, async ({ live }) => {
        await live.whenIdle();
        let threadId = await live.createChat(`dry run, tools ${tools ? 'on' : 'off'}`, { tools });
        let request = { threadId, message: MESSAGE, model: config.chatModel, maxTokens: 20, enableThinking: false };
        let dry = await live.client.ok('LLMAssistantRunTurn', { ...request, dryRun: true }, 120_000);
        let held = live.reserve(tools ? MAX_ROUNDS : 1, (tools ? MAX_ROUNDS : 1) * (roughTokens(MESSAGE) + (tools ? PROMPT_ALLOWANCE.tools : PROMPT_ALLOWANCE.plain) + 20));
        let real = await live.client.ok('LLMAssistantRunTurn', request, config.turnTimeoutMs);
        held.settle(real.usage?.rounds, real.usage ? real.usage.promptTokens + real.usage.completionTokens : undefined);
        expect(dry.modelFound).toBe(true);
        let model = await live.modelInfo();
        live.note(`Dry run: ${dry.estimatedPromptTokens} estimated tokens, window ${dry.contextWindow} (the model lists ${model.context_length ?? 'none'}), ${dry.tools.length} tools. Real: ${JSON.stringify(real.usage ?? 'no usage')}.`);
        expect(dry.contextWindow, 'a window is reported').toBeGreaterThan(0);
        expect(real.usage, 'the real turn reports usage').toBeTruthy();
        // After tool rounds the prompt count is a sum over rounds, which one estimate cannot be compared with.
        if (real.usage.rounds !== 1) {
            live.skip(`The model called a tool (${real.usage.rounds} rounds), so there is no single prompt count to compare with.`);
        }
        let ratio = dry.estimatedPromptTokens / real.usage.promptTokens;
        live.note(`Estimate / real = ${ratio.toFixed(2)}.`);
        expect(ratio, `estimate ${dry.estimatedPromptTokens} against ${real.usage.promptTokens} counted`).toBeGreaterThan(1 / FACTOR);
        expect(ratio, `estimate ${dry.estimatedPromptTokens} against ${real.usage.promptTokens} counted`).toBeLessThan(FACTOR);
    });
}

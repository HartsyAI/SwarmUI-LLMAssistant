import { test, expect, config } from '../lib/live';

/** The rewrite's token cap comes from the user's settings (2,048 unless changed). */
async function rewriteCap(live: any): Promise<{ tokens: number; named: string }> {
    let settings = (await live.client.ok('LLMAssistantGetSettings')).settings;
    return { tokens: (settings.promptRewrite?.maxTokens ?? 2048) + 1500, named: settings.promptRewrite?.model ?? '' };
}

for (let naming of [false, true]) {
    test(`a prompt is rewritten ${naming ? 'when the request names the chat model' : 'with no model named, as automatic rewriting asks'}`, async ({ live }) => {
        await live.whenIdle();
        let cap = await rewriteCap(live);
        if (!naming && cap.named) {
            live.note(`Your settings name "${cap.named}" as the rewriting model, so this request is not the unnamed case.`);
        }
        // A rewrite may ask the model twice (a retry) and once more for a negative prompt.
        let held = live.reserve(1, cap.tokens);
        let reply = await live.client.ok('LLMAssistantRewritePrompt', {
            prompt: `a red lighthouse on a cliff at dawn (smoke ${live.runId}${naming ? ' named' : ''})`,
            imageModel: config.imageModel,
            ...(naming ? { rewriteModel: config.chatModel } : {})
        }, config.turnTimeoutMs);
        held.settle(reply.retried ? 2 : 1, reply.usage ? reply.usage.inputTokens + reply.usage.outputTokens : undefined);
        live.note(`Style ${reply.style?.id ?? 'none'} (${reply.via}), model "${reply.model ?? ''}", rewritten ${reply.rewritten}, fellBack ${reply.fellBack}, skipped ${reply.skipped ?? 'no'}. ${(reply.warnings ?? []).join(' ')}`.slice(0, 600));
        if (!reply.style) {
            live.skip(`No rewrite style applies: ${reply.reason}`);
        }
        expect(reply.fellBack, 'the rewrite did not fall back').toBe(false);
        expect(reply.rewritten, 'the prompt was rewritten').toBe(true);
        expect(reply.text.length).toBeGreaterThan(0);
        expect(reply.text, 'no reasoning is left in the prompt').not.toContain('<think>');
    });
}

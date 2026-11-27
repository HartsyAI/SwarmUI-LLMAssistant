import { test, expect, config, MAX_ROUNDS, PROMPT_ALLOWANCE } from '../lib/live';
import type { TurnResult } from '../lib/live';
import { listBackends, listImageModels, usedWithin } from '../lib/guard';
import { objection } from '../lib/image-approval';
import { defaultImagePreset, imagePath } from '../lib/images';
import { pngSpread } from '../lib/png';
import { unloadOwnChatModel } from '../lib/server';
import { updateState } from '../lib/state';

test('an image is generated through the assistant, with its prompt rewritten', async ({ live }) => {
    test.setTimeout(config.idleTimeoutMs + config.imageTimeoutMs + 180_000);
    if (!config.imageModel) {
        live.skip('LLMA_LIVE_IMAGE_MODEL is not set.');
    }
    if (!(await listImageModels(live.client)).includes(config.imageModel)) {
        live.skip(`SwarmUI lists no model "${config.imageModel}".`);
    }
    if (live.guard.image.length === 0) {
        live.skip('No local image backend is enabled.');
    }
    // Earlier scenarios leave the chat model in memory, which would count against the room the image needs. The turn loads it again.
    let unloaded = await unloadOwnChatModel(live.client);
    if (unloaded) {
        live.note(unloaded);
    }
    await live.whenIdle('image');
    let threadId = await live.createChat('image', { tools: true, autoEnhance: 'on' });
    await live.requireTool(threadId, 'generate_image');
    let chat = (await live.client.ok('LLMAssistantGetThread', { threadId })).thread;
    let preset = await defaultImagePreset(live.client, chat.assistantId);
    if (preset) {
        live.skip(`The image tool has a default preset ("${preset}"), whose settings the suite cannot check.`);
    }
    let wanted = { model: config.imageModel, prompt: `a red lighthouse on a cliff at dawn, run ${live.runId}`, backend: live.guard.image[0] };
    let request = { model: wanted.model, images: 1, params: { steps: 12, width: 768, height: 768, exactbackendid: wanted.backend }, prompt: wanted.prompt };
    let allowed = 0;
    let approved: string[] = [];
    let refused: string[] = [];
    // The rewrite inside the generation reports no usage, so its worst case stays counted. The turn holds its own rounds.
    let rewrite = await live.rewriteCap();
    live.reserve(rewrite.requests, rewrite.tokens, 1);
    let since = Date.now();
    try {
        let result: TurnResult = await live.chatTurn(threadId,
            `Call the generate_image tool exactly once, with exactly these arguments: ${JSON.stringify(request)}. Then say in one sentence that it is done.`, {
                forceToolId: 'generate_image',
                requests: MAX_ROUNDS,
                estimate: MAX_ROUNDS * (PROMPT_ALLOWANCE.tools + config.maxReplyTokens),
                timeoutMs: config.imageTimeoutMs,
                approve: a => {
                    let why = a.name !== 'generate_image' ? `it is ${a.name}` : allowed > 0 ? 'one image was already allowed' : objection(a.arguments, wanted);
                    if (why) {
                        refused.push(`${a.name} ${JSON.stringify(a.arguments)}: ${why}`);
                        return 'deny';
                    }
                    allowed++;
                    approved.push(JSON.stringify(a.arguments));
                    // Recorded before the answer is sent, so clean-up knows an image may exist even if nothing more is heard.
                    updateState(s => s.imageCalls.push(threadId));
                    return 'allow';
                }
            });
        let results = result.toolResults.filter(r => r.name === 'generate_image' && !r.result?.denied);
        let made = results.find(r => r.result?.success === true)?.result;
        // Recorded before anything can fail, so the image is deleted whatever else happens.
        let urls: string[] = made ? made.imageUrls ?? [made.imageUrl] : [];
        updateState(s => s.created.images.push(...urls.map(imagePath)));
        if (refused.length > 0) {
            live.note(`Denied: ${refused.join('; ')}`.slice(0, 600));
        }
        live.note(`Allowed ${allowed} call(s) with ${approved.join('; ') || 'nothing'}. Result: ${JSON.stringify(made ?? results[0]?.result ?? 'none').slice(0, 600)}`);
        expect(allowed, 'the model made the call that was asked for').toBe(1);
        live.expectFinished(result);
        expect(made, `an image: ${JSON.stringify(results).slice(0, 600)}`).toBeTruthy();
        expect(made.model).toBe(config.imageModel);
        let file = await live.client.getBytes(made.imageUrl);
        let spread = pngSpread(file);
        live.note(`Image: ${file.length} bytes, pixel spread ${spread ?? 'not measured'}.`);
        expect(file.length, 'an image file').toBeGreaterThan(10_000);
        if (spread !== null) {
            expect(spread, 'not a flat (black) frame').toBeGreaterThan(30);
        }
        expect.soft(made.rewriteStatus, `the prompt was rewritten (style ${made.promptStyle ?? 'none'}; ${(made.rewriteWarnings ?? []).join(' ')})`).toBe('rewritten');
    }
    finally {
        // Checked however the turn ended: no backend that should not take the request was used since it was sent.
        let used = usedWithin(await listBackends(live.client), live.guard.bystanders, (Date.now() - since) / 1000);
        expect.soft(used, 'backends used during the image turn that should not take it (by this run or someone else)').toEqual([]);
    }
});

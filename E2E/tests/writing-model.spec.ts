import { test as base, expect, setScenarios, mockRequests, clearRequests, api, send } from './fixtures';
import type { Page } from '@playwright/test';

// What reaches a model outside a chat turn (the chat-model style on the one-shot, voice and prompt rewrite requests, and the user's style notes on the rewrite), the warning for a model with no tool calling, and the upload of a
// message the server refuses. The mock lists mock-model-b as taking no tools.

const A = 'mock-model';
const B = 'mock-model-b';
const IMAGE_MODEL = 'e2e-mystery.safetensors';
const PNG = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==';

const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await clean(llm);
        await use(llm);
        await clean(llm);
    }
});

/** Leaves no style, switch, pause or note behind. */
async function clean(page: Page) {
    let listed = await api(page, 'LLMAssistantGetPromptStyles');
    for (let style of listed.styles ?? []) {
        if (style.scopes.includes('personal')) {
            await api(page, 'LLMAssistantDeletePromptStyle', { id: style.id, scope: 'personal' });
        }
    }
    await api(page, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ promptStyles: { llmAddenda: true, disabled: [] } }) });
    await api(page, 'LLMAssistantSetMemoryControls', { recallPaused: false });
    await api(page, 'LLMAssistantClearUserProfile');
    await clearRequests();
    await setScenarios([]);
}

async function saveStyle(page: Page, id: string, body: string, extra: Record<string, unknown> = {}) {
    let saved = await api(page, 'LLMAssistantSavePromptStyle', { id: id, kind: 'llm', name: id, matches: `exact:${A}`, body: body, ...extra });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
}

/** The system prompt of the one request the mock saw whose last user message holds `text`. */
async function systemPromptOf(text: string): Promise<string> {
    let request = (await mockRequests()).find(r => JSON.stringify(r.messages[r.messages.length - 1].content).includes(text));
    expect(request, `no request about ${text}`).toBeTruthy();
    return request.messages.find((m: any) => m.role === 'system').content;
}

/** A word no other request holds. */
function unique(word: string) {
    return `${word}-${Math.random().toString(36).slice(2, 8)}`;
}

/** Runs a prompt rewrite on the image model with no style of its own and gives the system prompt the writing model was sent. */
async function rewriteSystemPrompt(page: Page, prompt: string): Promise<string> {
    await clearRequests();
    let reply = await api(page, 'LLMAssistantRewritePrompt', { prompt: prompt, imageModel: IMAGE_MODEL, model: A });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    return await systemPromptOf(prompt);
}

test('the one-shot request gets the chat-model style of its task, and a changed style is not answered from the cache', async ({ llm }) => {
    await saveStyle(llm, 'one-shot', 'FIRST ADDENDUM.');
    await saveStyle(llm, 'captions-only', 'CAPTION ADDENDUM.', { matches: `exact:${B}`, tasks: 'caption' });
    let message = unique('oneshot');
    expect((await api(llm, 'LLMAssistantSendMessage', { message: message, model: A })).success).toBe(true);
    expect(await systemPromptOf(message)).toContain('FIRST ADDENDUM.');

    await saveStyle(llm, 'one-shot', 'SECOND ADDENDUM.');
    await clearRequests();
    expect((await api(llm, 'LLMAssistantSendMessage', { message: message, model: A })).success).toBe(true);
    expect(await systemPromptOf(message)).toContain('SECOND ADDENDUM.');

    let chatting = unique('chatting');
    await api(llm, 'LLMAssistantSendMessage', { message: chatting, model: B });
    expect(await systemPromptOf(chatting)).not.toContain('CAPTION ADDENDUM.');
    let captioning = unique('captioning');
    await api(llm, 'LLMAssistantSendMessage', { message: captioning, model: B, instructionId: 'caption' });
    expect(await systemPromptOf(captioning)).toContain('CAPTION ADDENDUM.');
});

test('both voice routes get the chat-model style, and the master switch stops it', async ({ llm }) => {
    await saveStyle(llm, 'voice', 'VOICE ADDENDUM.');
    let spoken = unique('spoken');
    expect((await api(llm, 'LLMAssistantVoiceTurn', { message: spoken, model: A })).success).toBe(true);
    expect(await systemPromptOf(spoken)).toContain('VOICE ADDENDUM.');

    let streamed = unique('streamed');
    await llm.evaluate(([text, model]) => new Promise<void>((resolve, reject) => {
        (window as any).makeWSRequest('LLMAssistantVoiceTurnWS', { message: text, model: model }, (data: any) => {
            if (data.done) {
                resolve();
            }
        }, 0, (error: string) => reject(new Error(error)));
    }), [streamed, A]);
    expect(await systemPromptOf(streamed)).toContain('VOICE ADDENDUM.');

    await api(llm, 'LLMAssistantSaveSettings', { settings: JSON.stringify({ promptStyles: { llmAddenda: false, disabled: [] } }) });
    let quiet = unique('quiet');
    await api(llm, 'LLMAssistantVoiceTurn', { message: quiet, model: A });
    expect(await systemPromptOf(quiet)).not.toContain('VOICE ADDENDUM.');
});

test('a prompt rewrite carries the writing model\'s style and the style notes, and paused recall keeps the notes out', async ({ llm }) => {
    await saveStyle(llm, 'writer', 'WRITER ADDENDUM.', { tasks: 'rewrite' });
    for (let [kind, text] of [['style', 'Likes heavy film grain'], ['fact', 'Lives in Lyon']]) {
        expect((await api(llm, 'LLMAssistantAddMemoryNote', { kind: kind, text: text })).success).toBe(true);
    }
    let prompt = unique('a lighthouse');
    let system = await rewriteSystemPrompt(llm, prompt);
    expect(system).toContain('WRITER ADDENDUM.');
    expect(system).toContain('- [style] Likes heavy film grain');
    expect(system).not.toContain('Lyon');

    // The same prompt again, with recall paused: the cached rewrite made with the notes does not answer it.
    expect((await api(llm, 'LLMAssistantSetMemoryControls', { recallPaused: true })).success).toBe(true);
    let paused = await rewriteSystemPrompt(llm, prompt);
    expect(paused).toContain('WRITER ADDENDUM.');
    expect(paused).not.toContain('film grain');
});

test('a model with no tool calling is warned about once per chat, and a model that says nothing is not', async ({ llm }) => {
    let warning = llm.locator('.llma-toast', { hasText: 'no built-in tool support' });
    await setScenarios([{ match: 'tool warning', rounds: [{ text: 'Answered.', delayMs: 300 }] }]);
    await send(llm, 'tool warning on a quiet model');
    await expect(llm.locator('.llma-msg-row.assistant .llma-msg-bubble').last()).toContainText('Answered.');
    await expect(warning).toHaveCount(0);

    await llm.locator('#llma-model-select').selectOption(B, { force: true });
    await send(llm, 'tool warning on a model with no tools');
    await expect(warning).toContainText(B);
    await expect(llm.locator('.llma-msg-row.assistant .llma-msg-bubble').last()).toContainText('Answered.');
    await expect(warning).toHaveCount(0, { timeout: 10_000 });
    await send(llm, 'tool warning again');
    await expect(llm.locator('.llma-msg-row.assistant')).toHaveCount(3);
    await expect(llm.locator('#llma-stop-btn')).toBeHidden();
    await expect(warning).toHaveCount(0);
});

test('a refused send removes the image uploaded for it, and a stored one keeps it', async ({ llm }) => {
    let threadId = (await api(llm, 'LLMAssistantCreateThread')).thread.id;
    /** Uploads an image for a message, then sends the message, and gives the image's address and whether the server refused it. */
    let uploadAndSend = async (messageId: string, extra: Record<string, unknown>) => {
        let upload = await api(llm, 'LLMAssistantUploadChatImage', { threadId: threadId, messageId: messageId, imageData: PNG });
        expect(upload.success, JSON.stringify(upload)).toBe(true);
        expect((await llm.request.get(`/${upload.url}`)).status()).toBe(200);
        let payload = { threadId: threadId, userMessageId: messageId, message: 'look at this', model: A, media: [{ url: upload.url, mediaType: 'image/png' }], ...extra };
        let refused = await llm.evaluate(body => new Promise<boolean>(resolve => {
            (window as any).makeWSRequest('LLMAssistantSendMessageWS', body, (data: any) => {
                if (data.done) {
                    resolve(false);
                }
            }, 0, (error: string, data: any) => resolve(data?.refused === true));
        }), payload);
        return { url: upload.url as string, refused: refused };
    };

    let kept = await uploadAndSend('e2e-kept', {});
    expect(kept.refused).toBe(false);
    expect((await llm.request.get(`/${kept.url}`)).status()).toBe(200);

    let gone = await uploadAndSend('e2e-refused', { documents: ['no-such-document'] });
    expect(gone.refused).toBe(true);
    expect((await llm.request.get(`/${gone.url}`)).status()).not.toBe(200);

    // A refused request that names a stored message's id leaves that message's image alone.
    let reused = await api(llm, 'LLMAssistantGetThread', { threadId: threadId });
    expect(reused.thread.messages.some((m: any) => m.id === 'e2e-kept')).toBe(true);
    await llm.evaluate(([id, url]) => new Promise<void>(resolve => {
        (window as any).makeWSRequest('LLMAssistantSendMessageWS', { threadId: id, userMessageId: 'e2e-kept', message: 'again', documents: ['no-such-document'], media: [{ url: url, mediaType: 'image/png' }] },
            () => { }, 0, () => resolve());
    }), [threadId, kept.url]);
    expect((await llm.request.get(`/${kept.url}`)).status()).toBe(200);
});

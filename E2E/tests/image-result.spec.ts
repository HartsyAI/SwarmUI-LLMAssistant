import { test, expect, setScenarios, api, send, lastAssistant } from './fixtures';
import type { Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// What a generate_image result looks like in a chat. There is no image backend here, so the file the result names is written into the throwaway output folder, and the result the tool would have returned is put
// on a saved reply as the chat is read.

const FOLDER = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '.run', 'output', 'local', 'raw', 'e2e');
const FILE = 'a red fox (snow).png';
const ADDRESS = 'View/local/raw/e2e/a%20red%20fox%20%28snow%29.png';
const OTHER_FILE = 'another.png';
const OTHER_ADDRESS = `View/local/raw/e2e/${OTHER_FILE}`;
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhWMyFwAAAABJRU5ErkJggg==', 'base64');
const WARNING = 'The writing model failed: it is away. The prompt was left as you wrote it.';

/** Sends a message, then opens the chat again with a generate_image call on its reply. `reply` is what the model writes; with `rounds` the saved reply also says the call came before that text. */
async function chatWithImage(page: Page, result: Record<string, unknown>, { reply: text = `Here it is: [View image](${ADDRESS})`, rounds = false } = {}) {
    fs.mkdirSync(FOLDER, { recursive: true });
    fs.writeFileSync(path.join(FOLDER, FILE), PNG);
    fs.writeFileSync(path.join(FOLDER, OTHER_FILE), PNG);
    await setScenarios([{ match: 'draw a fox', rounds: [{ text: text }] }]);
    await send(page, 'draw a fox');
    await expect(lastAssistant(page)).toContainText('Here it is');
    // The reply is saved before the chat is read again.
    await expect(page.locator('#llma-stop-btn')).toBeHidden();
    await page.route('**/API/LLMAssistantGetThread', async route => {
        let response = await route.fetch();
        let body = await response.json();
        let reply = body.thread?.messages?.filter((m: any) => m.role == 'assistant').pop();
        if (reply) {
            reply.toolCalls = [{ id: 'img1', name: 'generate_image', arguments: { prompt: 'a red fox' }, result: { success: true, imageUrl: ADDRESS, prompt: 'a red fox', ...result } }];
            reply.rounds = rounds ? [{ text: '', toolCallIds: ['img1'] }, { text: reply.content, toolCallIds: [] }] : undefined;
        }
        await route.fulfill({ response, json: body });
    });
    await page.evaluate(() => (0, eval)('llmaSwitchThread(LLMAState.activeThreadId)'));
}

test('an image address with encoded spaces is a link in a reply and loads in the card, which names no file size', async ({ llm }) => {
    await chatWithImage(llm, {});
    let link = lastAssistant(llm).locator('a', { hasText: 'View image' });
    await expect(link).toHaveAttribute('href', ADDRESS);
    let card = llm.locator('.llma-tool-extra .llma-asset-card');
    await expect(card).toHaveCount(1);
    await expect.poll(async () => await card.locator('img').evaluate((img: HTMLImageElement) => img.naturalWidth)).toBe(1);
    await expect(card.locator('.llma-asset-card-sub')).toHaveText('Image');
    await expect(llm.locator('.llma-tool-rewrite-notice')).toHaveCount(0);
});

test('Download on an image card saves the image, not its address', async ({ llm }) => {
    await chatWithImage(llm, {});
    let card = llm.locator('.llma-tool-extra .llma-asset-card');
    await card.locator('[data-asset-menu]').click();
    let saved = llm.waitForEvent('download');
    await llm.locator('.llma-asset-menu').getByText('Download').click();
    let download = await saved;
    expect(download.suggestedFilename()).toBe('a_red_fox.png');
    expect(fs.readFileSync(await download.path())).toEqual(PNG);
});

test('a saved file is named with the type of its address or data URI', async ({ llm }) => {
    let names = await llm.evaluate(() => ['data:video/mp4;base64,AAAA', 'data:video/quicktime;base64,AAAA', 'data:image/jpeg;base64,AAAA', 'View/local/raw/a.webm', 'View/local/raw/a.png', 'View/local/raw/a']
        .map(url => (window as any).llmaImageFileName('a_fox.png', url)));
    expect(names).toEqual(['a_fox.mp4', 'a_fox.mov', 'a_fox.jpg', 'a_fox.webm', 'a_fox.png', 'a_fox.png']);
});

test('a prompt rewrite that fell back is said under the image, once, with the reason', async ({ llm }) => {
    await chatWithImage(llm, { rewriteStatus: 'fallback', rewriteWarnings: [WARNING] });
    let notice = llm.locator('.llma-tool-rewrite-notice');
    await expect(notice).toBeVisible();
    await expect(notice).toContainText('The prompt was not enhanced.');
    await expect(notice).toContainText(WARNING);
    // The same result drawn again adds no second card or notice.
    await llm.evaluate(() => {
        let w = window as any;
        let bubble = document.querySelector('.llma-msg-row.assistant:last-of-type .llma-msg-bubble');
        w.llmaRenderToolResult(bubble, { id: 'img1', name: 'generate_image', result: { success: true, imageUrl: 'x', rewriteStatus: 'fallback' } });
    });
    await expect(notice).toHaveCount(1);
    await expect(llm.locator('.llma-tool-extra .llma-asset-card')).toHaveCount(1);
});

test('a rewrite that worked says nothing, and Use as Init gives core the address as it writes it', async ({ llm }) => {
    await chatWithImage(llm, { rewriteStatus: 'rewritten', rewriteWarnings: ['One part was tidied.'] });
    let card = llm.locator('.llma-tool-extra .llma-asset-card');
    await expect(card).toBeVisible();
    await expect(llm.locator('.llma-tool-rewrite-notice')).toHaveCount(0);
    await llm.evaluate(() => {
        (window as any).setCurrentImage = (src: string) => (window as any).sentAsInit = src;
    });
    await card.locator('[data-asset-menu]').click();
    await llm.locator('.llma-asset-menu').getByText('Use as Init').click();
    expect(await llm.evaluate(() => (window as any).sentAsInit)).toBe(`View/local/raw/e2e/${FILE}`);
});

// The model's text repeats the image (with a leading slash, another spelling of the same address) and shows one the reply has no card for.
const REPEATING = `Here it is:\n\n![the fox](/${ADDRESS})\n\nAnd an older one:\n\n![another](${OTHER_ADDRESS})`;

for (let rounds of [false, true]) {
    test(`an image the reply also writes into its text shows once, as the card (${rounds ? 'rounds saved' : 'no rounds saved'})`, async ({ llm }) => {
        await chatWithImage(llm, {}, { reply: REPEATING, rounds: rounds });
        await expect(llm.locator('.llma-tool-extra .llma-asset-card')).toHaveCount(1);
        await expect(llm.locator('.llma-tool-extra .llma-asset-card img')).toBeVisible();
        await expect(lastAssistant(llm).locator('img[alt="the fox"]')).toBeHidden();
        await expect(lastAssistant(llm).locator('img[alt="another"]')).toBeVisible();
        await expect(lastAssistant(llm)).toContainText('And an older one:');
        // Text drawn after the card, as a reply that is still arriving is, hides it too.
        await llm.evaluate(address => {
            let w = window as any;
            let bubble = [...document.querySelectorAll('.llma-msg-row.assistant .llma-msg-bubble')].pop() as HTMLElement;
            let segment = w.createDiv(null, 'llma-msg-text-segment');
            bubble.appendChild(segment);
            w.llmaSetRenderedHtml(segment, w.llmaRenderMarkdown(`Again: ![again](${address})`));
        }, ADDRESS);
        await expect(lastAssistant(llm)).toContainText('Again:');
        await expect(lastAssistant(llm).locator('img[alt="again"]')).toBeHidden();
    });
}

test('an image written into a reply that has no card for it stays', async ({ llm }) => {
    await setScenarios([{ match: 'show a picture', rounds: [{ text: `Here it is:\n\n![the fox](${ADDRESS})` }] }]);
    fs.mkdirSync(FOLDER, { recursive: true });
    fs.writeFileSync(path.join(FOLDER, FILE), PNG);
    await send(llm, 'show a picture');
    await expect(lastAssistant(llm).locator('img[alt="the fox"]')).toBeVisible();
});

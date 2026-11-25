import AxeBuilder from '@axe-core/playwright';
import { test, expect, send, lastAssistant, setScenarios, reopen, api } from './fixtures';
import type { Page } from '@playwright/test';

// Accessibility of the chat UI: axe on the main views in the dark and light themes, keyboard navigation, focus, the live region, reduced motion and touch sizes.

// The first code block is long enough to become an asset card, so the card and its viewer are audited too.
const CODE = Array.from({ length: 10 }, (_, i) => `let value${i} = ${i};`).join('\n');
const REPLY = `## Heading\n\nA [link](https://example.com) and \`inline code\`.\n\n\`\`\`js\nlet x = 1;\n\`\`\`\n\n\`\`\`js\n${CODE}\n\`\`\`\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n> quoted`;

/** Runs axe (WCAG 2.0 to 2.2, A and AA) over the LLM Assistant tab and expects nothing. */
async function audit(page: Page, view: string, include: string = '#llma-container') {
    // Entry animations fade things in: axe would measure the colours half way through.
    await page.evaluate(() => Promise.all(document.getAnimations()
        .filter(a => a.effect?.getComputedTiming().iterations !== Infinity)
        .map(a => a.finished.catch(() => null))));
    // Scoped to this extension's own elements: the rest of the page is SwarmUI core's, which a change here cannot fix.
    let results = await new AxeBuilder({ page })
        .include(include)
        .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
        .analyze();
    let problems = results.violations.map(v => ({
        rule: v.id,
        help: v.help,
        nodes: v.nodes.slice(0, 5).map(n => `${n.target.join(' ')} :: ${n.any[0]?.message ?? n.failureSummary}`)
    }));
    expect.soft(problems, `axe violations in: ${view}`).toEqual([]);
}

/** A chat with a branch, a tool call, reasoning and rich Markdown on screen. */
async function richChat(page: Page) {
    await setScenarios([
        { match: 'show everything', rounds: [{ toolCalls: [{ name: 'memory_read', arguments: {} }] }, { text: `<think>thinking it over</think>${REPLY}` }] }
    ]);
    await send(page, 'show everything');
    await expect(lastAssistant(page)).toContainText('quoted', { timeout: 20_000 });
    await expect(page.locator('#llma-send-btn')).toBeVisible();
    let reply = page.locator('.llma-msg-row.assistant').last();
    await reply.hover();
    await reply.locator('.llma-msg-actions').getByRole('button', { name: 'Regenerate' }).click();
    await expect(page.locator('.llma-branch-pager')).toBeVisible({ timeout: 20_000 });
    await expect(page.locator('#llma-send-btn')).toBeVisible();
}

for (let theme of ['modern_dark', 'modern_light']) {
    test.describe(`axe in ${theme}`, () => {
        // An axe pass over a view takes seconds, and each test makes several.
        test.describe.configure({ timeout: 300_000 });

        test.beforeEach(async ({ llm }) => {
            // The page applies the user's own theme setting over any other, so that is what is changed.
            expect((await api(llm, 'ChangeUserSettings', { settings: { theme } })).success).toBe(true);
            await reopen(llm);
            await expect(llm.locator(`link.theme_sheet_header[href*="${theme}.css"]`)).toBeAttached({ timeout: 30_000 });
        });

        test.afterEach(async ({ llm }) => {
            await api(llm, 'ChangeUserSettings', { settings: { theme: 'modern_dark' } });
        });

        test('the chat has no violations', async ({ llm }) => {
            await audit(llm, 'empty chat');
            await richChat(llm);
            await llm.mouse.move(0, 0);
            await audit(llm, 'chat with messages');
            await llm.locator('.llma-msg-row.assistant .llma-msg-actions button').first().focus();
            await llm.locator('.llma-tool-group-head').first().click();
            await llm.locator('.llma-think-summary').first().click();
            await audit(llm, 'chat with the tool call and reasoning open');

            let edit = llm.locator('.llma-msg-row.user').first();
            await edit.hover();
            await edit.getByRole('button', { name: 'Edit' }).click();
            await audit(llm, 'editing a message');
            await llm.keyboard.press('Escape');
        });

        test('popovers and menus have no violations', async ({ llm }) => {
            await richChat(llm);
            await llm.locator('.llma-branch-label').click();
            await expect(llm.locator('.llma-branch-menu')).toBeVisible();
            await audit(llm, 'branch menu');
            await llm.keyboard.press('Escape');
            await llm.locator('#llma-params-btn').click();
            await audit(llm, 'parameters popover');
            await llm.locator('#llma-export-btn').click();
            await audit(llm, 'export menu');
            await llm.keyboard.press('Escape');
            await llm.locator('#llma-input').fill('/');
            await expect(llm.locator('.llma-tool-popup-item').first()).toBeVisible();
            await audit(llm, 'tool picker');
        });

        test('settings have no violations', async ({ llm }) => {
            await llm.locator('#llma-settings-btn').click();
            await expect(llm.locator('#llma-settings-overlay')).toBeVisible();
            for (let tab of await llm.locator('.llma-modal-tab:visible').evaluateAll(tabs => tabs.map(t => (t as HTMLElement).dataset.tab!))) {
                await llm.locator(`.llma-modal-tab[data-tab="${tab}"]`).click();
                await expect(llm.locator(`#llma-settings-${tab}`)).toBeVisible();
                await llm.waitForLoadState('networkidle');
                await audit(llm, `settings: ${tab}`);
            }
            await llm.locator('.llma-modal-tab[data-tab="assistants"]').click();
            await llm.locator('#llma-create-asst-btn').click();
            await audit(llm, 'settings: assistant editor');
        });

        test('dialogs and the chat list have no violations', async ({ llm }) => {
            await richChat(llm);
            await llm.locator('#llma-import-file').setInputFiles({
                name: 'chat.json', mimeType: 'application/json',
                buffer: Buffer.from(JSON.stringify({ title: 'Imported', messages: [{ id: 'a', parentId: null, role: 'user', content: 'hi' }] }))
            });
            await expect(llm.locator('#llma-import-overlay')).toBeVisible();
            await expect(llm.locator('#llma-import-title')).toBeVisible();
            await audit(llm, 'import dialog');
            await llm.keyboard.press('Escape');
            await expect(llm.locator('#llma-import-overlay')).toBeHidden();

            await llm.locator('.llma-asset-card [data-asset-open]').first().click();
            await expect(llm.locator('#llma-asset-overlay')).toBeVisible();
            await audit(llm, 'asset viewer');
            await llm.keyboard.press('Escape');
            await expect(llm.locator('#llma-asset-overlay')).toBeHidden();

            await llm.locator('#llma-thread-search').fill('everything');
            await expect(llm.locator('.llma-thread-snippet').first()).toBeVisible();
            await audit(llm, 'search results');
            await llm.locator('#llma-thread-search').fill('');
            await llm.locator('#llma-thread-select-toggle').click();
            await audit(llm, 'selecting chats');
        });

        test('the phone layout has no violations', async ({ llm }) => {
            await richChat(llm);
            await llm.setViewportSize({ width: 375, height: 740 });
            await audit(llm, 'phone: chat');
            await llm.locator('#llma-sidebar-toggle').click();
            await expect(llm.locator('#llma-sidebar')).toBeInViewport({ ratio: 1 });
            await audit(llm, 'phone: chat list');
            await llm.keyboard.press('Escape');
            await expect(llm.locator('#llma-sidebar')).not.toHaveClass(/sidebar-open/);
            await expect(llm.locator('#llma-sidebar-toggle')).toBeFocused();
        });
    });
}

test('arrow keys move between messages, Enter reaches the actions, and Escape goes back', async ({ llm }) => {
    await setScenarios([{ match: 'one', rounds: [{ text: 'reply one' }] }, { match: 'two', rounds: [{ text: 'reply two' }] }]);
    await send(llm, 'one');
    await expect(lastAssistant(llm)).toContainText('reply one');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await send(llm, 'two');
    await expect(lastAssistant(llm)).toContainText('reply two');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    let rows = llm.locator('#llma-messages .llma-msg-row');
    await expect(rows).toHaveCount(4);
    // One message is in the Tab order: the newest.
    await expect(llm.locator('#llma-messages .llma-msg-row[tabindex="0"]')).toHaveCount(1);
    await expect(rows.nth(3)).toHaveAttribute('tabindex', '0');
    await expect(rows.nth(3)).toHaveAttribute('role', 'article');
    await expect(rows.nth(2)).toHaveAttribute('aria-label', 'Your message');
    await expect(llm.locator('#llma-messages .llma-msg-actions button[tabindex="0"]')).toHaveCount(1);
    await rows.nth(3).focus();
    await llm.keyboard.press('ArrowUp');
    await expect(rows.nth(2)).toBeFocused();
    await llm.keyboard.press('Home');
    await expect(rows.nth(0)).toBeFocused();
    await llm.keyboard.press('ArrowUp');
    await expect(rows.nth(0)).toBeFocused();
    await llm.keyboard.press('ArrowDown');
    await expect(rows.nth(1)).toBeFocused();
    await llm.keyboard.press('Enter');
    let bar = rows.nth(1).getByRole('toolbar', { name: 'Message actions' });
    await expect(bar.getByRole('button', { name: 'Copy', exact: true })).toBeFocused();
    await expect(bar).toHaveCSS('opacity', '1');
    await llm.keyboard.press('ArrowRight');
    await expect(bar.getByRole('button', { name: 'Copy as Markdown' })).toBeFocused();
    await llm.keyboard.press('End');
    await expect(bar.getByRole('button', { name: 'Delete' })).toBeFocused();
    await llm.keyboard.press('ArrowRight');
    await expect(bar.getByRole('button', { name: 'Copy', exact: true })).toBeFocused();
    await llm.keyboard.press('Escape');
    await expect(rows.nth(1)).toBeFocused();
    await llm.keyboard.press('Escape');
    await expect(llm.locator('#llma-input')).toBeFocused();
});

test('a visible focus ring follows the keyboard', async ({ llm }) => {
    await llm.locator('#llma-input').focus();
    await llm.keyboard.press('Tab');
    let ring = await llm.evaluate(() => {
        let style = getComputedStyle(document.activeElement!);
        return { style: style.outlineStyle, width: parseFloat(style.outlineWidth), inside: !!document.activeElement!.closest('#llma-container') };
    });
    expect(ring.inside).toBe(true);
    expect(ring.style).toBe('solid');
    expect(ring.width).toBeGreaterThanOrEqual(2);
});

test('Escape closes each popover and puts focus back on its button', async ({ llm }) => {
    for (let [button, panel] of [['#llma-params-btn', '#llma-params-popover'], ['#llma-export-btn', '#llma-export-menu']]) {
        await expect(llm.locator(button)).toHaveAttribute('aria-expanded', 'false');
        await llm.locator(button).focus();
        await llm.keyboard.press('Enter');
        await expect(llm.locator(panel)).toBeVisible();
        await expect(llm.locator(button)).toHaveAttribute('aria-expanded', 'true');
        // Opened from the keyboard, so focus is inside.
        await expect.poll(() => llm.evaluate(p => !!document.activeElement?.closest(p), panel)).toBe(true);
        await llm.keyboard.press('Escape');
        await expect(llm.locator(panel)).toBeHidden();
        await expect(llm.locator(button)).toBeFocused();
        await expect(llm.locator(button)).toHaveAttribute('aria-expanded', 'false');
    }
    await setScenarios([{ match: 'fork me', rounds: [{ text: 'first' }] }]);
    await send(llm, 'fork me');
    await expect(lastAssistant(llm)).toContainText('first');
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    let reply = llm.locator('.llma-msg-row.assistant').last();
    await reply.hover();
    await reply.getByRole('button', { name: 'Regenerate' }).click();
    await expect(llm.locator('.llma-branch-label')).toBeVisible();
    await expect(llm.locator('#llma-send-btn')).toBeVisible();
    await llm.locator('.llma-branch-label').focus();
    await llm.keyboard.press('Enter');
    await expect(llm.locator('.llma-branch-menu-row.active')).toBeFocused();
    await llm.keyboard.press('ArrowUp');
    await expect(llm.locator('.llma-branch-menu-row').first()).toBeFocused();
    await llm.keyboard.press('Escape');
    await expect(llm.locator('.llma-branch-menu')).toHaveCount(0);
    await expect(llm.locator('.llma-branch-label')).toBeFocused();
});

test('a dialog keeps Tab inside, and closing it returns focus to what opened it', async ({ llm }) => {
    await llm.locator('#llma-settings-btn').focus();
    await llm.keyboard.press('Enter');
    await expect(llm.locator('#llma-settings-close')).toBeFocused();
    for (let i = 0; i < 40; i++) {
        await llm.keyboard.press(i % 8 == 7 ? 'Shift+Tab' : 'Tab');
        expect(await llm.evaluate(() => !!document.activeElement?.closest('#llma-settings-overlay'))).toBe(true);
    }
    let general = llm.locator('.llma-modal-tab[data-tab="general"]');
    await general.focus();
    await llm.keyboard.press('ArrowRight');
    let assistants = llm.locator('.llma-modal-tab[data-tab="assistants"]');
    await expect(assistants).toBeFocused();
    await expect(assistants).toHaveAttribute('aria-selected', 'true');
    await expect(llm.locator('#llma-settings-assistants')).toBeVisible();
    await expect(llm.locator('#llma-settings-assistants')).toHaveAttribute('role', 'tabpanel');
    await llm.keyboard.press('Escape');
    await expect(llm.locator('#llma-settings-overlay')).toBeHidden();
    await expect(llm.locator('#llma-settings-btn')).toBeFocused();
});

test('the live region says when a reply starts, ends or fails, and not once per token', async ({ llm }) => {
    await llm.evaluate(() => {
        (window as any).heard = [];
        let region = document.getElementById('llma-announcer')!;
        new MutationObserver(() => region.textContent && (window as any).heard.push(region.textContent)).observe(region, { childList: true, characterData: true, subtree: true });
    });
    let words = Array.from({ length: 40 }, (_, i) => `token${i}`).join(' ');
    await setScenarios([{ match: 'speak', rounds: [{ text: words, chunkDelayMs: 50 }] }, { match: 'break', rounds: [{ error: { status: 500, message: 'mock upstream failure' } }] }]);
    await send(llm, 'speak');
    await expect(llm.locator('#llma-messages')).toHaveAttribute('aria-busy', 'true');
    await expect(llm.locator('#llma-announcer')).toContainText('Reply complete', { timeout: 20_000 });
    await expect(llm.locator('#llma-messages')).toHaveAttribute('aria-busy', 'false');
    let heard: string[] = await llm.evaluate(() => (window as any).heard);
    expect(heard.length).toBeLessThanOrEqual(3);
    expect(heard.join(' ')).toContain('Assistant is replying');
    expect(heard.join(' ')).not.toContain('token1');
    // Nothing else in the message list is a live region.
    await expect(llm.locator('#llma-messages [aria-live], #llma-messages[aria-live], #llma-messages[role="log"]')).toHaveCount(0);
    await send(llm, 'break');
    await expect(llm.locator('#llma-announcer')).toContainText('Reply failed', { timeout: 20_000 });
});

test('reduced motion stops the animations', async ({ llm }) => {
    await llm.emulateMedia({ reducedMotion: 'reduce' });
    await setScenarios([{ match: 'still', rounds: [{ delayMs: 3000, text: 'done' }] }]);
    await send(llm, 'still');
    await expect(llm.locator('.llma-typing span').first()).toBeAttached();
    let motion = await llm.evaluate(() => ({
        dots: parseFloat(getComputedStyle(document.querySelector('.llma-typing span')!).animationDuration),
        repeats: getComputedStyle(document.querySelector('.llma-typing span')!).animationIterationCount,
        scroll: getComputedStyle(document.getElementById('llma-messages')!).scrollBehavior
    }));
    expect(motion.dots).toBeLessThan(0.01);
    expect(motion.repeats).toBe('1');
    expect(motion.scroll).toBe('auto');
});

test.describe('on a phone', () => {
    test.use({ viewport: { width: 375, height: 740 }, hasTouch: true });

    test('controls are at least 44 pixels each way and the page does not scroll sideways', async ({ llm }) => {
        await richChat(llm);
        let check = async (view: string) => {
            let small = await llm.evaluate(() => Array.from(document.querySelectorAll<HTMLElement>('#llma-container button, #llma-container select, #llma-container [role="button"], #llma-container .llma-segmented > .btn'))
                .filter(el => !el.classList.contains('llma-split-quickbutton') && el.checkVisibility({ visibilityProperty: true, opacityProperty: true }))
                .map(el => ({ el, box: el.getBoundingClientRect() }))
                // Off screen means a closed drawer.
                .filter(({ box }) => box.width > 0 && box.right > 0 && box.left < window.innerWidth)
                .filter(({ box }) => box.width < 43.5 || box.height < 43.5)
                .map(({ el, box }) => `${el.tagName}#${el.id}.${el.className} ${Math.round(box.width)}x${Math.round(box.height)}`));
            expect.soft(small, `small controls in: ${view}`).toEqual([]);
        };
        await check('chat');
        // Every control on screen is inside the screen: nothing runs off its side.
        let outside = await llm.evaluate(() => Array.from(document.querySelectorAll<HTMLElement>('.llma-main button, .llma-main select, .llma-main textarea'))
            .filter(el => el.checkVisibility({ visibilityProperty: true }) && el.getBoundingClientRect().width > 0)
            .filter(el => el.getBoundingClientRect().right > window.innerWidth + 1 || el.getBoundingClientRect().left < -1)
            .map(el => el.id || el.className));
        expect(outside).toEqual([]);
        expect(await llm.evaluate(() => document.getElementById('llma-container')!.scrollLeft)).toBe(0);
        await expect(llm.locator('#llma-send-btn')).toBeInViewport();
        await expect(llm.locator('#llma-input')).toBeInViewport();
        await llm.locator('#llma-sidebar-toggle').click();
        await expect(llm.locator('.llma-thread-item').first()).toBeInViewport();
        await llm.waitForTimeout(400);
        await check('chat list');
        await llm.keyboard.press('Escape');
        await llm.locator('#llma-settings-btn').click();
        await expect(llm.locator('#llma-settings-overlay')).toBeVisible();
        await llm.waitForTimeout(400);
        await check('settings');
    });
});

import { test as base, expect, api } from './fixtures';
import type { Page } from '@playwright/test';

// Prompt styles: the built-in styles, which style a model gets (the harness seeds two image models with no weights: one whose keys make core classify it as Ideogram 4, one it cannot
// place), customizing and resetting a style, the user's rules, and the settings tab.

const IDEOGRAM = 'e2e-ideogram-4.safetensors';
const MYSTERY = 'e2e-mystery.safetensors';

// Every spec starts and ends with none of the user's own styles or rules, so one cannot leak into the next.
const test = base.extend<{ llm: Page }>({
    llm: async ({ llm }, use) => {
        await clear(llm);
        await use(llm);
        await clear(llm);
    }
});

async function clear(page: Page) {
    let listed = await api(page, 'LLMAssistantGetPromptStyles');
    for (let style of listed.styles ?? []) {
        for (let scope of style.scopes) {
            if (scope == 'personal' || scope == 'shared') {
                await api(page, 'LLMAssistantDeletePromptStyle', { id: style.id, scope: scope });
            }
        }
    }
    await api(page, 'LLMAssistantSetStyleOverrides', { overrides: [] });
}

async function resolve(page: Page, axis: string, model: string) {
    let reply = await api(page, 'LLMAssistantResolvePromptStyle', { axis: axis, model: model });
    expect(reply.success, JSON.stringify(reply)).toBe(true);
    return reply;
}

test('the built-in styles are listed, and core classifies the seeded models', async ({ llm }) => {
    let listed = await api(llm, 'LLMAssistantGetPromptStyles');
    expect(listed.success).toBe(true);
    let ids = listed.styles.map((s: any) => s.id);
    expect(ids).toContain('generic');
    expect(ids).toContain('ideogram-4');
    let ideogram = listed.styles.find((s: any) => s.id == 'ideogram-4');
    expect(ideogram.scope).toBe('bundled');
    expect(ideogram.scopes).toEqual(['bundled']);
    expect(ideogram.kind).toBe('image');
    expect(ideogram.format).toBe('json');
    expect(ideogram.matches).toEqual([{ kind: 'class', pattern: 'ideogram-4' }, { kind: 'compat', pattern: 'ideogram-4' }]);
    expect(ideogram.guide).toBe('docs/prompting/ideogram-4.md');
    let seeded = listed.imageModels.find((m: any) => m.name == IDEOGRAM);
    expect(seeded?.classId).toBe('ideogram-4');
    expect(seeded?.className).toBe('Ideogram 4');
    expect(listed.imageModels.find((m: any) => m.name == MYSTERY)?.classId ?? null).toBeNull();
});

test('a model gets the style written for its class, or the default, and the answer says why', async ({ llm }) => {
    let ideogram = await resolve(llm, 'image', IDEOGRAM);
    expect(ideogram.found).toBe(true);
    expect(ideogram.style.id).toBe('ideogram-4');
    expect(ideogram.via).toBe('match');
    expect(ideogram.candidates[0].matched).toBe('class:ideogram-4');
    expect(ideogram.reason).toContain('class:ideogram-4');

    let mystery = await resolve(llm, 'image', MYSTERY);
    expect(mystery.found).toBe(true);
    expect(mystery.style.id).toBe('generic');
    expect(mystery.via).toBe('default');

    let unknown = await resolve(llm, 'image', 'no-such-model.safetensors');
    expect(unknown.found).toBe(false);
    expect(unknown.style.id).toBe('generic');

    // A chat model with no chat style written for it gets none.
    let chat = await resolve(llm, 'llm', 'mock-model');
    expect(chat.style).toBeNull();
    expect(chat.via).toBe('none');
});

test('customizing a built-in style makes a personal copy that wins, and deleting it resets', async ({ llm }) => {
    let bundled = (await api(llm, 'LLMAssistantGetPromptStyle', { id: 'ideogram-4' })).style;
    expect(bundled.scope).toBe('bundled');
    let saved = await api(llm, 'LLMAssistantSavePromptStyle', { id: 'ideogram-4', kind: 'image', name: 'My Ideogram', description: 'mine', matches: 'class:ideogram-4', format: 'json',
        formatter: 'ideogram-4', negative: 'none', supports: 'boxes', body: 'My own instructions.' });
    expect(saved.success, JSON.stringify(saved)).toBe(true);

    let listed = await api(llm, 'LLMAssistantGetPromptStyles');
    let mine = listed.styles.find((s: any) => s.id == 'ideogram-4');
    expect(mine.scope).toBe('personal');
    expect(mine.name).toBe('My Ideogram');
    expect(mine.scopes).toEqual(['bundled', 'personal']);
    expect((await resolve(llm, 'image', IDEOGRAM)).style.scope).toBe('personal');
    // Each copy can still be read on its own.
    expect((await api(llm, 'LLMAssistantGetPromptStyle', { id: 'ideogram-4', scope: 'personal' })).style.body).toBe('My own instructions.');
    expect((await api(llm, 'LLMAssistantGetPromptStyle', { id: 'ideogram-4', scope: 'bundled' })).style.body).toBe(bundled.body);

    expect((await api(llm, 'LLMAssistantDeletePromptStyle', { id: 'ideogram-4', scope: 'personal' })).success).toBe(true);
    let reset = (await api(llm, 'LLMAssistantGetPromptStyles')).styles.find((s: any) => s.id == 'ideogram-4');
    expect(reset.scope).toBe('bundled');
    expect(reset.scopes).toEqual(['bundled']);
    // A built-in style itself cannot be deleted.
    expect((await api(llm, 'LLMAssistantDeletePromptStyle', { id: 'ideogram-4', scope: 'personal' })).success).toBe(false);
});

test('a style can match a model by name pattern, and a chat-model style by name glob', async ({ llm }) => {
    let saved = await api(llm, 'LLMAssistantSavePromptStyle', { id: 'mystery-tags', kind: 'image', name: 'Mystery tags', matches: 'glob:*mystery*', format: 'tags', body: 'Write comma separated tags.' });
    expect(saved.success, JSON.stringify(saved)).toBe(true);
    let mystery = await resolve(llm, 'image', MYSTERY);
    expect(mystery.style.id).toBe('mystery-tags');
    expect(mystery.via).toBe('match');
    // Its own class-based style still wins for the other model.
    expect((await resolve(llm, 'image', IDEOGRAM)).style.id).toBe('ideogram-4');

    let chat = await api(llm, 'LLMAssistantSavePromptStyle', { id: 'mock-chat', kind: 'llm', name: 'Mock chat', matches: 'glob:mock-*', tasks: 'rewrite, chat', body: 'Be brief.' });
    expect(chat.success, JSON.stringify(chat)).toBe(true);
    let resolved = await resolve(llm, 'llm', 'mock-model');
    expect(resolved.style.id).toBe('mock-chat');
    expect(resolved.found).toBe(true);
    expect(resolved.candidates[0].matched).toBe('glob:mock-*');
});

test('a rule pins a model to a style or to none, and a bad rule or style is refused', async ({ llm }) => {
    let set = await api(llm, 'LLMAssistantSetStyleOverrides', { overrides: [{ axis: 'image', match: '*ideogram*', style: 'generic' }, { axis: 'image', match: MYSTERY, style: 'off' }] });
    expect(set.success, JSON.stringify(set)).toBe(true);
    let pinned = await resolve(llm, 'image', IDEOGRAM);
    expect(pinned.style.id).toBe('generic');
    expect(pinned.via).toBe('override');
    let off = await resolve(llm, 'image', MYSTERY);
    expect(off.off).toBe(true);
    expect(off.style).toBeNull();
    expect((await api(llm, 'LLMAssistantGetPromptStyles')).overrides).toHaveLength(2);

    // Refused: nothing changes.
    let badRule = await api(llm, 'LLMAssistantSetStyleOverrides', { overrides: [{ axis: 'image', match: '*x*', style: 'Not A Style' }] });
    expect(badRule.success).toBeFalsy();
    expect((await api(llm, 'LLMAssistantGetPromptStyles')).overrides).toHaveLength(2);
    // A condition the kind cannot use is refused rather than dropped.
    let badStyle = await api(llm, 'LLMAssistantSavePromptStyle', { id: 'bad', kind: 'image', matches: 'family:qwen', body: 'x' });
    expect(badStyle.success).toBeFalsy();
    expect(badStyle.error).toContain('not understood');
    let empty = await api(llm, 'LLMAssistantSavePromptStyle', { id: 'empty', kind: 'image', body: '   ' });
    expect(empty.success).toBeFalsy();

    // A rule that names a style that no longer exists is ignored, and the answer says so.
    await api(llm, 'LLMAssistantSetStyleOverrides', { overrides: [{ axis: 'image', match: '*', style: 'deleted-style' }] });
    let ignored = await resolve(llm, 'image', IDEOGRAM);
    expect(ignored.style.id).toBe('ideogram-4');
    expect(ignored.reason).toContain('does not exist');
});

test('the Prompt styles tab lists the styles, customizes and resets one, and checks a model', async ({ llm }) => {
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('.llma-modal-tab[data-tab="prompt-styles"]').click();
    await expect(llm.locator('#llma-settings-prompt-styles')).toBeVisible();
    let image = llm.locator('#llma-styles-image');
    let ideogramRow = image.locator('li[data-style-id="ideogram-4"]');
    await expect(ideogramRow).toContainText('Ideogram 4 (structured JSON)');
    await expect(ideogramRow).toContainText('Built in');
    await expect(ideogramRow).toContainText('class:ideogram-4');
    await expect(ideogramRow.locator('.llma-style-reset')).toHaveCount(0);
    await expect(image.locator('li[data-style-id="generic"]')).toContainText('Default');
    // The chat model section starts with no styles.
    await expect(llm.locator('#llma-styles-llm')).toContainText('No styles yet.');

    // Customize: the editor opens on the built-in text, and saving keeps a personal copy.
    await ideogramRow.locator('.llma-style-edit').click();
    await expect(llm.locator('#llma-styles-editor')).toBeVisible();
    await expect(llm.locator('#llma-style-id')).toHaveValue('ideogram-4');
    await expect(llm.locator('#llma-style-id')).toBeDisabled();
    await expect(llm.locator('#llma-style-body')).toHaveValue(/You write prompts for Ideogram 4/);
    await llm.locator('#llma-style-name').fill('Ideogram, my way');
    await llm.locator('#llma-style-save').click();
    await expect(llm.locator('#llma-styles-editor')).toBeHidden();
    await expect(ideogramRow).toContainText('Ideogram, my way');
    await expect(ideogramRow).toContainText('Personal');
    await expect(ideogramRow).toContainText('Built in');
    await expect(ideogramRow.locator('.llma-style-reset')).toBeVisible();

    // Check a model: the answer names the style, its scope and the condition that matched.
    await image.locator('.llma-resolve-model').fill(IDEOGRAM);
    await image.locator('.llma-resolve-run').click();
    await expect(image.locator('.llma-resolve-result')).toContainText('Ideogram, my way');
    await expect(image.locator('.llma-resolve-result')).toContainText('personal');
    await expect(image.locator('.llma-resolve-result')).toContainText('Ideogram 4');

    // Reset removes the copy.
    llm.once('dialog', dialog => dialog.accept());
    await ideogramRow.locator('.llma-style-reset').click();
    await expect(ideogramRow).toContainText('Ideogram 4 (structured JSON)');
    await expect(ideogramRow.locator('.llma-style-reset')).toHaveCount(0);
    await expect(ideogramRow).not.toContainText('Personal');
});

test('a rule made in the tab pins a model and shows in the check', async ({ llm }) => {
    await llm.locator('#llma-settings-btn').click();
    await llm.locator('.llma-modal-tab[data-tab="prompt-styles"]').click();
    let image = llm.locator('#llma-styles-image');
    await expect(image.locator('li[data-rule-match]')).toHaveCount(0);
    await image.locator('.llma-rule-match').fill('*mystery*');
    await image.locator('.llma-rule-style').selectOption('ideogram-4');
    await image.locator('.llma-rule-add').click();
    await expect(image.locator('li[data-rule-match="*mystery*"]')).toContainText('ideogram-4');

    await image.locator('.llma-resolve-model').fill(MYSTERY);
    await image.locator('.llma-resolve-run').click();
    await expect(image.locator('.llma-resolve-result')).toContainText('Ideogram 4 (structured JSON)');
    await expect(image.locator('.llma-resolve-result')).toContainText('rule');
    expect((await api(llm, 'LLMAssistantGetPromptStyles')).overrides).toEqual([{ axis: 'image', match: '*mystery*', style: 'ideogram-4' }]);

    await image.locator('li[data-rule-match="*mystery*"] .llma-rule-delete').click();
    await expect(image.locator('li[data-rule-match]')).toHaveCount(0);
    expect((await api(llm, 'LLMAssistantGetPromptStyles')).overrides).toEqual([]);
});

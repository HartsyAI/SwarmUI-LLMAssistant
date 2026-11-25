import { test, expect, send, setScenarios, mockRequests, api } from './fixtures';

// The settings dialog and the per-chat parameters popover are built from SwarmUI's own input helpers and groups.

async function openSettings(page: any, tab = 'general') {
    await page.locator('#llma-settings-btn').click();
    await expect(page.locator('#llma-settings-overlay')).toBeVisible();
    await page.locator(`.llma-modal-tab[data-tab="${tab}"]`).click();
}

test('the general settings are core input groups made of core inputs', async ({ llm }) => {
    await openSettings(llm);
    let form = llm.locator('#llma-settings-general-form');
    await expect(form.locator('.input-group')).toHaveCount(5);
    await expect(form.locator('.auto-slider-box')).toHaveCount(4);
    await expect(form.locator('.auto-checkbox')).toHaveCount(5);
    // Core's tab and toggle markup, not custom classes.
    await expect(llm.locator('.llma-modal-tabs.nav.nav-tabs .nav-link').first()).toBeVisible();
});

test('a slider and its number box stay in step', async ({ llm }) => {
    await openSettings(llm);
    await llm.locator('#llma-s-temperature').fill('1.3');
    await llm.locator('#llma-s-temperature').dispatchEvent('input');
    await expect(llm.locator('#llma-s-temperature_rangeslider')).toHaveValue('1.3');
    await llm.locator('#llma-s-top-p_rangeslider').fill('0.5');
    await expect(llm.locator('#llma-s-top-p')).toHaveValue('0.5');
});

test('the ? button explains a setting', async ({ llm }) => {
    await openSettings(llm);
    await llm.locator('#llma-s-max-tokens').locator('xpath=ancestor::div[contains(@class,"auto-input")]').locator('.info-popover-button').click();
    await expect(llm.locator('#popover_llma-s-max-tokens')).toBeVisible();
    await expect(llm.locator('#popover_llma-s-max-tokens')).toContainText('longest a reply may be');
});

test('a group collapses and opens again from its header', async ({ llm }) => {
    await openSettings(llm);
    let content = llm.locator('#input_group_content_llma_general_interface');
    await expect(content).toBeVisible();
    await llm.locator('#input_group_llma_general_interface .header-label').click();
    await expect(content).toBeHidden();
    await llm.locator('#input_group_llma_general_interface .header-label').click();
    await expect(content).toBeVisible();
});

test('changed settings save, persist and show again when the dialog reopens', async ({ llm }) => {
    await openSettings(llm);
    await llm.locator('#llma-s-max-tokens').fill('2048');
    await llm.locator('#llma-s-markdown').uncheck();
    await llm.locator('#llma-s-temperature').fill('1.1');
    await llm.locator('#llma-settings-save').click();
    await expect(llm.locator('#llma-settings-overlay')).toBeHidden();
    let saved = await api(llm, 'LLMAssistantGetSettings');
    expect(saved.settings.parameters.maxTokens).toBe(2048);
    expect(saved.settings.parameters.temperature).toBeCloseTo(1.1);
    expect(saved.settings.ui.markdownEnabled).toBe(false);
    await llm.reload();
    await llm.evaluate(() => (document.getElementById('maintab_llmassistant') as HTMLElement).click());
    // The Settings button works once the tab has finished starting (its models are listed); a click before that does nothing, which made this test flaky.
    await expect(llm.locator('#llma-model-select option[value="mock-model"]')).toHaveCount(1, { timeout: 30_000 });
    await openSettings(llm);
    await expect(llm.locator('#llma-s-max-tokens')).toHaveValue('2048');
    await expect(llm.locator('#llma-s-markdown')).not.toBeChecked();
    await expect(llm.locator('#llma-s-temperature_rangeslider')).toHaveValue('1.1');
    // put the defaults back for the specs that follow
    await llm.locator('#llma-settings-reset').click();
});

test('companion settings: toggles and the persona and corner dropdowns', async ({ llm }) => {
    await openSettings(llm, 'companion');
    let form = llm.locator('#llma-settings-companion-form');
    await expect(form.locator('.input-group')).toHaveCount(3);
    await expect(llm.locator('#llma-s-companion-persona option').first()).toContainText('active assistant');
    await expect(llm.locator('#llma-s-companion-corner option')).toHaveCount(4);
    await llm.locator('#llma-s-companion-corner').selectOption('top-left');
    await llm.locator('#llma-s-companion-chat-idle-min').fill('12');
    await llm.locator('#llma-settings-save').click();
    let saved = await api(llm, 'LLMAssistantGetSettings');
    expect(saved.settings.companion.corner).toBe('top-left');
    expect(saved.settings.companion.chatter.idleMinutes).toBe(12);
    await openSettings(llm);
    await llm.locator('#llma-settings-reset').click();
});

test('the per-chat parameters popover applies to the next request', async ({ llm }) => {
    await setScenarios([{ match: 'first', rounds: [{ text: 'one' }] }, { match: 'second', rounds: [{ text: 'two' }] }]);
    await send(llm, 'first');
    await expect(llm.locator('.llma-msg-row.assistant').last()).toContainText('one');
    await llm.locator('#llma-params-btn').click();
    await expect(llm.locator('#llma-params-form .auto-input')).toHaveCount(5);
    await llm.locator('#llma-p-temperature').fill('0.3');
    await llm.locator('#llma-p-temperature').dispatchEvent('input');
    await llm.locator('#llma-p-max-tokens').fill('777');
    await llm.locator('#llma-params-apply').click();
    await send(llm, 'second');
    await expect(llm.locator('.llma-msg-row.assistant').last()).toContainText('two');
    let requests = await mockRequests();
    let last = requests.filter(r => r.messages?.some((m: any) => m.role === 'user' && String(m.content).includes('second'))).pop();
    expect(last.temperature).toBeCloseTo(0.3);
    expect(last.max_tokens ?? last.max_completion_tokens).toBe(777);
});

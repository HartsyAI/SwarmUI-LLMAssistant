import { test, expect, api } from './fixtures';

// The few places the extension reaches past a documented core hook are isolated in CoreBridge (C#) and core-bridge.js (front end).
// These specs pin what each of them has to keep doing, so a core change that breaks one shows up here.

test('a per-user API key of the extension\'s types can be saved, and an unknown type is refused', async ({ llm }) => {
    for (let keyType of ['openai_api', 'anthropic_api']) {
        let saved = await api(llm, 'SetAPIKey', { keyType, key: 'sk-e2e-not-a-real-key' });
        expect(saved.error, JSON.stringify(saved)).toBeUndefined();
        let status = await api(llm, 'GetAPIKeyStatus', { keyType });
        expect(status.status).not.toBe('unset');
        await api(llm, 'SetAPIKey', { keyType, key: 'none' });
    }
    let refused = await api(llm, 'SetAPIKey', { keyType: 'bogus_api', key: 'x' });
    expect(refused.error).toBeTruthy();
});

test('the LLM model type is registered, and survives core rebuilding its model sets', async ({ llm }) => {
    let before = await api(llm, 'ListModels', { path: '', depth: 1, subtype: 'LLM' });
    expect(before.error, JSON.stringify(before)).toBeUndefined();
    // Changing a model path makes core clear and rebuild every model set, then fire ModelPathsChangedEvent.
    let changed = await api(llm, 'ChangeServerSettings', { settings: { paths: { sdlorafolder: 'LoraE2E' } } });
    expect(changed.error, JSON.stringify(changed)).toBeUndefined();
    try {
        let after = await api(llm, 'ListModels', { path: '', depth: 1, subtype: 'LLM' });
        expect(after.error, JSON.stringify(after)).toBeUndefined();
        // The tab still lists the LLM backend's models too.
        let models = await api(llm, 'LLMAssistantGetModels');
        expect(models.success).toBe(true);
    }
    finally {
        await api(llm, 'ChangeServerSettings', { settings: { paths: { sdlorafolder: 'Lora' } } });
    }
});

test('the LLM backend cards under Server > Backends are organized into sections', async ({ llm }) => {
    await llm.evaluate(() => (document.getElementById('servertabbutton') as HTMLElement).click());
    await llm.evaluate(() => (document.getElementById('serverbackendstabbutton') as HTMLElement).click());
    await expect(llm.locator('.llma-card-organized').first()).toBeVisible({ timeout: 20_000 });
    await expect(llm.locator('.llma-card-section-title').first()).toBeVisible();
});

test('the front-end image bridge calls core\'s function first, then each listener, and isolates a failing one', async ({ llm }) => {
    let outcome = await llm.evaluate(() => {
        let calls: string[] = [];
        (window as any).gotImageResult = function (image: string) {
            calls.push('core:' + image);
            return 'core-result';
        };
        let bridge = new ((0, eval)('LLMACoreBridge'))();
        bridge.onImageGenerated(() => { throw new Error('a broken listener'); });
        bridge.onImageGenerated((image: string, metadata: string, batchId: string) => calls.push(`listener:${image}:${batchId}`));
        let result = (window as any).gotImageResult('img1', '{}', 'b0');
        return { calls, result, hooked: bridge.imageHooked };
    });
    expect(outcome.hooked).toBe(true);
    expect(outcome.result).toBe('core-result');
    expect(outcome.calls).toEqual(['core:img1', 'listener:img1:b0']);
});

import { defineConfig } from '@playwright/test';

// Offline checks of the live suite's own safety rules: no server, no browser, no model. The rules read their settings from the
// environment when first loaded, so the ones the checks assume are set here.
process.env.LLMA_LIVE_IMAGE_MODEL = 'SDXL/sd_xl_base_1.0.safetensors';
process.env.LLMA_LIVE_TRUSTED_BACKENDS = '2';

export default defineConfig({
    testDir: '.',
    forbidOnly: true,
    reporter: [['list']],
    outputDir: '../../.run/live-selftest'
});

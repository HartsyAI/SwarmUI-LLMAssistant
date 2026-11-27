import { defineConfig } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Offline checks of the live suite's own safety rules: no server, no browser, no model. The rules read their settings from the
// environment when first loaded, so the ones the checks assume are set here.
process.env.LLMA_LIVE_MODEL = 'chat-model';
process.env.LLMA_LIVE_IMAGE_MODEL = 'SDXL/sd_xl_base_1.0.safetensors';
process.env.LLMA_LIVE_TRUSTED_BACKENDS = '2';
process.env.LLMA_LIVE_MAX_REQUESTS = '40';
process.env.LLMA_LIVE_MAX_TOKENS = '100000';
process.env.LLMA_LIVE_KEEP_IMAGE = '';
// A record of its own, so a real run's is never touched.
process.env.LLMA_LIVE_RUN_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '.run', 'live-selftest-state');

export default defineConfig({
    testDir: '.',
    forbidOnly: true,
    workers: 1,
    reporter: [['list']],
    outputDir: '../../.run/live-selftest'
});

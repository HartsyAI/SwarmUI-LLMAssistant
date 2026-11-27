import { defineConfig } from '@playwright/test';
import { config } from './lib/config';

// The opt-in live suite: real scenarios against a SwarmUI that is already running (see README.md). It starts no server and opens no
// browser. The default config (../playwright.config.ts, which CI runs) looks only in ../tests, so nothing here runs there.
export default defineConfig({
    testDir: './scenarios',
    globalSetup: './global-setup.ts',
    globalTeardown: './global-teardown.ts',
    // One server and one GPU: strictly one scenario at a time, and a failure is a finding, so nothing is retried.
    fullyParallel: false,
    workers: 1,
    retries: 0,
    forbidOnly: true,
    // Each scenario may first wait for the server to be free.
    timeout: config.idleTimeoutMs + 2 * config.turnTimeoutMs,
    reporter: [['list'], ['./reporter.ts']],
    outputDir: '../.run/live/results'
});

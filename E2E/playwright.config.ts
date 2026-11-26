import { defineConfig } from '@playwright/test';

// The stack (MockLLM + a throwaway SwarmUI) is started once by global-setup.ts and shared by every spec, so specs run
// serially: they share one server, one user and one thread list.
export default defineConfig({
    testDir: './tests',
    globalSetup: './harness/global-setup.ts',
    globalTeardown: './harness/global-teardown.ts',
    fullyParallel: false,
    workers: 1,
    retries: 0,
    timeout: 60_000,
    expect: { timeout: 10_000 },
    reporter: [['list'], ['html', { open: 'never' }]],
    use: {
        baseURL: `http://localhost:${process.env.E2E_SWARM_PORT || 17801}`,
        trace: 'retain-on-failure',
        screenshot: 'only-on-failure',
        viewport: { width: 1440, height: 900 }
    },
    // Chromium runs everything. Firefox and WebKit run only the specs tagged @smoke (load, send, open settings), so they cost minutes, not another suite.
    projects: [
        { name: 'chromium', use: { browserName: 'chromium' } },
        { name: 'firefox-smoke', use: { browserName: 'firefox' }, grep: /@smoke/ },
        { name: 'webkit-smoke', use: { browserName: 'webkit' }, grep: /@smoke/ }
    ]
});

import path from 'node:path';
import { fileURLToPath } from 'node:url';

/** Where a run keeps its state and report. Under E2E/.run, which is gitignored. The offline self-test points it elsewhere, so it
 * never touches a real run's record. */
export const RUN_DIR = process.env.LLMA_LIVE_RUN_DIR || path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '.run', 'live');

/** Pauses of the clean-up and the busy check. The self-test shortens them. */
export const timing = { sampleMs: 2000, retryMs: 5000, settleMs: 90_000, loadMs: 90_000 };

function text(name: string): string {
    return (process.env[name] ?? '').trim();
}

function num(name: string, fallback: number): number {
    let value = Number(text(name) || fallback);
    if (!Number.isFinite(value) || value < 0) {
        throw new Error(`${name} must be a number that is 0 or more.`);
    }
    return value;
}

/** Everything the suite is told, all from environment variables (see ../README.md). No variable holds a key or a password. */
export const config = {
    baseUrl: text('LLMA_LIVE_BASE_URL').replace(/\/+$/, ''),
    chatModel: text('LLMA_LIVE_MODEL'),
    imageModel: text('LLMA_LIVE_IMAGE_MODEL'),
    embedModel: text('LLMA_LIVE_EMBED_MODEL'),
    /** Backend ids the user vouches are local, for the backend types whose address alone cannot prove it. */
    trustedBackends: text('LLMA_LIVE_TRUSTED_BACKENDS').split(',').map(id => id.trim()).filter(id => id.length > 0),
    maxRequests: num('LLMA_LIVE_MAX_REQUESTS', 40),
    maxTokens: num('LLMA_LIVE_MAX_TOKENS', 120_000),
    maxReplyTokens: num('LLMA_LIVE_MAX_REPLY_TOKENS', 400),
    maxImages: num('LLMA_LIVE_MAX_IMAGES', 1),
    minFreeRamGb: num('LLMA_LIVE_MIN_FREE_RAM_GB', 6),
    minFreeVramGb: num('LLMA_LIVE_MIN_FREE_VRAM_GB', 5),
    minFreeRamImageGb: num('LLMA_LIVE_MIN_FREE_RAM_IMAGE_GB', 10),
    minFreeVramImageGb: num('LLMA_LIVE_MIN_FREE_VRAM_IMAGE_GB', 6),
    maxGpuUtil: num('LLMA_LIVE_MAX_GPU_UTIL', 30),
    idleTimeoutMs: num('LLMA_LIVE_IDLE_TIMEOUT_S', 600) * 1000,
    turnTimeoutMs: num('LLMA_LIVE_TURN_TIMEOUT_S', 300) * 1000,
    imageTimeoutMs: num('LLMA_LIVE_IMAGE_TIMEOUT_S', 720) * 1000,
    keepImage: text('LLMA_LIVE_KEEP_IMAGE') === '1',
    /** Whether turns with tools may run while MCP tools are offered (a reading one runs unasked, and may be metered). */
    allowMcp: text('LLMA_LIVE_ALLOW_MCP') === '1'
};

/** Why the suite cannot run at all, or null when it can. */
export function notConfigured(): string | null {
    if (!config.baseUrl) {
        return 'LLMA_LIVE_BASE_URL is not set (the address of a running SwarmUI).';
    }
    if (!config.chatModel) {
        return 'LLMA_LIVE_MODEL is not set (the id of a chat model that SwarmUI offers).';
    }
    return null;
}

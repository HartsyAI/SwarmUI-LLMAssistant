import fs from 'node:fs';
import path from 'node:path';
import { config, RUN_DIR } from './config';

/** One run's counters and the list of everything it made. Kept in a file because Playwright starts a new worker after a failed test. */
export type RunState = {
    runId: string;
    startedAt: string;
    requests: number;
    tokens: number;
    images: number;
    /** What this run made and has not deleted yet. Only these are ever deleted. */
    created: { threads: string[]; presets: string[]; knowledge: string[]; images: string[] };
    /** The chats in which an image call was allowed, so an image of theirs that cannot be found is reported. */
    imageCalls: string[];
    /** Whether the run began adding a knowledge document, and whether one is being added with its id not yet known. */
    knowledgeStarted: boolean;
    knowledgePending: boolean;
    /** Whether any chat model was in memory before the run; if so, nothing is unloaded. */
    anyModelWasLoaded: boolean;
    /** The image backend the run makes its image on, when it held no model before the run. */
    imageBackendToFree: string | null;
    /** Backends accepted only because they cannot take the run's requests. */
    bystanders: string[];
    usedLlm: boolean;
    notes: string[];
};

const FILE = path.join(RUN_DIR, 'state.json');

export function newState(): RunState {
    return {
        runId: Date.now().toString(36),
        startedAt: new Date().toISOString(),
        requests: 0,
        tokens: 0,
        images: 0,
        created: { threads: [], presets: [], knowledge: [], images: [] },
        imageCalls: [],
        knowledgeStarted: false,
        knowledgePending: false,
        anyModelWasLoaded: false,
        imageBackendToFree: null,
        bystanders: [],
        usedLlm: false,
        notes: []
    };
}

export function readState(): RunState | null {
    return fs.existsSync(FILE) ? JSON.parse(fs.readFileSync(FILE, 'utf8')) : null;
}

export function writeState(state: RunState) {
    fs.mkdirSync(RUN_DIR, { recursive: true });
    fs.writeFileSync(FILE, JSON.stringify(state, null, 2));
}

/** Reads, changes and saves the state. */
export function updateState(change: (state: RunState) => void): RunState {
    let state = readState();
    if (!state) {
        throw new Error('There is no run state; global setup did not run.');
    }
    change(state);
    writeState(state);
    return state;
}

/** Room set aside under the run's caps before a model is asked for something. */
export type Hold = {
    /** Replaces the estimate with what happened. A figure left out keeps its estimate; with <c>incomplete</c> counts the larger of the two stays. */
    settle(requests?: number, tokens?: number, incomplete?: boolean): void;
};

/** The token counts SwarmUI reports for a turn. */
export type Usage = { rounds: number; promptTokens: number; completionTokens: number; incomplete?: boolean };

/** Settles a hold from a turn's reported usage; without a report the estimate stays. <c>extra</c> is what the report does not cover. */
export function settleUsage(held: Hold, usage: Usage | undefined, extra: { requests: number; tokens: number } = { requests: 0, tokens: 0 }) {
    if (usage) {
        held.settle(usage.rounds + extra.requests, usage.promptTokens + usage.completionTokens + extra.tokens, usage.incomplete === true);
    }
}

/** Counts <c>requests</c> and <c>tokens</c> against the caps. Returns the reason when there is no room (and counts nothing). */
export function hold(requests: number, tokens: number, images = 0): Hold | string {
    let state = readState()!;
    if (state.requests + requests > config.maxRequests) {
        return `The request cap would be passed: ${state.requests} used, ${requests} needed, ${config.maxRequests} allowed (LLMA_LIVE_MAX_REQUESTS).`;
    }
    if (state.tokens + tokens > config.maxTokens) {
        return `The token cap would be passed: ${state.tokens} used, about ${tokens} needed, ${config.maxTokens} allowed (LLMA_LIVE_MAX_TOKENS).`;
    }
    if (state.images + images > config.maxImages) {
        return `The image cap would be passed: ${state.images} used, ${config.maxImages} allowed (LLMA_LIVE_MAX_IMAGES).`;
    }
    updateState(s => {
        s.requests += requests;
        s.tokens += tokens;
        s.images += images;
    });
    return {
        settle(actualRequests?: number, actualTokens?: number, incomplete = false) {
            let settled = (estimate: number, actual?: number) => actual === undefined ? estimate : incomplete ? Math.max(estimate, actual) : actual;
            updateState(s => {
                s.requests += settled(requests, actualRequests) - requests;
                s.tokens += settled(tokens, actualTokens) - tokens;
            });
        }
    };
}

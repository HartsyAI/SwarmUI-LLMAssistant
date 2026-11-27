import { test, expect } from '@playwright/test';
import type { SwarmClient } from '../lib/client';
import { config, timing } from '../lib/config';
import { deleteCreated, unloadWhatWasLoaded } from '../lib/server';
import { hold, newState, readState, settleUsage, writeState } from '../lib/state';
import type { Hold, RunState } from '../lib/state';

Object.assign(timing, { sampleMs: 0, retryMs: 5, settleMs: 40, loadMs: 200 });

const MODEL = 'SDXL/sd_xl_base_1.0.safetensors';

type Server = {
    threads?: Record<string, any>;
    presets?: string[];
    knowledge?: { id: string; title: string }[];
    loaded?: string[];
    /** How many times the models are listed before the loaded ones show. */
    loadingFor?: number;
    /** A further model of the local engine, which reads "not loaded" wherever it is. */
    unseen?: { id: string; device: string };
    gpus?: number[];
    resourcesHidden?: boolean;
    imageModel?: string | null;
};

/** A SwarmUI holding these things; <c>did</c> lists every call that changes something. */
function swarm(server: Server) {
    let did: string[] = [];
    let listed = 0;
    let reply = async (route: string, body: any = {}): Promise<any> => {
        if (route === 'LLMAssistantGetThread') {
            return { thread: server.threads?.[body.threadId] ?? { messages: [] } };
        }
        if (route === 'GetMyUserData') {
            return { presets: (server.presets ?? []).map(title => ({ title })) };
        }
        if (route === 'LLMAssistantGetKnowledge') {
            return { documents: server.knowledge ?? [] };
        }
        if (route === 'LLMAssistantGetModels') {
            let done = ++listed > (server.loadingFor ?? 0);
            let unseen = server.unseen ? [{ id: server.unseen.id, provider: 'hartsy-local', metadata: { device: server.unseen.device }, is_loaded: false }] : [];
            return { models: [...['chat-model', 'other-model'].map(id => ({ id, is_loaded: done && (server.loaded ?? []).includes(id) })), ...unseen] };
        }
        if (route === 'GetGlobalStatus' || route === 'GetCurrentStatus') {
            return { status: { live_gens: 0 } };
        }
        if (route === 'GetServerResourceInfo') {
            return server.resourcesHidden ? { error: 'no permission' }
                : { system_ram: { free: 2e10 }, gpus: Object.fromEntries((server.gpus ?? [0]).map((use, i) => [i, { utilization_gpu: use, free_memory: 1e10 }])) };
        }
        if (route === 'ListBackends') {
            return Object.fromEntries(['2', '3'].map(id => [id, { id, type: 'hartsyinference', current_model: server.imageModel === undefined ? MODEL : server.imageModel }]));
        }
        if (route !== 'LLMAssistantStopGeneration') {
            did.push(`${route} ${body.threadId ?? body.path ?? body.docId ?? body.preset ?? body.backend ?? ''}`.trim());
        }
        return { success: true, freed: 1, providers: 1 };
    };
    return { client: { call: reply, ok: reply } as unknown as SwarmClient, did };
}

function run(change: Partial<RunState>) {
    writeState({ ...newState(), runId: 'run1', usedLlm: true, ...change });
}

const made = (url: string, id = 'c1') => ({ messages: [{ role: 'assistant', toolCalls: [{ id, name: 'generate_image', result: { success: true, imageUrl: url } }] }] });
const allowed = (threadId: string) => [{ threadId, callId: 'c1', at: new Date().toISOString() }];

test('deletes only what the run made, finding images and documents it never recorded, and counts only what was there', async () => {
    run({
        created: { threads: ['t1', 't2'], presets: ['never-saved'], knowledge: [], images: [] },
        imageCalls: allowed('t1'), knowledgeStarted: true, knowledgePending: true
    });
    // The image in t2 was approved by a person there, not by the run.
    let { client, did } = swarm({ threads: { t1: made('View/local/raw/late%20one.png'), t2: made('View/local/raw/theirs.png', 'p1') }, knowledge: [{ id: 'k1', title: 'Live smoke run1' }, { id: 'k2', title: 'My notes' }, { id: 'k3', title: 'Live smoke run0' }] });
    let result = await deleteCreated(client);
    expect(did).toEqual(['LLMAssistantDeleteThread t1', 'LLMAssistantDeleteThread t2', 'LLMAssistantDeleteKnowledge k1', 'DeleteImage raw/late one.png']);
    expect(result.deleted).toEqual({ threads: 2, presets: 0, knowledge: 1, images: 1 });
    expect(result.warnings).toEqual([]);
    expect(result.failures).toEqual([]);
    expect(readState()!.created).toEqual({ threads: [], presets: [], knowledge: [], images: [] });
});

test('says that an image or a document may remain when it cannot be found', async () => {
    run({ created: { threads: ['t1'], presets: [], knowledge: [], images: [] }, imageCalls: allowed('t1'), knowledgeStarted: true, knowledgePending: true });
    let result = await deleteCreated(swarm({}).client);
    expect(result.warnings.join(' ')).toMatch(/An image may remain.*t1/);
    expect(result.warnings.join(' ')).toMatch(/knowledge document .* may still appear/);
});

test('keeps the images when asked to, and no longer lists them as the run\'s', async () => {
    run({ created: { threads: ['t1'], presets: [], knowledge: [], images: [] }, imageCalls: allowed('t1') });
    let { client, did } = swarm({ threads: { t1: made('View/local/raw/mine.png') } });
    config.keepImage = true;
    try {
        let result = await deleteCreated(client);
        expect(result.kept).toEqual(['raw/mine.png']);
    }
    finally {
        config.keepImage = false;
    }
    expect(did).toEqual(['LLMAssistantDeleteThread t1']);
    expect(readState()!.created.images).toEqual([]);
});

test('unloads the chat model and frees its own image backend when only the run can have loaded them', async () => {
    run({ imageCalls: allowed('t1'), imageBackendToFree: '2' });
    let { client, did } = swarm({ loaded: ['chat-model'] });
    await unloadWhatWasLoaded(client);
    expect(did).toEqual(['LLMAssistantUnloadModels', 'FreeBackendMemory 2']);
});

for (let [name, state, server] of [
    ['a model was loaded before the run', { anyModelWasLoaded: true }, { loaded: ['chat-model'] }],
    ['another model is loaded now', {}, { loaded: ['chat-model', 'other-model'] }],
    ['GPU use cannot be seen', {}, { loaded: ['chat-model'], resourcesHidden: true }],
    ['no GPU is reported', {}, { loaded: ['chat-model'], gpus: [] }],
    ['one of two GPUs is busy', {}, { loaded: ['chat-model'], gpus: [0, 95] }],
    ['a local model runs off CUDA', {}, { loaded: ['chat-model'], unseen: { id: 'm.gguf', device: 'cpu' } }],
    ['a local model is a checkpoint folder', {}, { loaded: ['chat-model'], unseen: { id: 'moss-vl', device: 'cuda:0' } }]
] as [string, Partial<RunState>, Server][]) {
    test(`leaves the chat model loaded when ${name}`, async () => {
        run(state);
        let { client, did } = swarm(server);
        await unloadWhatWasLoaded(client);
        expect(did).toEqual([]);
    });
}

for (let [name, state, server] of [
    ['no image call was allowed', { imageBackendToFree: '2' }, {}],
    ['it held a model before the run', { imageCalls: allowed('t1') }, {}],
    ['it holds another model now', { imageCalls: allowed('t1'), imageBackendToFree: '2' }, { imageModel: 'Other/model.safetensors' }],
    ['a GPU is busy', { imageCalls: allowed('t1'), imageBackendToFree: '2' }, { gpus: [90] }]
] as [string, Partial<RunState>, Server][]) {
    test(`leaves the image backend alone when ${name}`, async () => {
        run(state);
        let { client, did } = swarm(server);
        await unloadWhatWasLoaded(client);
        expect(did).toEqual([]);
    });
}

test('waits for a load that a cut-off request started, then unloads it', async () => {
    run({ cutOff: true });
    let { client, did } = swarm({ loaded: ['chat-model'], loadingFor: 3 });
    await unloadWhatWasLoaded(client);
    expect(did).toEqual(['LLMAssistantUnloadModels']);
});

test('says so when such a load is never seen', async () => {
    run({ cutOff: true });
    let { client, did } = swarm({});
    expect((await unloadWhatWasLoaded(client)).join(' ')).toContain('could still finish');
    expect(did).toEqual([]);
});

test('keeps the larger figure when a usage report is missing or incomplete', async () => {
    run({});
    let held = () => hold(4, 1000) as Hold;
    let used = () => [readState()!.requests, readState()!.tokens];
    settleUsage(held(), undefined);
    expect(used()).toEqual([4, 1000]);
    settleUsage(held(), { rounds: 1, promptTokens: 90, completionTokens: 10, incomplete: true });
    expect(used()).toEqual([8, 2000]);
    settleUsage(held(), { rounds: 1, promptTokens: 90, completionTokens: 10 }, { requests: 1, tokens: 50 });
    expect(used()).toEqual([10, 2150]);
    expect(hold(100, 1)).toContain('request cap');
});

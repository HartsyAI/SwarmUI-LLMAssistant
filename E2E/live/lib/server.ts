import type { SwarmClient } from './client';
import { config, timing } from './config';
import { listBackends } from './guard';
import { imagesInThread } from './images';
import { readState, updateState } from './state';
import type { RunState } from './state';

const GB = 1024 ** 3;

const pause = (ms: number) => new Promise(done => setTimeout(done, ms));

export async function listChatModels(client: SwarmClient): Promise<any[]> {
    return (await client.ok('LLMAssistantGetModels')).models ?? [];
}

export async function chatModelInfo(client: SwarmClient): Promise<any | null> {
    return (await listChatModels(client)).find((m: any) => m.id === config.chatModel) ?? null;
}

/** Whether SwarmUI has image work waiting, loading or running. <c>everyone</c> says whether every user's work was visible. */
async function generations(client: SwarmClient): Promise<{ busy: string | null; everyone: boolean }> {
    let global = await client.call('GetGlobalStatus');
    let everyone = !global.error;
    if (!everyone) {
        // Without the right to see every user's work, this session's own is all there is to look at.
        global = await client.ok('GetCurrentStatus');
    }
    let busy = Object.entries<number>(global.status ?? {}).filter(([, count]) => count > 0).map(([name, count]) => `${name}=${count}`);
    if (global.backend_status?.any_loading) {
        busy.push('a backend is loading');
    }
    return { busy: busy.length > 0 ? `SwarmUI is busy (${busy.join(', ')})` : null, everyone };
}

type Look = {
    /** Image work in SwarmUI, or null. */
    busy: string | null;
    /** The least free system RAM and the most free memory on one GPU over the samples; Infinity when not visible. */
    ram: number;
    vram: number;
    /** The use of the busiest GPU (the median sample). */
    util: number;
    /** Whether everyone's work and the use of the GPUs could be seen. Without that, nothing proves the server is unused. */
    known: boolean;
};

/** Looks at the server through SwarmUI's own routes. A model request from another program does not show in SwarmUI's counters, so
 * memory and GPU use are looked at too. */
async function look(client: SwarmClient): Promise<Look> {
    let work = await generations(client);
    let seen: Look = { busy: work.busy, ram: Infinity, vram: Infinity, util: 0, known: false };
    if (work.busy) {
        return seen;
    }
    let utils: number[] = [];
    for (let sample = 0; sample < 3; sample++) {
        let info = await client.call('GetServerResourceInfo');
        if (info.error) {
            return seen;
        }
        seen.ram = Math.min(seen.ram, info.system_ram.free / GB);
        let gpus = Object.values<any>(info.gpus ?? {});
        if (gpus.length > 0) {
            seen.vram = Math.min(seen.vram, Math.max(...gpus.map(g => g.free_memory / GB)));
            utils.push(Math.max(...gpus.map(g => g.utilization_gpu)));
        }
        if (sample < 2) {
            await pause(timing.sampleMs);
        }
    }
    seen.util = utils.length > 0 ? utils.sort((a, b) => a - b)[Math.floor(utils.length / 2)] : 0;
    seen.known = work.everyone && utils.length === 3;
    return seen;
}

/** Why the server is not ready for a GPU scenario, or null when it is. */
export async function busyReason(client: SwarmClient, kind: 'llm' | 'image'): Promise<{ reason: string | null; seen: string }> {
    let now = await look(client);
    if (now.busy) {
        return { reason: now.busy, seen: now.busy };
    }
    let loaded = kind === 'llm' && (await chatModelInfo(client))?.is_loaded === true;
    let minRam = kind === 'image' ? config.minFreeRamImageGb : config.minFreeRamGb;
    let minVram = kind === 'image' ? config.minFreeVramImageGb : config.minFreeVramGb;
    let gb = (value: number) => value === Infinity ? 'unknown' : `${value.toFixed(1)} GB`;
    let seen = `free RAM ${gb(now.ram)}, free VRAM ${gb(now.vram)}, GPU use ${now.util}%${loaded ? ', chat model already loaded' : ''}${now.known ? '' : ' (not everything is visible to this user)'}`;
    // A chat model that is already in memory needs no more of it.
    if (!loaded && now.ram < minRam) {
        return { reason: `free system RAM is ${now.ram.toFixed(1)} GB, under the ${minRam} GB wanted`, seen };
    }
    if (!loaded && now.vram < minVram) {
        return { reason: `free GPU memory is ${now.vram.toFixed(1)} GB, under the ${minVram} GB wanted`, seen };
    }
    if (now.util > config.maxGpuUtil) {
        return { reason: `a GPU is ${now.util}% busy, over the ${config.maxGpuUtil}% allowed`, seen };
    }
    return { reason: null, seen };
}

/** Why memory must not be freed now, or null when the server is seen to be unused. Waits a little for it to settle. */
async function inUse(client: SwarmClient): Promise<string | null> {
    let reason: string | null = null;
    for (let attempt = 0; attempt < 6; attempt++) {
        let now = await look(client);
        reason = now.busy ?? (!now.known ? 'whether the server is in use cannot be seen' : now.util > config.maxGpuUtil ? `a GPU is ${now.util}% busy` : null);
        if (!reason || (!now.busy && !now.known)) {
            return reason;
        }
        await pause(timing.retryMs);
    }
    return reason;
}

export type Cleanup = {
    /** How many things of each kind were there and are now deleted. */
    deleted: { threads: number; presets: number; knowledge: number; images: number };
    /** Things the run still lists because deleting them failed. */
    failures: string[];
    /** Things that may be left although the run could not find them. */
    warnings: string[];
    /** Images left on purpose (LLMA_LIVE_KEEP_IMAGE). */
    kept: string[];
};

/** Before a chat goes, reads what the image calls the run allowed in it made. A turn that was just stopped saves its reply a little
 * later, so the calls are given that time to appear. */
async function recordImages(client: SwarmClient, threadId: string, callIds: string[], warnings: string[]) {
    let deadline = Date.now() + timing.settleMs;
    while (callIds.length > 0) {
        let thread = (await client.ok('LLMAssistantGetThread', { threadId })).thread;
        let saved = new Set((thread.messages ?? []).flatMap((m: any) => (m.toolCalls ?? []).map((c: any) => c.id)));
        if (callIds.every(id => saved.has(id)) || Date.now() >= deadline) {
            updateState(s => s.created.images = [...new Set([...s.created.images, ...imagesInThread(thread, callIds)])]);
            if (callIds.some(id => imagesInThread(thread, [id]).length === 0)) {
                warnings.push(`An image may remain in your image history: an image call was allowed in chat ${threadId}, but the chat holds no address of its image.`);
            }
            return;
        }
        await pause(timing.retryMs);
    }
}

/** Finds knowledge documents by the run id in their title: one whose adding timed out has no recorded id. */
async function recordKnowledge(client: SwarmClient, state: RunState, warnings: string[]) {
    if (!state.knowledgeStarted) {
        return;
    }
    let library = await client.ok('LLMAssistantGetKnowledge');
    let found: string[] = (library.documents ?? []).filter((d: any) => String(d.title).includes(`Live smoke ${state.runId}`)).map((d: any) => d.id);
    let unrecorded = found.filter(id => !state.created.knowledge.includes(id));
    updateState(s => s.created.knowledge = [...s.created.knowledge, ...unrecorded]);
    if (state.knowledgePending && unrecorded.length === 0) {
        warnings.push(`A knowledge document titled "Live smoke ${state.runId}..." may still appear: adding it did not finish.`);
    }
}

/** Deletes what the run made, and nothing else. */
export async function deleteCreated(client: SwarmClient): Promise<Cleanup> {
    let result: Cleanup = { deleted: { threads: 0, presets: 0, knowledge: 0, images: 0 }, failures: [], warnings: [], kept: [] };
    let remove = async (kind: keyof Cleanup['deleted'], id: string, action: () => Promise<boolean>) => {
        try {
            if (await action()) {
                result.deleted[kind]++;
            }
            updateState(s => s.created[kind] = s.created[kind].filter(other => other !== id));
        }
        catch (error: any) {
            result.failures.push(`${kind} ${id}: ${error.message}`);
        }
    };
    let state = readState();
    if (!state) {
        return result;
    }
    for (let id of state.created.threads) {
        await remove('threads', id, async () => {
            await client.call('LLMAssistantStopGeneration', { threadId: id });
            // Read first: once the chat is gone, nothing says which images it made.
            await recordImages(client, id, state.imageCalls.filter(c => c.threadId === id).map(c => c.callId), result.warnings);
            await client.ok('LLMAssistantDeleteThread', { threadId: id });
            return true;
        });
    }
    for (let title of state.created.presets) {
        await remove('presets', title, async () => {
            let mine = await client.ok('GetMyUserData');
            if (!(mine.presets ?? []).some((p: any) => p.title === title)) {
                return false;
            }
            await client.ok('DeletePreset', { preset: title });
            return true;
        });
    }
    try {
        await recordKnowledge(client, readState()!, result.warnings);
    }
    catch (error: any) {
        result.failures.push(`knowledge documents of this run could not be listed: ${error.message}`);
    }
    for (let id of readState()!.created.knowledge) {
        await remove('knowledge', id, async () => {
            await client.ok('LLMAssistantDeleteKnowledge', { docId: id });
            return true;
        });
    }
    // Read again: the chats above may have added images.
    let images = readState()!.created.images;
    if (config.keepImage) {
        // Kept images stop being the run's to delete, so no later run reports them as left behind.
        result.kept = images;
        updateState(s => s.created.images = []);
        return result;
    }
    for (let image of images) {
        await remove('images', image, async () => {
            await client.ok('DeleteImage', { path: image });
            return true;
        });
    }
    return result;
}

/** The local engine reports as loaded only a GGUF on CUDA; any other model of its reads "not loaded" even while it is in memory. */
function loadedStateUnknown(model: any): boolean {
    return model.provider === 'hartsy-local' && (!String(model.metadata?.device ?? '').startsWith('cuda') || !String(model.id).toLowerCase().endsWith('.gguf'));
}

/** Unloads the chat model when only this run can have loaded what is loaded, and the server is seen to be unused. The route unloads
 * every provider's models, so one other loaded model is reason enough to leave them all. Returns what it did, or null for nothing. */
export async function unloadOwnChatModel(client: SwarmClient): Promise<string | null> {
    let state = readState();
    if (!state?.usedLlm) {
        return null;
    }
    if (state.anyModelWasLoaded) {
        return 'A chat model was loaded before the run, so nothing was unloaded.';
    }
    if ((await listChatModels(client)).some(loadedStateUnknown)) {
        return 'Nothing was unloaded: SwarmUI cannot say whether every local model is loaded (one is not on CUDA, or is a checkpoint folder). Unload from the top bar if you want it gone.';
    }
    let listLoaded = async () => (await listChatModels(client)).filter(m => m.is_loaded === true).map(m => m.id as string);
    let loaded = await listLoaded();
    // A model is not listed as loaded until its load is done, which can be after the request that started it was given up.
    for (let deadline = Date.now() + timing.loadMs; loaded.length === 0 && state.cutOff && Date.now() < deadline;) {
        await pause(timing.retryMs);
        loaded = await listLoaded();
    }
    if (loaded.length === 0) {
        return state.cutOff ? 'No chat model was seen loaded; a load that a cut-off request started could still finish.' : null;
    }
    let others = loaded.filter(id => id !== config.chatModel);
    if (others.length > 0) {
        return `Nothing was unloaded: a model this run does not use is loaded too (${others.join(', ')}).`;
    }
    let reason = await inUse(client);
    if (reason) {
        return `The chat model was left loaded: ${reason}.`;
    }
    let reply = await client.ok('LLMAssistantUnloadModels', {}, 120_000);
    return `Unloaded the chat model (${reply.freed} of ${reply.providers} LLM providers freed something).`;
}

const plain = (name: unknown) => String(name ?? '').toLowerCase().replace(/\.safetensors$/, '');

/** Frees the one image backend the run made its image on, if it was empty before and holds the run's model now. */
async function freeOwnImageModel(client: SwarmClient): Promise<string | null> {
    let state = readState()!;
    // Without an allowed image call the run loaded no image model.
    if (!state.imageBackendToFree || state.imageCalls.length === 0) {
        return null;
    }
    let backend = (await listBackends(client)).find(b => b.id === state.imageBackendToFree);
    if (!backend?.currentModel || plain(backend.currentModel) !== plain(config.imageModel)) {
        return null;
    }
    let reason = await inUse(client);
    if (reason) {
        return `Image backend ${backend.id} still holds the run's model: ${reason}.`;
    }
    await client.ok('FreeBackendMemory', { system_ram: false, backend: backend.id }, 120_000);
    return `Freed the GPU memory of image backend ${backend.id}, which held no model before the run.`;
}

/** Frees only what the run loaded, and only while the server is unused. Returns what it did. */
export async function unloadWhatWasLoaded(client: SwarmClient): Promise<string[]> {
    if (!readState()) {
        return [];
    }
    let done = [await unloadOwnChatModel(client), await freeOwnImageModel(client)];
    return done.filter(line => line !== null);
}

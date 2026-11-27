import type { SwarmClient } from './client';
import { config } from './config';
import { listBackends } from './guard';
import { readState, updateState } from './state';

const GB = 1024 ** 3;

export async function chatModelInfo(client: SwarmClient): Promise<any | null> {
    let reply = await client.ok('LLMAssistantGetModels');
    return (reply.models ?? []).find((m: any) => m.id === config.chatModel) ?? null;
}

/** Whether SwarmUI has no image work waiting, loading or running, for any user. Null when it will not say. */
async function generationsIdle(client: SwarmClient): Promise<string | null> {
    let global = await client.call('GetGlobalStatus');
    if (global.error) {
        // Without the right to see every user's work, this session's own is all there is to look at.
        global = await client.ok('GetCurrentStatus');
    }
    let busy = Object.entries<number>(global.status ?? {}).filter(([, count]) => count > 0).map(([name, count]) => `${name}=${count}`);
    if (global.backend_status?.any_loading) {
        busy.push('a backend is loading');
    }
    return busy.length > 0 ? `SwarmUI is busy (${busy.join(', ')})` : null;
}

/** Why the server is not ready for a GPU scenario, or null when it is. Reads memory and GPU use through SwarmUI's own resource route;
 * a model request from another program does not show in SwarmUI's counters, so free memory and GPU use are looked at too. */
export async function busyReason(client: SwarmClient, kind: 'llm' | 'image'): Promise<{ reason: string | null; seen: string }> {
    let busy = await generationsIdle(client);
    if (busy) {
        return { reason: busy, seen: busy };
    }
    let loaded = kind === 'llm' && (await chatModelInfo(client))?.is_loaded === true;
    let minRam = kind === 'image' ? config.minFreeRamImageGb : config.minFreeRamGb;
    let minVram = kind === 'image' ? config.minFreeVramImageGb : config.minFreeVramGb;
    let free = { ram: Infinity, vram: Infinity };
    let utils: number[] = [];
    for (let sample = 0; sample < 3; sample++) {
        let info = await client.call('GetServerResourceInfo');
        if (info.error) {
            return { reason: null, seen: 'memory and GPU use not visible to this user, so not checked' };
        }
        free.ram = Math.min(free.ram, info.system_ram.free / GB);
        let gpus = Object.values<any>(info.gpus ?? {});
        if (gpus.length > 0) {
            free.vram = Math.min(free.vram, Math.max(...gpus.map(g => g.free_memory / GB)));
            utils.push(Math.min(...gpus.map(g => g.utilization_gpu)));
        }
        if (sample < 2) {
            await new Promise(done => setTimeout(done, 2000));
        }
    }
    let util = utils.length > 0 ? utils.sort((a, b) => a - b)[Math.floor(utils.length / 2)] : 0;
    let seen = `free RAM ${free.ram.toFixed(1)} GB, free VRAM ${free.vram === Infinity ? 'unknown' : `${free.vram.toFixed(1)} GB`}, GPU use ${util}%${loaded ? ', chat model already loaded' : ''}`;
    // A chat model that is already in memory needs no more of it.
    if (!loaded && free.ram < minRam) {
        return { reason: `free system RAM is ${free.ram.toFixed(1)} GB, under the ${minRam} GB wanted`, seen };
    }
    if (!loaded && free.vram < minVram) {
        return { reason: `free GPU memory is ${free.vram.toFixed(1)} GB, under the ${minVram} GB wanted`, seen };
    }
    if (util > config.maxGpuUtil) {
        return { reason: `the GPU is ${util}% busy, over the ${config.maxGpuUtil}% allowed`, seen };
    }
    return { reason: null, seen };
}

/** Deletes what the run made and still lists. Returns one line per thing it could not delete. */
export async function deleteCreated(client: SwarmClient): Promise<string[]> {
    let failures: string[] = [];
    let remove = async (kind: keyof NonNullable<ReturnType<typeof readState>>['created'], id: string, action: () => Promise<void>) => {
        try {
            await action();
            updateState(s => s.created[kind] = s.created[kind].filter(other => other !== id));
        }
        catch (error: any) {
            failures.push(`${kind} ${id}: ${error.message}`);
        }
    };
    let state = readState();
    if (!state) {
        return failures;
    }
    for (let id of state.created.threads) {
        await remove('threads', id, async () => {
            await client.call('LLMAssistantStopGeneration', { threadId: id });
            await client.ok('LLMAssistantDeleteThread', { threadId: id });
        });
    }
    for (let title of state.created.presets) {
        await remove('presets', title, async () => {
            let mine = await client.ok('GetMyUserData');
            if ((mine.presets ?? []).some((p: any) => p.title === title)) {
                await client.ok('DeletePreset', { preset: title });
            }
        });
    }
    for (let id of state.created.knowledge) {
        await remove('knowledge', id, async () => {
            await client.ok('LLMAssistantDeleteKnowledge', { docId: id });
        });
    }
    for (let image of config.keepImage ? [] : state.created.images) {
        await remove('images', image, async () => {
            await client.ok('DeleteImage', { path: image });
        });
    }
    return failures;
}

/** Unloads the chat model when this run is what loaded it and nothing is generating. Returns what it did, or null when there was nothing to do. */
export async function unloadOwnChatModel(client: SwarmClient): Promise<string | null> {
    let state = readState();
    if (!state?.usedLlm) {
        return null;
    }
    if (state.llmWasLoaded) {
        return 'The chat model was loaded before the run, so it was left loaded.';
    }
    if (await generationsIdle(client)) {
        return 'The chat model was left loaded: SwarmUI was busy.';
    }
    let reply = await client.ok('LLMAssistantUnloadModels', {}, 120_000);
    return `Unloaded the chat model (${reply.freed} of ${reply.providers} LLM providers freed something).`;
}

/** Frees only what the run loaded, and only while nothing else is generating. Returns what it did. */
export async function unloadWhatWasLoaded(client: SwarmClient): Promise<string[]> {
    let state = readState();
    let done: string[] = [];
    if (!state) {
        return done;
    }
    let unloaded = await unloadOwnChatModel(client);
    if (unloaded) {
        done.push(unloaded);
    }
    if (state.usedImageBackends && state.emptyImageBackends.length > 0 && !await generationsIdle(client)) {
        for (let backend of await listBackends(client)) {
            if (state.emptyImageBackends.includes(backend.id) && backend.currentModel) {
                await client.ok('FreeBackendMemory', { system_ram: false, backend: backend.id }, 120_000);
                done.push(`Freed the GPU memory of image backend ${backend.id}, which held no model before the run.`);
            }
        }
    }
    return done;
}

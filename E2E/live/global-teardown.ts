import { SwarmClient } from './lib/client';
import { config } from './lib/config';
import { checkBackends, listBackends, usedWithin } from './lib/guard';
import { deleteCreated, unloadWhatWasLoaded } from './lib/server';
import { readState, updateState } from './lib/state';

/** Deletes what the run made (and nothing else), unloads the models it loaded, and checks the backends once more. */
export default async function globalTeardown() {
    if (!readState()) {
        return;
    }
    let client = new SwarmClient(config.baseUrl);
    let lines: string[] = [];
    let problems: string[] = [];
    try {
        let cleanup = await deleteCreated(client);
        let { threads, presets, knowledge, images } = cleanup.deleted;
        lines.push(`Deleted ${threads} chats, ${presets} presets, ${knowledge} knowledge documents and ${images} images that the run made.`);
        if (cleanup.failures.length > 0) {
            lines.push(`NOT deleted, remove by hand: ${cleanup.failures.join('; ')}`);
        }
        lines.push(...cleanup.warnings);
        let kept = readState()!.created.images;
        if (config.keepImage && kept.length > 0) {
            lines.push(`Kept because LLMA_LIVE_KEEP_IMAGE=1: ${kept.join(', ')}`);
        }
        lines.push(...await unloadWhatWasLoaded(client));
    }
    catch (error: any) {
        lines.push(`Clean-up failed: ${error.message}. Left to delete: ${JSON.stringify(readState()!.created)}`);
    }
    try {
        await checkBackends(client);
        let state = readState()!;
        // Only an image call of the run could have reached one; a request that ended late shows here.
        if (state.imageCalls.length > 0) {
            let used = usedWithin(await listBackends(client), state.bystanders, (Date.now() - Date.parse(state.startedAt)) / 1000);
            if (used.length > 0) {
                problems.push(`Backend ${used.join(', ')}, which should not take the run's requests, was used during the run (by this run or by someone else).`);
            }
        }
    }
    catch (error: any) {
        problems.push(`The backends could not be accepted at the end of the run: ${error.message}`);
    }
    updateState(s => s.notes.push(...lines, ...problems));
    if (problems.length > 0) {
        throw new Error(problems.join(' '));
    }
}

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
        if (cleanup.kept.length > 0) {
            lines.push(`Kept because LLMA_LIVE_KEEP_IMAGE=1: ${cleanup.kept.join(', ')}`);
        }
        lines.push(...await unloadWhatWasLoaded(client));
    }
    catch (error: any) {
        lines.push(`Clean-up failed: ${error.message}. Left to delete: ${JSON.stringify(readState()!.created)}`);
    }
    let bystanders = readState()!.bystanders;
    try {
        // One added while the run went on is looked at too.
        bystanders = [...new Set([...bystanders, ...(await checkBackends(client)).bystanders])];
    }
    catch (error: any) {
        problems.push(`The backends could not be accepted at the end of the run: ${error.message}`);
    }
    try {
        let first = readState()!.imageCalls[0];
        // Only an image call of the run could have reached one, from the first that was allowed; a request that ended late shows here.
        if (first) {
            let used = usedWithin(await listBackends(client), bystanders, (Date.now() - Date.parse(first.at)) / 1000);
            if (used.length > 0) {
                problems.push(`Backend ${used.join(', ')}, which should not take the run's requests, was used since the run's first image call (by this run or by someone else).`);
            }
        }
    }
    catch (error: any) {
        problems.push(`Whether another backend was used could not be checked: ${error.message}`);
    }
    updateState(s => s.notes.push(...lines, ...problems));
    if (problems.length > 0) {
        throw new Error(problems.join(' '));
    }
}

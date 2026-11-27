import { SwarmClient } from './lib/client';
import { config } from './lib/config';
import { deleteCreated, unloadWhatWasLoaded } from './lib/server';
import { readState, updateState } from './lib/state';

/** Deletes what the run made (and nothing else) and unloads the models it loaded. */
export default async function globalTeardown() {
    if (!readState()) {
        return;
    }
    let client = new SwarmClient(config.baseUrl);
    let before = readState()!.created;
    let counts = Object.entries(before).map(([kind, list]) => `${list.length} ${kind}`).join(', ');
    let lines: string[] = [];
    try {
        let failures = await deleteCreated(client);
        lines.push(failures.length > 0 ? `NOT deleted, remove by hand: ${failures.join('; ')}` : `Deleted what the run made (${counts}).`);
        if (config.keepImage && before.images.length > 0) {
            lines.push(`Kept the image(s) because LLMA_LIVE_KEEP_IMAGE=1: ${before.images.join(', ')}`);
        }
        lines.push(...await unloadWhatWasLoaded(client));
    }
    catch (error: any) {
        lines.push(`Clean-up failed: ${error.message}. Left to delete: ${JSON.stringify(readState()!.created)}`);
    }
    updateState(s => s.notes.push(...lines));
}

import fs from 'node:fs';
import { SwarmClient } from './lib/client';
import { config, notConfigured, RUN_DIR } from './lib/config';
import { checkBackends, listBackends, listImageModels } from './lib/guard';
import { chatModelInfo } from './lib/server';
import { newState, readState, writeState } from './lib/state';

/** Checks the backends before anything is asked of any model, and starts the run's record. Throwing here runs no scenario. */
export default async function globalSetup() {
    let previous = readState();
    // An earlier run's record and report are never read as this run's.
    fs.rmSync(`${RUN_DIR}/report.md`, { force: true });
    if (previous && Object.values(previous.created).some(list => list.length > 0)) {
        // This run deletes only its own things, so the earlier run's record is kept for whoever removes them.
        let kept = `${RUN_DIR}/leftover-${previous.runId}.json`;
        fs.renameSync(`${RUN_DIR}/state.json`, kept);
        console.warn(`An earlier run (${previous.runId}) did not delete everything it made. This run leaves it alone; see ${kept}: ${JSON.stringify(previous.created)}`);
    }
    fs.rmSync(`${RUN_DIR}/state.json`, { force: true });
    let missing = notConfigured();
    if (missing) {
        console.log(`Live suite not configured, every scenario is skipped: ${missing}`);
        return;
    }
    let client = new SwarmClient(config.baseUrl);
    let guard = await checkBackends(client);
    let model = await chatModelInfo(client);
    if (!model) {
        throw new Error(`LLMA_LIVE_MODEL "${config.chatModel}" is not offered by this SwarmUI.`);
    }
    let state = newState();
    state.llmWasLoaded = model.is_loaded === true;
    state.emptyImageBackends = (await listBackends(client)).filter(b => guard.image.includes(b.id) && !b.currentModel).map(b => b.id);
    state.notes.push(`Chat model ${model.id} (provider ${model.provider}, context length ${model.context_length ?? 'not listed'}${state.llmWasLoaded ? ', already loaded' : ''}).`);
    state.notes.push(`Backends accepted for LLM requests: ${guard.llm.join(', ')}.${config.imageModel ? ` For the image: ${guard.image.join(', ') || 'none'}.` : ''}`);
    state.notes.push(...guard.notes);
    if (config.imageModel && !(await listImageModels(client)).includes(config.imageModel)) {
        state.notes.push(`LLMA_LIVE_IMAGE_MODEL "${config.imageModel}" is not in SwarmUI's model list.`);
    }
    writeState(state);
}

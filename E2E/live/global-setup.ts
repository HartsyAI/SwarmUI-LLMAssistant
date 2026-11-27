import fs from 'node:fs';
import { SwarmClient } from './lib/client';
import { config, notConfigured, RUN_DIR } from './lib/config';
import { checkBackends, listBackends, listImageModels } from './lib/guard';
import { listChatModels } from './lib/server';
import { hasLeftovers, newState, readState, setAsideUnreadable, writeState } from './lib/state';

/** Checks the backends before anything is asked of any model, and starts the run's record. Throwing here runs no scenario. */
export default async function globalSetup() {
    let unreadable = setAsideUnreadable();
    if (unreadable) {
        console.warn(`The record of an earlier run could not be read; it may list things that run did not delete. See ${unreadable}.`);
    }
    let previous = readState();
    // An earlier run's record and report are never read as this run's.
    fs.rmSync(`${RUN_DIR}/report.md`, { force: true });
    if (previous && hasLeftovers(previous)) {
        // This run deletes only its own things, so the earlier run's record is kept for whoever removes them.
        let kept = `${RUN_DIR}/leftover-${previous.runId}.json`;
        fs.renameSync(`${RUN_DIR}/state.json`, kept);
        console.warn(`An earlier run (${previous.runId}) may have left something behind. This run leaves it alone; see ${kept}: ${JSON.stringify(previous.created)}${previous.knowledgePending ? ', and a knowledge document that was still being added' : ''}`);
    }
    fs.rmSync(`${RUN_DIR}/state.json`, { force: true });
    let missing = notConfigured();
    if (missing) {
        console.log(`Live suite not configured, every scenario is skipped: ${missing}`);
        return;
    }
    let client = new SwarmClient(config.baseUrl);
    let guard = await checkBackends(client);
    let models = await listChatModels(client);
    let model = models.find(m => m.id === config.chatModel);
    if (!model) {
        throw new Error(`LLMA_LIVE_MODEL "${config.chatModel}" is not offered by this SwarmUI.`);
    }
    let state = newState();
    state.anyModelWasLoaded = models.some(m => m.is_loaded === true);
    let imageBackend = (await listBackends(client)).find(b => b.id === guard.image[0]);
    state.imageBackendToFree = imageBackend && !imageBackend.currentModel ? imageBackend.id : null;
    state.bystanders = guard.bystanders;
    state.notes.push(`Chat model ${model.id} (provider ${model.provider}, context length ${model.context_length ?? 'not listed'}${model.is_loaded ? ', already loaded' : ''}).`);
    state.notes.push(`Backends accepted for LLM requests: ${guard.llm.join(', ')}.${config.imageModel ? ` For the image: ${guard.image[0] ?? 'none'}.` : ''}`);
    state.notes.push(...guard.notes);
    if (config.imageModel && !(await listImageModels(client)).includes(config.imageModel)) {
        state.notes.push(`LLMA_LIVE_IMAGE_MODEL "${config.imageModel}" is not in SwarmUI's model list.`);
    }
    writeState(state);
}

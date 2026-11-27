import { test, expect, config } from '../lib/live';
import { updateState } from '../lib/state';

test('knowledge search finds a document by meaning with the embedding model', async ({ live }) => {
    if (!config.embedModel) {
        live.skip('LLMA_LIVE_EMBED_MODEL is not set.');
    }
    let library = await live.client.ok('LLMAssistantGetKnowledge');
    if (library.settings?.model !== config.embedModel || !library.settings?.providerId) {
        live.skip(`The knowledge library's embedding model is "${library.settings?.model ?? ''}", not "${config.embedModel}". Choose it in Settings > Knowledge (the suite changes no settings).`);
    }
    await live.whenIdle();
    // The document and the query are one embedding request each.
    live.reserve(2, 500);
    let added = await live.client.ok('LLMAssistantAddKnowledge', { name: `live-smoke-${live.runId}.txt`, title: `Live smoke ${live.runId}`,
        text: `Notes of run ${live.runId}. Our household feline, a grey tabby named Quillon, sleeps on the radiator and refuses dry food.` }, config.turnTimeoutMs);
    let docId = added.document.id as string;
    updateState(s => s.created.knowledge.push(docId));
    let found = await live.client.ok('LLMAssistantSearchKnowledge', { query: 'What does the pet cat like to eat?', limit: 10 }, config.turnTimeoutMs);
    live.note(`Search mode ${found.mode}, ${found.results.length} results${found.embedError ? `, embedding error: ${found.embedError}` : ''}.`);
    expect(found.embedError ?? null).toBeNull();
    let mine = found.results.find((r: any) => r.docId === docId);
    expect(mine, 'the document is among the results').toBeTruthy();
    expect(mine.semantic, 'it was matched by meaning').toBeTruthy();
});

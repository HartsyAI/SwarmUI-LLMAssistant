import { test, expect, config } from '../lib/live';
import { updateState } from '../lib/state';

// The target shares no word with the query, so only its meaning can rank it above a document about something else.
const TARGET = 'Quillon, a grey tabby feline, sleeps on warm radiators and refuses kibble.';
const DECOY = 'Quarterly invoices must reach accounting before Friday afternoon.';
const QUERY = 'cat meal preferences';
/** The most results the search gives. */
const LIMIT = 10;

test('knowledge search finds a document by meaning with the embedding model', async ({ live }) => {
    // The wait for the server, then two additions and a search, each allowed the usual time.
    test.setTimeout(config.idleTimeoutMs + 3 * config.turnTimeoutMs + 120_000);
    if (!config.embedModel) {
        live.skip('LLMA_LIVE_EMBED_MODEL is not set.');
    }
    let library = await live.client.ok('LLMAssistantGetKnowledge');
    if (library.settings?.model !== config.embedModel || !library.settings?.providerId) {
        live.skip(`The knowledge library's embedding model is "${library.settings?.model ?? ''}", not "${config.embedModel}". Choose it in Settings > Knowledge (the suite changes no settings).`);
    }
    await live.whenIdle();
    // Two documents and the query are one embedding request each.
    live.reserve(3, 600);
    let add = async (suffix: string, text: string) => {
        updateState(s => {
            s.knowledgeStarted = true;
            s.knowledgePending = true;
        });
        // The run id in the title is how clean-up finds a document whose adding timed out.
        let added = await live.client.ok('LLMAssistantAddKnowledge', { name: `live-smoke-${live.runId}${suffix}.txt`, title: `Live smoke ${live.runId}${suffix}`, text }, config.turnTimeoutMs);
        updateState(s => {
            s.created.knowledge.push(added.document.id);
            s.knowledgePending = false;
        });
        expect(added.document.embedded, `the document was embedded (${added.document.embedError ?? 'no reason given'})`).toBe(true);
        return added.document.id as string;
    };
    let target = await add('', TARGET);
    let decoy = await add(' other', DECOY);
    let found = await live.client.ok('LLMAssistantSearchKnowledge', { query: QUERY, limit: LIMIT }, config.turnTimeoutMs);
    let ids: string[] = found.results.map((r: any) => r.docId);
    live.note(`Search mode ${found.mode}, ${found.results.length} results, target at ${ids.indexOf(target) + 1}, decoy at ${ids.indexOf(decoy) + 1 || 'none'}${found.embedError ? `, embedding error: ${found.embedError}` : ''}.`);
    expect(found.embedError ?? null).toBeNull();
    let mine = found.results.find((r: any) => r.docId === target);
    let others = library.documents?.length ?? 0;
    // Only a full list can have crowded the target out; with room left, its absence is a failure.
    if (!mine && others > 0 && found.results.length >= LIMIT) {
        live.skip(`Your ${others} other documents fill all ${LIMIT} results without the target, so this library cannot show whether search by meaning works.`);
    }
    expect(mine, 'the document is among the results').toBeTruthy();
    expect(mine.keyword, 'no word of the query is in it').toBe(false);
    expect(mine.semantic, 'it was matched by meaning').toBe(true);
    if (ids.includes(decoy)) {
        expect(ids.indexOf(target), 'it ranks above the unrelated document').toBeLessThan(ids.indexOf(decoy));
    }
});

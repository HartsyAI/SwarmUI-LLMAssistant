import type { SwarmClient } from './client';

/** An assistant and the ones it extends, nearest first. The server follows at most five. */
export async function assistantChain(client: SwarmClient, assistantId: string): Promise<any[]> {
    let chain: any[] = [];
    let seen = new Set<string>();
    for (let id = assistantId || 'default'; id && !seen.has(id) && seen.size < 5;) {
        seen.add(id);
        let assistant = (await client.ok('LLMAssistantGetAssistant', { assistantId: id })).assistant;
        if (!assistant) {
            break;
        }
        chain.push(assistant);
        id = assistant.extends ?? '';
    }
    return chain;
}

/** What the assistant, or the nearest one it extends, sets a generation parameter to; undefined when none sets it. */
export async function assistantParameter(client: SwarmClient, assistantId: string, name: string): Promise<unknown> {
    return (await assistantChain(client, assistantId)).map(assistant => assistant.parameters?.[name]).find(value => value !== undefined);
}

import type { SwarmClient } from './client';

/** The path DeleteImage takes, from the address a generation reports. The address may or may not be percent-encoded. */
export function imagePath(url: string): string {
    let decoded = url;
    try {
        decoded = decodeURIComponent(url);
    }
    catch {
        // Not encoded: a name with a % of its own.
    }
    return decoded.replace(/^\/+/, '').replace(/^View\/[^/]+\//, '').replace(/^Output\//, '');
}

/** The images that these generate_image calls, as saved in a chat, report having made. Other calls in the chat are not the run's. */
export function imagesInThread(thread: any, callIds: string[]): string[] {
    let paths: string[] = [];
    for (let message of thread?.messages ?? []) {
        for (let call of message.toolCalls ?? []) {
            if (call.name === 'generate_image' && callIds.includes(call.id) && call.result?.success === true) {
                let urls: unknown[] = call.result.imageUrls ?? [call.result.imageUrl];
                paths.push(...urls.filter((url): url is string => typeof url === 'string' && url.length > 0).map(imagePath));
            }
        }
    }
    return paths;
}

/** The default preset the image tool would apply for this assistant: the user's own tool setting, or one set by the assistant or by an
 * assistant it extends. Any of them adds settings to a call that its approval does not show. */
export async function defaultImagePreset(client: SwarmClient, assistantId: string): Promise<string | null> {
    let own = (await client.ok('LLMAssistantGetToolConfig', { toolId: 'generate_image' })).config?.defaultPreset;
    if (own) {
        return own;
    }
    let seen = new Set<string>();
    // The server follows at most five assistants up the chain.
    for (let id = assistantId || 'default'; id && !seen.has(id) && seen.size < 5;) {
        seen.add(id);
        let assistant = (await client.ok('LLMAssistantGetAssistant', { assistantId: id })).assistant;
        let preset = assistant?.toolConfig?.generate_image?.defaultPreset;
        if (preset) {
            return preset;
        }
        id = assistant?.extends ?? '';
    }
    return null;
}

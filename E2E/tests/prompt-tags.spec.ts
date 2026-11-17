import { test, expect, setScenarios, mockRequests, clearRequests, api } from './fixtures';

// <llmprompt:...> and its companions are expanded while a generation's prompt is prepared. Core's TestPromptFill route runs
// that same preparation without needing an image backend, so it drives these specs.

async function fill(page: Parameters<typeof api>[0], prompt: string): Promise<string> {
    let response = await api(page, 'TestPromptFill', { prompt });
    expect(response.error, JSON.stringify(response)).toBeUndefined();
    return response.result;
}

test('a prompt with no LLM tag is left alone and never calls the model', async ({ llm }) => {
    expect(await fill(llm, 'a plain prompt, no tags')).toBe('a plain prompt, no tags');
    expect(await mockRequests()).toHaveLength(0);
});

test('<llmprompt:...> is replaced by the model reply, and the model is given the tag text', async ({ llm }) => {
    await setScenarios([{ match: 'a cozy cabin', rounds: [{ text: 'a cozy cabin in a snowy forest, golden hour' }] }]);
    let result = await fill(llm, '<llmprompt:a cozy cabin>, (highly detailed:1.2)');
    expect(result).toBe('a cozy cabin in a snowy forest, golden hour, (highly detailed:1.2)');
    let requests = await mockRequests();
    expect(requests).toHaveLength(1);
    let user = requests[0].messages.find((m: any) => m.role === 'user');
    expect(String(user.content)).toContain('a cozy cabin');
    expect(requests[0].messages[0].role).toBe('system');
});

test('<mpprompt:...> (the MagicPrompt alias) works the same way', async ({ llm }) => {
    await setScenarios([{ match: 'old tag style', rounds: [{ text: 'expanded by alias' }] }]);
    expect(await fill(llm, '<mpprompt:old tag style>')).toBe('expanded by alias');
});

test('<llmprompt[instruction]:...> picks that instruction as the system prompt', async ({ llm }) => {
    await setScenarios([]);
    await fill(llm, '<llmprompt:the same words>');
    let defaultSystem = (await mockRequests())[0].messages[0].content;
    await clearRequests();
    await fill(llm, '<llmprompt[caption]:the same words>');
    let captionSystem = (await mockRequests())[0].messages[0].content;
    expect(captionSystem).not.toBe(defaultSystem);
});

test('<llmoriginal> puts the untouched prompt back in', async ({ llm }) => {
    await setScenarios([{ match: 'rough idea', rounds: [{ text: 'polished idea' }] }]);
    let result = await fill(llm, '<llmprompt:rough idea>, <llmoriginal>');
    expect(result).toContain('polished idea');
    // The original text comes back (its own tag shows as the words inside it) and the marker itself is gone.
    expect(result).toContain('rough idea');
    expect(result).not.toContain('<llmoriginal>');
});

test('<llmresponse:N> reuses the Nth reply without another model call', async ({ llm }) => {
    await setScenarios([{ match: 'first idea', rounds: [{ text: 'reply one' }] }]);
    let result = await fill(llm, '<llmprompt:first idea>, again: <llmresponse:0>');
    expect(result).toBe('reply one, again: reply one');
    expect(await mockRequests()).toHaveLength(1);
});

test('when the backend fails the tag falls back to its own text', async ({ llm }) => {
    await setScenarios([{ match: 'will fail', rounds: [{ error: { status: 500, message: 'upstream down' } }] }]);
    expect(await fill(llm, '<llmprompt:will fail>')).toBe('will fail');
});

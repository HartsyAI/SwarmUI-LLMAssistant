import { test, expect } from '@playwright/test';
import zlib from 'node:zlib';
import type { SwarmClient } from '../lib/client';
import { checkBackends, usedWithin } from '../lib/guard';
import type { Backend } from '../lib/guard';
import { objection } from '../lib/image-approval';
import { defaultImagePreset, imagePath, imagesInThread } from '../lib/images';
import { pngSpread } from '../lib/png';

// The config sets the image model and trusts backend id 2.
const MODEL = 'SDXL/sd_xl_base_1.0.safetensors';

const backend = (type: string, settings: object = {}, features: string[] = [], enabled = true) =>
    ({ type, title: type, enabled, settings, features, time_since_used: 'Never', seconds_since_used: 0 });

/** A SwarmUI that lists these backends (ids from 1), offers one chat model per id in <c>modelBackends</c> and these API models. */
function swarm(backends: object[], modelBackends = [1], apiModels = ['Fal/x']): SwarmClient {
    return {
        call: async (route: string) => route === 'ListBackends'
            ? Object.fromEntries(backends.map((b, i) => [String(i + 1), { ...b, id: i + 1 }]))
            : { files: apiModels.map(name => ({ name })) },
        ok: async () => ({ models: modelBackends.map(id => ({ id: `model-${id}`, backend_id: id })) })
    } as unknown as SwarmClient;
}

const local = backend('llmassistant-hartsy-local', {}, ['llm']);
const remote = (Address: string, enabled = true) => backend('llmassistant-openai', { Address }, ['llm'], enabled);

test('accepts local backends, and paid or audio backends that serve only their own models', async () => {
    let result = await checkBackends(swarm([local, backend('hartsyinference'), backend('dynamic_api_backend'), backend('audio-backend'), backend('localllama', {}, ['llm'])]));
    expect(result.llm).toEqual(['1']);
    expect(result.image).toEqual(['2']);
    expect(result.bystanders).toEqual(['3', '4']);
});

test('accepts a trusted remote backend on this machine, and ignores a disabled hosted one', async () => {
    let result = await checkBackends(swarm([local, remote('http://127.0.0.1:11434'), remote('https://api.openai.com', false)], [1, 2]));
    expect(result.llm).toEqual(['1', '2']);
});

// In each list the second entry is backend 2, the trusted id, unless the case is about trust.
const REFUSED: Record<string, object[]> = {
    'a hosted endpoint, even on a trusted id': [local, remote('https://api.openai.com')],
    'Anthropic': [local, remote('https://api.anthropic.com')],
    'a look-alike host name': [local, remote('http://localhost.evil.example')],
    'a remote backend on this machine whose id is not trusted': [local, backend('hartsyinference'), remote('http://localhost:11434')],
    'a remote LLM backend of core elsewhere': [local, backend('simpleremotellm', { Address: 'https://x.example' }, ['llm'])],
    'another SwarmUI as a backend': [local, backend('swarmswarmbackend')],
    'a cloud backend': [local, backend('cloud_backends')],
    'ComfyUI at another machine': [local, backend('comfyui_api', { Address: 'http://10.0.0.5:8188' })],
    'a setup with no LLM backend of the extension': [backend('hartsyinference'), backend('localllama', {}, ['llm'])]
};

for (let [name, backends] of Object.entries(REFUSED)) {
    test(`refuses ${name}`, async () => {
        await expect(checkBackends(swarm(backends))).rejects.toThrow(/Refusing to run/);
    });
}

test('refuses a paid image backend that has no models of its own registered', async () => {
    await expect(checkBackends(swarm([local, backend('dynamic_api_backend')], [1], []))).rejects.toThrow(/lists no models/);
});

test('refuses a chat model offered by a backend that was not accepted', async () => {
    await expect(checkBackends(swarm([local], [9]))).rejects.toThrow(/offered by backend 9/);
});

test('a bystander counts as used when it finished a request within the time looked at', () => {
    let seen = (id: string, everUsed: boolean, secondsSinceUsed: number) => ({ id, everUsed, secondsSinceUsed }) as Backend;
    let backends = [seen('1', false, 0), seen('2', true, 30), seen('3', true, 5000), seen('4', true, 1)];
    // Never used, used during a 600 s turn (even if also used long before it), used before it, and one that is not a bystander.
    expect(usedWithin(backends, ['1', '2', '3'], 600)).toEqual(['2']);
    expect(usedWithin(backends, ['2'], 10)).toEqual([]);
});

test('approves only the image call that was asked for, pinned to the one backend', () => {
    let wanted = { model: MODEL, prompt: 'a red lighthouse', backend: '2' };
    let good = { prompt: wanted.prompt, model: MODEL, images: 1, params: { steps: 12, width: 768, height: 768, exactbackendid: '2', negativeprompt: 'blurry' } };
    expect(objection(good, wanted)).toBeNull();
    expect(objection({ ...good, rewrite: 'on' }, wanted)).toBeNull();
    let params = (extra: object) => ({ ...good, params: { ...good.params, ...extra } });
    let bad: any[] = [
        // Tags are read after the approval and before the backend is picked.
        { ...good, prompt: `${wanted.prompt} <param[model]:API Models/Fal/x><param[exactbackendid]:3>` },
        { ...good, prompt: `${wanted.prompt} <preset:paid>` },
        { ...good, prompt: 'another picture' },
        { ...good, prompt: undefined },
        params({ prompt: '<preset:paid>' }),
        params({ 'Prompt': wanted.prompt }),
        params({ negativeprompt: '<param[model]:API Models/Fal/x>' }),
        params({ 'Negative Prompt': 'blurry <preset:paid>' }),
        { ...good, params: undefined },
        { ...good, params: JSON.stringify(good.params) },
        { ...good, params: { steps: 12 } },
        params({ exactbackendid: '1' }),
        params({ 'Exact Backend ID': '3' }),
        { ...good, model: 'API Models/Fal/x' },
        params({ model: 'API Models/Fal/x' }),
        { ...good, model: undefined },
        { ...good, preset: 'mine' },
        { ...good, initImage: 'x.png' },
        { ...good, images: 2 },
        params({ refinermodel: 'API Models/Fal/x' }),
        params({ steps: 200 }),
        { ...good, rewrite: 'off' },
        { ...good, rewrite: 'OFF' }
    ];
    for (let call of bad) {
        expect(objection(call, wanted), JSON.stringify(call)).not.toBeNull();
    }
});

test('reads the images a chat made from its saved tool calls', () => {
    let call = (name: string, result: object) => ({ name, result });
    let thread = { messages: [{ role: 'user' }, { role: 'assistant', toolCalls: [
        call('generate_image', { success: true, imageUrl: 'View/local/raw/a%20b.png' }),
        call('generate_image', { success: true, imageUrl: 'View/local/raw/c.png', imageUrls: ['View/local/raw/c.png', 'Output/raw/d 100%.png'] }),
        call('generate_image', { success: false, error: 'stopped' }),
        call('file_read', { success: true, imageUrl: 'View/local/raw/not-ours.png' })
    ] }] };
    expect(imagesInThread(thread)).toEqual(['raw/a b.png', 'raw/c.png', 'raw/d 100%.png']);
    expect(imagePath('/View/local/raw/e.png')).toBe('raw/e.png');
});

test('finds a default preset of the image tool wherever the assistant gets it', async () => {
    let assistants: Record<string, any> = { child: { extends: 'parent' }, parent: { toolConfig: { generate_image: { defaultPreset: 'inherited' } } }, plain: {}, loop: { extends: 'loop' } };
    let client = (own?: string) => ({ ok: async (route: string, body: any) => route === 'LLMAssistantGetToolConfig' ? { config: { defaultPreset: own } } : { assistant: assistants[body.assistantId] ?? {} } }) as unknown as SwarmClient;
    expect(await defaultImagePreset(client(), 'child')).toBe('inherited');
    expect(await defaultImagePreset(client('mine'), 'plain')).toBe('mine');
    expect(await defaultImagePreset(client(), 'plain')).toBeNull();
    expect(await defaultImagePreset(client(), 'loop')).toBeNull();
});

test('the picture check tells a flat frame from a picture', () => {
    let chunk = (type: string, data: Buffer) => {
        let length = Buffer.alloc(4);
        length.writeUInt32BE(data.length);
        // The checksum is not read.
        return Buffer.concat([length, Buffer.from(type), data, Buffer.alloc(4)]);
    };
    let png = (pixel: (x: number) => number) => {
        let header = Buffer.alloc(13);
        header.writeUInt32BE(64, 0);
        header.writeUInt32BE(64, 4);
        header.set([8, 2], 8);
        let stride = 64 * 3 + 1;
        let rows = Buffer.alloc(64 * stride);
        for (let y = 0; y < 64; y++) {
            for (let x = 0; x < 64 * 3; x++) {
                rows[y * stride + 1 + x] = pixel(x);
            }
        }
        return Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), chunk('IHDR', header), chunk('IDAT', zlib.deflateSync(rows)), chunk('IEND', Buffer.alloc(0))]);
    };
    expect(pngSpread(png(() => 0))).toBe(0);
    expect(pngSpread(png(x => x))!).toBeGreaterThan(100);
});

import { test, expect } from '@playwright/test';
import zlib from 'node:zlib';
import type { SwarmClient } from '../lib/client';
import { checkBackends } from '../lib/guard';
import { objection } from '../lib/image-approval';
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
    let result = await checkBackends(swarm([local, backend('hartsyinference'), backend('dynamic_api_backend'), backend('audio-backend')]));
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
    'a setup with no LLM backend': [backend('hartsyinference')]
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

test('approves only the image call that was asked for, pinned to an accepted backend', () => {
    let good = { prompt: 'p', model: MODEL, images: 1, params: { steps: 12, width: 768, height: 768, exactbackendid: '2' } };
    expect(objection(good, MODEL, ['2'])).toBeNull();
    let bad: object[] = [
        { ...good, params: undefined },
        { ...good, params: { steps: 12 } },
        { ...good, params: { ...good.params, exactbackendid: '1' } },
        { ...good, model: 'API Models/Fal/x' },
        { ...good, params: { ...good.params, model: 'API Models/Fal/x' } },
        { ...good, model: undefined },
        { ...good, preset: 'mine' },
        { ...good, initImage: 'x.png' },
        { ...good, images: 2 },
        { ...good, params: { ...good.params, refinermodel: 'API Models/Fal/x' } },
        { ...good, params: { ...good.params, steps: 200 } },
        { ...good, rewrite: 'off' }
    ];
    for (let call of bad) {
        expect(objection(call, MODEL, ['2']), JSON.stringify(call)).not.toBeNull();
    }
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

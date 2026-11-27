import type { SwarmClient } from './client';
import { config } from './config';

/** Refuses to run when a backend that may cost money, or that cannot be shown to be local, could take one of the suite's requests.
 * It reads SwarmUI's backend list (secret values come masked) and changes nothing. Anything it does not recognise counts against it. */
export class Refusal extends Error {
    constructor(reason: string) {
        super(`Refusing to run: ${reason}`);
    }
}

export type Backend = {
    id: string;
    type: string;
    title: string;
    enabled: boolean;
    features: string[];
    settings: Record<string, any>;
    currentModel: string | null;
    secondsSinceUsed: number;
    everUsed: boolean;
};

export type GuardResult = {
    /** Backends that may answer the suite's LLM requests. */
    llm: string[];
    /** Local backends that may make the suite's image. */
    image: string[];
    /** Backends that could cost money but cannot serve the configured local model; a run checks that they stayed unused. */
    bystanders: string[];
    notes: string[];
};

const LOCAL_LLM = 'llmassistant-hartsy-local';
const REMOTE_LLM = 'llmassistant-openai';
/** Run their model in or beside SwarmUI on its own machine. */
const LOCAL_IMAGE = ['hartsyinference', 'comfyui_selfstart', 'auto_webui_selfstart'];
/** Reached by address: local only when the address is this machine and the user vouches for what listens there. */
const BY_ADDRESS_IMAGE = ['comfyui_api', 'auto_webui_api'];
/** Serve only the models they list under their own folder, so SwarmUI never sends them another model's request. */
const OWN_MODELS_ONLY: Record<string, string> = { 'dynamic_api_backend': 'API Models/', 'audio-backend': 'Audio Models/' };

export async function listBackends(client: SwarmClient): Promise<Backend[]> {
    let reply = await client.call('ListBackends', { nonreal: true, full_data: true });
    if (reply.error) {
        throw new Refusal(`SwarmUI did not give its backend list (${reply.error}), so no backend can be checked.`);
    }
    return Object.values<any>(reply).map(b => ({
        id: String(b.id),
        type: String(b.type),
        title: String(b.title ?? ''),
        enabled: b.enabled !== false,
        features: b.features ?? [],
        settings: b.settings ?? {},
        currentModel: b.current_model || null,
        secondsSinceUsed: b.seconds_since_used ?? 0,
        everUsed: b.time_since_used !== 'Never'
    }));
}

function isThisMachine(address: unknown): boolean {
    try {
        let host = new URL(String(address)).hostname.toLowerCase();
        return host === 'localhost' || host === '127.0.0.1' || host === '[::1]';
    }
    catch {
        return false;
    }
}

function vouchedLocal(backend: Backend): boolean {
    return config.trustedBackends.includes(backend.id) && isThisMachine(backend.settings.Address);
}

/** Every installed model name SwarmUI lists for a folder, for example "API Models". */
export async function listImageModels(client: SwarmClient, folder = ''): Promise<string[]> {
    let reply = await client.call('ListModels', { path: folder, depth: 8, subtype: 'Stable-Diffusion' });
    if (reply.error) {
        throw new Error(`ListModels failed: ${reply.error}`);
    }
    let prefix = folder ? `${folder}/` : '';
    return (reply.files ?? []).map((file: any) => {
        let name = String(file.name);
        return name.startsWith(prefix) ? name : `${prefix}${name}`;
    });
}

export async function checkBackends(client: SwarmClient): Promise<GuardResult> {
    let backends = (await listBackends(client)).filter(b => b.enabled);
    let result: GuardResult = { llm: [], image: [], bystanders: [], notes: [] };
    for (let backend of backends) {
        let name = `backend ${backend.id} "${backend.title}" (${backend.type})`;
        if (backend.type === LOCAL_LLM) {
            result.llm.push(backend.id);
        }
        else if (backend.type === REMOTE_LLM || backend.features.includes('llm')) {
            if (!vouchedLocal(backend)) {
                throw new Refusal(`${name} could take LLM requests and is not shown to be local. A remote LLM backend is accepted only when its address is this machine AND its id is in LLMA_LIVE_TRUSTED_BACKENDS (your word that it does not forward to a paid service).`);
            }
            result.llm.push(backend.id);
            result.notes.push(`${name} accepted on LLMA_LIVE_TRUSTED_BACKENDS.`);
        }
        else if (!config.imageModel) {
            // The suite makes no image request, and this backend takes no LLM request.
            continue;
        }
        else if (LOCAL_IMAGE.includes(backend.type) || (BY_ADDRESS_IMAGE.includes(backend.type) && vouchedLocal(backend))) {
            result.image.push(backend.id);
        }
        else if (backend.type in OWN_MODELS_ONLY) {
            let prefix = OWN_MODELS_ONLY[backend.type];
            if (config.imageModel.toLowerCase().startsWith(prefix.toLowerCase())) {
                throw new Refusal(`LLMA_LIVE_IMAGE_MODEL is one of the models of ${name}.`);
            }
            // With none of its models registered, SwarmUI's model filter would not keep a local model's request away from it.
            if (backend.type === 'dynamic_api_backend' && (await listImageModels(client, prefix.replace(/\/$/, ''))).length === 0) {
                throw new Refusal(`${name} lists no models of its own, so SwarmUI could send it the image request.`);
            }
            result.bystanders.push(backend.id);
            result.notes.push(`${name} is enabled but serves only "${prefix}..." models, so it cannot take the image request.`);
        }
        else {
            throw new Refusal(`${name} is not known to be local and could take the image request. Unset LLMA_LIVE_IMAGE_MODEL to run without images.`);
        }
    }
    if (result.llm.length === 0) {
        throw new Refusal('no enabled LLM backend was found.');
    }
    // Whatever offers a chat model must be one of the accepted backends; this also catches a provider that no backend entry explains.
    let offered = await client.ok('LLMAssistantGetModels');
    for (let model of offered.models ?? []) {
        if (!result.llm.includes(String(model.backend_id))) {
            throw new Refusal(`the model "${model.id}" is offered by backend ${model.backend_id}, which was not accepted as a local LLM backend.`);
        }
    }
    return result;
}

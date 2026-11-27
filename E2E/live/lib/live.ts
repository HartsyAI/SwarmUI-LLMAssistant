import { test as base, expect } from '@playwright/test';
import type { TestInfo } from '@playwright/test';
import { SwarmClient } from './client';
import { config, notConfigured } from './config';
import { checkBackends } from './guard';
import type { GuardResult } from './guard';
import { busyReason, chatModelInfo } from './server';
import { hold, readState, updateState } from './state';
import type { Hold } from './state';

export { expect, config };

/** A rough token count (four characters a token), for holding room under the cap before a request. */
export const roughTokens = (text: string) => Math.ceil(text.length / 4);

/** Room for what SwarmUI adds to a message: instructions, and tool definitions when tools are on. */
export const PROMPT_ALLOWANCE = { plain: 800, tools: 5000 };

/** The most model rounds one turn may take before the suite stops it; a turn with tools holds this many requests. */
export const MAX_ROUNDS = 4;

export type Approval = { approvalId: string; id: string; name: string; arguments: any; risk: string };

export type TurnResult = {
    frames: any[];
    /** The reply as streamed, reasoning included. */
    text: string;
    /** The reply without its reasoning. */
    visible: string;
    chunks: number;
    done: any | null;
    errors: string[];
    toolCalls: any[];
    toolResults: any[];
    approvals: (Approval & { decision: string })[];
    seconds: number;
    stoppedForRounds: boolean;
};

export type TurnOptions = {
    maxTokens?: number;
    forceToolId?: string;
    /** Answers a call waiting for approval. A call it does not allow is denied. */
    approve?: (approval: Approval) => 'allow' | 'deny';
    onChunk?: (count: number) => Promise<void> | void;
    timeoutMs?: number;
    /** Estimated tokens of the whole turn, for the cap. */
    estimate?: number;
    /** Model requests to hold under the cap (1 when left out). */
    requests?: number;
    /** A summary the turn may write first: it is a request of its own, and its tokens are not in the turn's usage. */
    summary?: { tokens: number };
};

export const stripThinking = (text: string) => text.replace(/<think>[\s\S]*?(<\/think>|$)/g, '').trim();

export class Live {
    constructor(readonly client: SwarmClient, readonly guard: GuardResult, private readonly info: TestInfo) { }

    get runId(): string {
        return readState()!.runId;
    }

    /** A line for the report. */
    note(text: string) {
        this.info.annotations.push({ type: 'note', description: text });
    }

    skip(reason: string): never {
        base.skip(true, reason);
        throw new Error(reason);
    }

    /** Room under the run's caps; skips the scenario when there is none. */
    reserve(requests: number, tokens: number, images = 0): Hold {
        let held = hold(requests, tokens, images);
        if (typeof held === 'string') {
            this.skip(held);
        }
        return held;
    }

    /** Waits until the server can take a GPU scenario, and checks the backends again; skips when it stays busy. */
    async whenIdle(kind: 'llm' | 'image' = 'llm') {
        let deadline = Date.now() + config.idleTimeoutMs;
        let state = await busyReason(this.client, kind);
        while (state.reason && Date.now() < deadline) {
            await new Promise(done => setTimeout(done, 15_000));
            state = await busyReason(this.client, kind);
        }
        if (state.reason) {
            this.skip(`The server was not ready after ${config.idleTimeoutMs / 1000}s: ${state.reason}.`);
        }
        this.note(`Before the ${kind} work: ${state.seen}.`);
        await checkBackends(this.client);
        updateState(s => s.usedLlm = true);
    }

    /** A chat of the run's own: approval mode Ask, never remembered, with a title so no title is asked of the model. */
    async createChat(label: string, options: { tools: boolean; autoEnhance?: 'on' | 'off' }): Promise<string> {
        let made = await this.client.ok('LLMAssistantCreateThread', { rememberChat: false, ...(options.autoEnhance ? { autoEnhance: options.autoEnhance } : {}) });
        let threadId = made.thread.id as string;
        updateState(s => s.created.threads.push(threadId));
        await this.prepareChat(threadId, label, options.tools);
        return threadId;
    }

    async prepareChat(threadId: string, label: string, tools: boolean) {
        await this.client.ok('LLMAssistantRenameThread', { threadId, title: `Live smoke ${this.runId}: ${label}` });
        await this.client.ok('LLMAssistantSetThreadToolsEnabled', { threadId, enabled: tools });
        await this.client.ok('LLMAssistantSetThreadApprovalMode', { threadId, mode: 'ask' });
        await this.client.ok('LLMAssistantSetThreadMemory', { threadId, enabled: false });
    }

    /** The tools a turn in this chat would offer, from a dry run (no model is called). */
    async offeredTools(threadId: string): Promise<string[]> {
        let dry = await this.client.ok('LLMAssistantRunTurn', { threadId, message: 'Which tools are offered?', model: config.chatModel, dryRun: true }, 120_000);
        return dry.tools ?? [];
    }

    async requireTool(threadId: string, tool: string) {
        let offered = await this.offeredTools(threadId);
        if (!offered.includes(tool)) {
            this.skip(`The tool "${tool}" is not offered to your active assistant (switch it on in Settings > Tools and in the assistant). The suite changes no settings.`);
        }
    }

    async stop(threadId: string): Promise<any> {
        return await this.client.ok('LLMAssistantStopGeneration', { threadId });
    }

    /** One reply over the chat WebSocket, the way the page sends it. Every approval the caller does not allow is denied. */
    async chatTurn(threadId: string, message: string, options: TurnOptions = {}): Promise<TurnResult> {
        let maxTokens = options.maxTokens ?? config.maxReplyTokens;
        let summary = options.summary ? { requests: 1, tokens: options.summary.tokens } : { requests: 0, tokens: 0 };
        let held = this.reserve((options.requests ?? 1) + summary.requests, (options.estimate ?? roughTokens(message) + PROMPT_ALLOWANCE.plain + maxTokens) + summary.tokens);
        let result: TurnResult = { frames: [], text: '', visible: '', chunks: 0, done: null, errors: [], toolCalls: [], toolResults: [], approvals: [], seconds: 0, stoppedForRounds: false };
        let started = Date.now();
        try {
            await this.client.stream('LLMAssistantSendMessageWS',
                { threadId, message, model: config.chatModel, approvalMode: 'ask', maxTokens, enableThinking: false, ...(options.forceToolId ? { forceToolId: options.forceToolId } : {}) },
                async frame => {
                    result.frames.push(frame);
                    if (typeof frame.chunk === 'string') {
                        result.text += frame.chunk;
                        result.chunks++;
                        await options.onChunk?.(result.chunks);
                    }
                    if (frame.tool_call) {
                        result.toolCalls.push(frame.tool_call);
                    }
                    if (frame.tool_result) {
                        result.toolResults.push(frame.tool_result);
                    }
                    if (frame.tool_approval) {
                        let decision = options.approve?.(frame.tool_approval) ?? 'deny';
                        result.approvals.push({ ...frame.tool_approval, decision });
                        await this.client.ok('LLMAssistantResolveToolApproval', { approvalId: frame.tool_approval.approvalId, decision });
                    }
                    if (frame.iteration >= MAX_ROUNDS && !result.stoppedForRounds) {
                        result.stoppedForRounds = true;
                        await this.stop(threadId);
                    }
                    if (frame.done) {
                        result.done = frame;
                    }
                    if (frame.error || frame.failure) {
                        result.errors.push(String(frame.error ?? frame.failure));
                    }
                }, options.timeoutMs ?? config.turnTimeoutMs);
        }
        catch (error) {
            await this.stop(threadId).catch(() => { });
            throw error;
        }
        finally {
            result.seconds = (Date.now() - started) / 1000;
            result.visible = stripThinking(result.text);
            let usage = result.done?.usage;
            held.settle(usage ? usage.rounds + summary.requests : undefined, usage ? usage.promptTokens + usage.completionTokens + summary.tokens : undefined);
        }
        return result;
    }

    /** The newest saved reply of a chat. */
    async savedReply(threadId: string): Promise<any> {
        let reply = await this.client.ok('LLMAssistantGetThread', { threadId });
        return [...(reply.thread.messages ?? [])].reverse().find((m: any) => m.role === 'assistant');
    }

    /** Checks the parts every finished reply must have: an end, no error, and token counts. */
    expectFinished(result: TurnResult) {
        expect(result.errors, 'error frames').toEqual([]);
        expect(result.done, 'the done frame').not.toBeNull();
        expect.soft(result.done?.usage?.promptTokens ?? 0, 'usage: prompt tokens').toBeGreaterThan(0);
        expect.soft(result.done?.usage?.completionTokens ?? 0, 'usage: completion tokens').toBeGreaterThan(0);
        let usage = result.done?.usage;
        this.note(`${result.seconds.toFixed(1)}s, ${result.chunks} chunks, usage ${usage ? JSON.stringify(usage) : 'not reported'}.`);
    }

    async modelInfo(): Promise<any> {
        return await chatModelInfo(this.client);
    }
}

export const test = base.extend<{ live: Live }>({
    live: async ({ }, use, testInfo) => {
        let missing = notConfigured();
        base.skip(missing !== null, missing ?? '');
        let client = new SwarmClient(config.baseUrl);
        let live = new Live(client, await checkBackends(client), testInfo);
        let before = readState()!;
        let mine = new Set(Object.values(before.created).flat());
        await use(live);
        // A scenario's chats are stopped as soon as it ends, so a failed one leaves no reply running for the next.
        for (let threadId of readState()!.created.threads.filter(id => !mine.has(id))) {
            await client.call('LLMAssistantStopGeneration', { threadId }).catch(() => { });
        }
    }
});

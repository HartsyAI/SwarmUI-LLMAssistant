import { test, expect } from '@playwright/test';
import type { FullConfig, Suite, TestCase, TestResult } from '@playwright/test/reporter';
import fs from 'node:fs';
import path from 'node:path';
import type { SwarmClient } from '../lib/client';
import { RUN_DIR } from '../lib/config';
import { assistantParameter } from '../lib/assistants';
import { newState, readState, setAsideUnreadable, writeState } from '../lib/state';
import LiveReport from '../reporter';

test('a record cut off while being written is set aside, and a whole one is left', () => {
    writeState({ ...newState(), runId: 'whole' });
    expect(setAsideUnreadable()).toBeNull();
    expect(readState()!.runId).toBe('whole');
    fs.writeFileSync(path.join(RUN_DIR, 'state.json'), '{"runId": "cut');
    let kept = setAsideUnreadable()!;
    expect(fs.readFileSync(kept, 'utf8')).toContain('cut');
    expect(readState()).toBeNull();
    fs.rmSync(kept);
});

test('an interrupted run does not read as clean', async () => {
    writeState(newState());
    let report = new LiveReport();
    let scenario = (title: string) => ({ title, parent: { title: 'file' }, location: { file: '10-reply.spec.ts' }, annotations: [] }) as unknown as TestCase;
    report.onBegin({} as FullConfig, { allTests: () => [scenario('a'), scenario('b'), scenario('c')] } as unknown as Suite);
    report.onTestEnd(scenario('a'), { status: 'passed', duration: 1, errors: [], annotations: [] } as unknown as TestResult);
    report.onTestEnd(scenario('b'), { status: 'interrupted', duration: 1, errors: [], annotations: [] } as unknown as TestResult);
    report.onEnd({ status: 'interrupted' } as any);
    let print = console.log;
    console.log = () => { };
    try {
        await report.onExit();
    }
    finally {
        console.log = print;
    }
    let text = fs.readFileSync(path.join(RUN_DIR, 'report.md'), 'utf8');
    expect(text).toContain('1 passed, 0 failed, 0 skipped, 1 interrupted, 1 not run');
    expect(text).toContain('not a complete result');
});

test('reads a generation parameter from the assistant or the nearest one it extends', async () => {
    let assistants: Record<string, any> = { child: { extends: 'parent' }, parent: { parameters: { autoCompact: false } }, own: { extends: 'parent', parameters: { autoCompact: true } } };
    let client = { ok: async (_route: string, body: any) => ({ assistant: assistants[body.assistantId] }) } as unknown as SwarmClient;
    expect(await assistantParameter(client, 'child', 'autoCompact')).toBe(false);
    expect(await assistantParameter(client, 'own', 'autoCompact')).toBe(true);
    expect(await assistantParameter(client, 'missing', 'autoCompact')).toBeUndefined();
});

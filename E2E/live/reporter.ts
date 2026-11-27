import fs from 'node:fs';
import path from 'node:path';
import type { Reporter, TestCase, TestResult } from '@playwright/test/reporter';
import { config, RUN_DIR } from './lib/config';
import { readState } from './lib/state';

type Row = { title: string; status: string; seconds: number; notes: string[] };

/** Writes E2E/.run/live/report.md and prints it: one line per scenario, what each measured, what it cost and what was cleaned up. */
export default class LiveReport implements Reporter {
    private rows: Row[] = [];
    private problems: string[] = [];

    onTestEnd(test: TestCase, result: TestResult) {
        let annotations = result.annotations ?? test.annotations;
        let notes = annotations.filter(a => a.type === 'note' || a.type === 'skip').map(a => a.description ?? '');
        for (let error of result.errors) {
            let lines = (error.message ?? '').replace(/\u001b\[[0-9;]*m/g, '').split('\n').map(line => line.trim()).filter(line => line.length > 0);
            notes.push(`FAILED: ${lines.slice(0, 4).join(' | ').slice(0, 600)}`);
        }
        this.rows.push({ title: `${test.parent.title ? `${path.basename(test.location.file, '.spec.ts')}: ` : ''}${test.title}`, status: result.status, seconds: result.duration / 1000, notes });
    }

    onError(error: { message?: string }) {
        this.problems.push((error.message ?? 'unknown error').replace(/\u001b\[[0-9;]*m/g, '').split('\n')[0]);
    }

    async onExit() {
        // Nothing ran (`--list`, or the suite refused to start): leave the last report alone and say only why.
        if (this.rows.length === 0 && this.problems.length === 0) {
            return;
        }
        let state = readState();
        let count = (status: string) => this.rows.filter(r => r.status === status).length;
        let lines = [
            '# Live smoke report',
            '',
            `${new Date().toISOString()}: ${count('passed')} passed, ${count('failed') + count('timedOut')} failed, ${count('skipped')} skipped.`,
            `Chat model: ${config.chatModel || '(not set)'}. Image model: ${config.imageModel || '(not set)'}. Embedding model: ${config.embedModel || '(not set)'}.`,
        ];
        if (state) {
            lines.push(`Used ${state.requests} of ${config.maxRequests} model requests, about ${state.tokens} of ${config.maxTokens} tokens, ${state.images} of ${config.maxImages} images.`);
        }
        lines.push('', ...this.problems.map(p => `- ${p}`), ...(state?.notes ?? []).map(note => `- ${note}`), '');
        for (let row of this.rows) {
            lines.push(`## ${row.status.toUpperCase()} ${row.title} (${row.seconds.toFixed(0)}s)`, ...row.notes.map(note => `- ${note}`), '');
        }
        let report = lines.join('\n');
        fs.mkdirSync(RUN_DIR, { recursive: true });
        fs.writeFileSync(path.join(RUN_DIR, 'report.md'), report);
        console.log(`\n${report}\nReport saved to ${path.join(RUN_DIR, 'report.md')}`);
    }
}

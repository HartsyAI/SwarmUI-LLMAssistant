import { startStack } from './stack.mjs';

/** Starts MockLLM and the throwaway SwarmUI before any spec runs. */
export default async function globalSetup() {
    if (process.env.E2E_REUSE_STACK) {
        // A stack started by hand with `npm run stack`; the specs just use it.
        return;
    }
    let stack = await startStack();
    (globalThis as any).__e2eStack = stack;
}

/** Stops what global-setup started. */
export default async function globalTeardown() {
    let stack = (globalThis as any).__e2eStack;
    if (stack) {
        stack.stop();
    }
}

/** The only way the suite reaches anything: SwarmUI's own HTTP and WebSocket API. No engine or model server is ever called directly.
 * The session id is held here and never put in an error message, a log or the report. */
export class SwarmClient {
    private sessionId = '';

    constructor(readonly baseUrl: string) { }

    private async post(route: string, body: object, timeoutMs: number): Promise<any> {
        let response = await fetch(`${this.baseUrl}/API/${route}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body),
            signal: AbortSignal.timeout(timeoutMs)
        });
        if (!response.ok) {
            throw new Error(`${route}: HTTP ${response.status}`);
        }
        return await response.json();
    }

    private async session(): Promise<string> {
        if (!this.sessionId) {
            let made = await this.post('GetNewSession', {}, 30_000);
            if (!made.session_id) {
                throw new Error(`GetNewSession gave no session (${made.error ?? 'no reason given'}). A SwarmUI that requires a login is not supported.`);
            }
            this.sessionId = made.session_id;
        }
        return this.sessionId;
    }

    /** Calls a route and returns its reply as it is. */
    async call(route: string, body: object = {}, timeoutMs = 60_000): Promise<any> {
        let reply = await this.post(route, { session_id: await this.session(), ...body }, timeoutMs);
        if (reply?.error_id === 'invalid_session_id') {
            this.sessionId = '';
            reply = await this.post(route, { session_id: await this.session(), ...body }, timeoutMs);
        }
        return reply;
    }

    /** Calls a route and throws when it reports a failure. */
    async ok(route: string, body: object = {}, timeoutMs = 60_000): Promise<any> {
        let reply = await this.call(route, body, timeoutMs);
        if (reply?.error || reply?.success === false) {
            throw new Error(`${route} failed: ${reply.error ?? JSON.stringify(reply).slice(0, 300)}`);
        }
        return reply;
    }

    /** Reads a file SwarmUI serves, for example a generated image. */
    async getBytes(relativeUrl: string, timeoutMs = 60_000): Promise<Buffer> {
        let url = new URL(relativeUrl, `${this.baseUrl}/`);
        if (url.origin !== new URL(this.baseUrl).origin) {
            throw new Error('Refusing to fetch from a host other than SwarmUI.');
        }
        let response = await fetch(url, { signal: AbortSignal.timeout(timeoutMs) });
        if (!response.ok) {
            throw new Error(`Reading a served file: HTTP ${response.status}`);
        }
        return Buffer.from(await response.arrayBuffer());
    }

    /** Opens a WebSocket route, sends the request and hands every frame to <c>onFrame</c> until the server closes the socket. */
    async stream(route: string, body: object, onFrame: (frame: any) => Promise<void> | void, timeoutMs: number): Promise<void> {
        let sessionId = await this.session();
        let socket = new WebSocket(`${this.baseUrl.replace(/^http/, 'ws')}/API/${route}`);
        await new Promise<void>((resolve, reject) => {
            let ended = false;
            // Frames are handled one after another, so a handler that calls a route cannot be overtaken by the next frame.
            let handled: Promise<void> = Promise.resolve();
            // Fails at once. SwarmUI reads nothing from the socket while a route runs, so the close asked for here may not be answered
            // until the work is done, and the caller has to be able to stop it before then.
            let fail = (error: Error) => {
                if (!ended) {
                    ended = true;
                    clearTimeout(timer);
                    socket.close();
                    reject(error);
                }
            };
            let timer = setTimeout(() => fail(new Error(`${route}: no end of the reply after ${Math.round(timeoutMs / 1000)}s`)), timeoutMs);
            socket.addEventListener('open', () => socket.send(JSON.stringify({ session_id: sessionId, ...body })));
            socket.addEventListener('message', event => {
                handled = handled.then(async () => {
                    if (!ended) {
                        await onFrame(JSON.parse(String(event.data)));
                    }
                }).catch(fail);
            });
            socket.addEventListener('error', () => {
                handled = handled.then(() => fail(new Error(`${route}: the WebSocket failed`)));
            });
            socket.addEventListener('close', () => {
                handled.then(() => {
                    if (!ended) {
                        ended = true;
                        clearTimeout(timer);
                        resolve();
                    }
                });
            });
        });
    }
}

import { test, expect } from '@playwright/test';
import { createHash } from 'node:crypto';
import http from 'node:http';
import type { AddressInfo, Socket } from 'node:net';
import { SwarmClient } from '../lib/client';

/** A server that opens the WebSocket, may send frames, and then never answers again: not even a close. SwarmUI is like this while a
 * route runs, since it reads nothing from the socket until the route is done. */
async function neverEnding(frames: object[] = []) {
    let sockets: Socket[] = [];
    let server = http.createServer((_request, response) => response.end(JSON.stringify({ session_id: 'test' })));
    server.on('upgrade', (request, socket: Socket) => {
        sockets.push(socket);
        let accept = createHash('sha1').update(`${request.headers['sec-websocket-key']}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`).digest('base64');
        socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
        for (let frame of frames) {
            // A short unmasked text frame.
            let text = Buffer.from(JSON.stringify(frame));
            socket.write(Buffer.concat([Buffer.from([0x81, text.length]), text]));
        }
    });
    await new Promise<void>(done => server.listen(0, '127.0.0.1', done));
    return {
        client: new SwarmClient(`http://127.0.0.1:${(server.address() as AddressInfo).port}`),
        close: () => {
            sockets.forEach(socket => socket.destroy());
            server.close();
        }
    };
}

test('a route that does not answer in time is reported as given up', async () => {
    let server = http.createServer((request, response) => {
        if (request.url === '/API/GetNewSession') {
            response.end(JSON.stringify({ session_id: 'test' }));
        }
    });
    await new Promise<void>(done => server.listen(0, '127.0.0.1', done));
    let client = new SwarmClient(`http://127.0.0.1:${(server.address() as AddressInfo).port}`);
    let givenUp = 0;
    client.onGivenUp = () => givenUp++;
    try {
        await expect(client.call('Slow', {}, 300)).rejects.toThrow();
        expect(givenUp).toBe(1);
    }
    finally {
        server.closeAllConnections();
        server.close();
    }
});

test('a reply that never ends fails at its time limit', async () => {
    test.setTimeout(10_000);
    let server = await neverEnding();
    try {
        await expect(server.client.stream('Route', {}, () => { }, 500)).rejects.toThrow(/no end of the reply/);
    }
    finally {
        server.close();
    }
});

test('a failing frame handler fails the call at once', async () => {
    test.setTimeout(10_000);
    let server = await neverEnding([{ chunk: 'a' }]);
    try {
        await expect(server.client.stream('Route', {}, () => {
            throw new Error('handler failed');
        }, 60_000)).rejects.toThrow('handler failed');
    }
    finally {
        server.close();
    }
});

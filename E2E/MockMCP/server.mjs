// A scripted MCP server for the e2e specs: no dependencies, runs on plain node.
//
//   node server.mjs                          stdio: one JSON message per line on stdin and stdout
//   node server.mjs --http 17803             Streamable HTTP on /mcp (needs "Authorization: Bearer mock-token")
//
// Tools:
//   echo      { text }            read-only; answers "echo: <text>"
//   write_note { text }           changes something (no read-only hint); answers "noted: <text>"
//   lookup    { query }           open-world (reaches outside systems), not read-only
//   slow      { ms }              read-only; answers after <ms> milliseconds
//   fail      {}                  answers with isError: true
//   crash     {}                  the process exits at once (stdio) or the connection is cut (http)
//   add_tool  {}                  adds a tool named "extra" and says the tool list changed
//
// Every tools/call is appended as one JSON line to the file named by MOCK_MCP_LOG, when it is set, so a spec can see what was called.
import fs from 'node:fs';
import http from 'node:http';
import readline from 'node:readline';

const LOG = process.env.MOCK_MCP_LOG;
const SERVER_NAME = process.env.MOCK_MCP_NAME || 'mock-mcp';
let extra = false;

const tools = () => {
    let list = [
        { name: 'echo', description: 'Echo the text back.', inputSchema: { type: 'object', properties: { text: { type: 'string' } }, required: ['text'] }, annotations: { readOnlyHint: true } },
        { name: 'write_note', description: 'Save a note.', inputSchema: { type: 'object', properties: { text: { type: 'string' } }, required: ['text'] } },
        { name: 'lookup', description: 'Look something up on the web.', inputSchema: { type: 'object', properties: { query: { type: 'string' } }, required: ['query'] }, annotations: { openWorldHint: true } },
        { name: 'slow', description: 'Answer after a delay.', inputSchema: { type: 'object', properties: { ms: { type: 'number' } } }, annotations: { readOnlyHint: true } },
        { name: 'fail', description: 'Always fails.', inputSchema: { type: 'object', properties: {} } },
        { name: 'crash', description: 'Takes the server down.', inputSchema: { type: 'object', properties: {} } },
        { name: 'add_tool', description: 'Adds another tool.', inputSchema: { type: 'object', properties: {} }, annotations: { readOnlyHint: true } },
    ];
    if (extra) {
        list.push({ name: 'extra', description: 'Added later.', inputSchema: { type: 'object', properties: {} }, annotations: { readOnlyHint: true } });
    }
    return list;
};

const text = (value, isError = false) => ({ content: [{ type: 'text', text: value }], isError });

/** Handles one JSON-RPC message; returns the response, or null for a notification. `notify` sends a server notification. */
async function handle(message, notify, onCrash) {
    let { id, method, params } = message;
    if (id === undefined) {
        return null;
    }
    let reply = result => ({ jsonrpc: '2.0', id, result });
    switch (method) {
        case 'initialize':
            return reply({ protocolVersion: params?.protocolVersion || '2025-06-18', capabilities: { tools: { listChanged: true } }, serverInfo: { name: SERVER_NAME, version: '1.0.0' } });
        case 'ping':
            return reply({});
        case 'tools/list':
            return reply({ tools: tools() });
        case 'tools/call': {
            let { name, arguments: args = {} } = params || {};
            if (LOG) {
                fs.appendFileSync(LOG, JSON.stringify({ name, arguments: args }) + '\n');
            }
            switch (name) {
                case 'echo': return reply(text(`echo: ${args.text}`));
                case 'write_note': return reply(text(`noted: ${args.text}`));
                case 'lookup': return reply(text(`found: ${args.query}`));
                case 'slow':
                    await new Promise(done => setTimeout(done, Number(args.ms) || 1000));
                    return reply(text('finished'));
                case 'fail': return reply(text('this tool always fails', true));
                case 'crash':
                    onCrash();
                    return null;
                case 'add_tool':
                    extra = true;
                    setTimeout(() => notify({ jsonrpc: '2.0', method: 'notifications/tools/list_changed' }), 50);
                    return reply(text('added'));
                case 'extra': return reply(text('extra works'));
                default:
                    return { jsonrpc: '2.0', id, error: { code: -32602, message: `Unknown tool: ${name}` } };
            }
        }
        default:
            return { jsonrpc: '2.0', id, error: { code: -32601, message: `Method not found: ${method}` } };
    }
}

let httpIndex = process.argv.indexOf('--http');
if (httpIndex < 0) {
    let send = message => process.stdout.write(JSON.stringify(message) + '\n');
    let lines = readline.createInterface({ input: process.stdin });
    lines.on('line', async line => {
        if (!line.trim()) {
            return;
        }
        let response = await handle(JSON.parse(line), send, () => process.exit(1));
        if (response) {
            send(response);
        }
    });
}
else {
    let port = parseInt(process.argv[httpIndex + 1], 10);
    let server = http.createServer((request, response) => {
        if (request.url !== '/mcp' || request.method !== 'POST') {
            response.writeHead(404).end();
            return;
        }
        if (request.headers.authorization !== 'Bearer mock-token') {
            response.writeHead(401, { 'Content-Type': 'text/plain' }).end('missing or wrong token');
            return;
        }
        let body = '';
        request.on('data', chunk => body += chunk);
        request.on('end', async () => {
            let message = JSON.parse(body);
            let answer = await handle(message, () => { }, () => request.socket.destroy());
            if (answer === null) {
                if (!request.socket.destroyed) {
                    response.writeHead(202).end();
                }
                return;
            }
            let headers = { 'Content-Type': 'application/json', 'Mcp-Session-Id': 'mock-session' };
            // An event stream for tools/call, a plain body for everything else, so both answer shapes are exercised.
            if (message.method === 'tools/call') {
                response.writeHead(200, { ...headers, 'Content-Type': 'text/event-stream' });
                response.end(`event: message\ndata: ${JSON.stringify(answer)}\n\n`);
            }
            else {
                response.writeHead(200, headers).end(JSON.stringify(answer));
            }
        });
    });
    server.listen(port, '127.0.0.1', () => console.log(`mock MCP on ${port}`));
}

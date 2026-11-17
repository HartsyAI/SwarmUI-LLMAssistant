// Starts and stops the e2e stack: the scripted MockLLM server and a THROWAWAY SwarmUI instance that has only this
// extension enabled and one "LLM: Remote (OpenAI-Compatible)" backend pointed at the mock.
//
// It never touches the real Data/, Output/ or Models/ folders: Swarm runs with its own --data_dir, --settings_file and
// --backends_file under E2E/.run/, and Paths.OutputPath / Paths.ModelRoot point inside that folder too.
//
// Requires an existing SwarmUI build at <swarm root>/src/bin/live_release (build SwarmUI first, as launch-windows.bat does).
// Core caches an extension's build as src/bin/extensions/<dll>/<dll>-<git HEAD>.dll and only rebuilds when that file is missing
// (it decides this before it knows about --environment dev), so uncommitted changes would otherwise run against a stale build.
// startStack() therefore deletes that cached file first, which makes core rebuild the extension from the working tree.

import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const e2eDir = path.resolve(here, '..');
const extensionDir = path.resolve(e2eDir, '..');
const extensionFolderName = path.basename(extensionDir);
const isWindows = process.platform === 'win32';

export const SWARM_PORT = Number(process.env.E2E_SWARM_PORT || 17801);
export const MOCK_PORT = Number(process.env.E2E_MOCK_PORT || 17802);
export const SWARM_URL = `http://localhost:${SWARM_PORT}`;
export const MOCK_URL = `http://localhost:${MOCK_PORT}`;

/** SwarmUI root: the folder holding src/Extensions/<this extension>, or E2E_SWARM_ROOT. */
export function swarmRoot() {
    if (process.env.E2E_SWARM_ROOT) {
        return path.resolve(process.env.E2E_SWARM_ROOT);
    }
    return path.resolve(extensionDir, '..', '..', '..');
}

function log(message) {
    console.log(`[e2e] ${message}`);
}

/** Runs a command to completion and throws with its output on failure. */
function run(command, args, options = {}) {
    let result = spawnSync(command, args, { encoding: 'utf8', ...options });
    if (result.status !== 0) {
        throw new Error(`${command} ${args.join(' ')} failed (${result.status}):\n${result.stdout || ''}\n${result.stderr || ''}`);
    }
}

/** Kills a process and everything it started. */
function killTree(child) {
    if (!child || child.exitCode !== null || !child.pid) {
        return;
    }
    if (isWindows) {
        spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F']);
    }
    else {
        try {
            process.kill(-child.pid, 'SIGKILL');
        }
        catch (e) {
            child.kill('SIGKILL');
        }
    }
}

/** Polls a URL until it answers 2xx, or throws once the deadline passes or the watched process exits. */
async function waitFor(url, label, timeoutMs, child) {
    let deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        if (child && child.exitCode !== null) {
            throw new Error(`${label} exited early with code ${child.exitCode}. See E2E/.run/*.log`);
        }
        try {
            let response = await fetch(url);
            if (response.ok) {
                return;
            }
        }
        catch (e) {
            // not up yet
        }
        await new Promise(resolve => setTimeout(resolve, 1000));
    }
    throw new Error(`${label} did not answer ${url} within ${Math.round(timeoutMs / 1000)}s. See E2E/.run/*.log`);
}

/** Deletes core's cached build of this extension so the next Swarm start rebuilds it from the working tree. */
function invalidateExtensionBuild(root) {
    let head = spawnSync('git', ['rev-parse', 'HEAD'], { cwd: extensionDir, encoding: 'utf8' });
    let hash = head.status === 0 ? head.stdout.trim().slice(0, 8) : 'unknown';
    let dllName = `SwarmExtension${extensionFolderName}`;
    let cached = path.join(root, 'src', 'bin', 'extensions', dllName, `${dllName}-${hash}.dll`);
    if (fs.existsSync(cached)) {
        try {
            fs.rmSync(cached, { force: true });
            log(`removed cached extension build ${path.basename(cached)} so it is rebuilt from the working tree`);
        }
        catch (e) {
            throw new Error(`Could not remove ${cached} (is a SwarmUI instance using it running?): ${e.message}`);
        }
    }
}

/** Writes the throwaway Swarm data folder and returns its paths. */
function seedData(root, runDir) {
    let dataDir = path.join(runDir, 'data');
    let outputDir = path.join(runDir, 'output');
    let modelsDir = path.join(runDir, 'models');
    for (let dir of [dataDir, outputDir, path.join(modelsDir, 'llm')]) {
        fs.mkdirSync(dir, { recursive: true });
    }
    let extensionsRoot = path.join(root, 'src', 'Extensions');
    let others = fs.existsSync(extensionsRoot)
        ? fs.readdirSync(extensionsRoot, { withFileTypes: true }).filter(e => e.isDirectory() && e.name !== extensionFolderName).map(e => e.name)
        : [];
    let slash = p => p.replace(/\\/g, '/');
    let settings = [
        'IsInstalled: true',
        'InstallDate: 2025-01-01',
        'InstallVersion: e2e',
        'LaunchMode: none',
        'Maintenance:',
        '\tCheckForUpdates: false',
        'Paths:',
        `\tModelRoot: ${slash(modelsDir)}`,
        `\tOutputPath: ${slash(outputDir)}`,
        'DisabledExtensions:',
        // FDS lists put their "- item" lines at the same indent as the key.
        ...others.map(name => `- ${name}`),
        ''
    ].join('\n');
    let backends = [
        '0:',
        '\ttype: llmassistant-openai',
        '\ttitle: Mock LLM',
        '\tenabled: true',
        '\tsettings:',
        `\t\tAddress: ${MOCK_URL}`,
        '\t\tDefaultModel: mock-model',
        '\t\tConnectionAttemptTimeoutSeconds: 5',
        ''
    ].join('\n');
    let settingsFile = path.join(dataDir, 'Settings.fds');
    let backendsFile = path.join(dataDir, 'Backends.fds');
    fs.writeFileSync(settingsFile, settings);
    fs.writeFileSync(backendsFile, backends);
    return { dataDir, settingsFile, backendsFile };
}

/** Starts the mock and Swarm; resolves with { stop() } once both answer. */
export async function startStack() {
    let root = swarmRoot();
    let swarmDll = path.join(root, 'src', 'bin', 'live_release', 'SwarmUI.dll');
    if (!fs.existsSync(swarmDll)) {
        throw new Error(`SwarmUI is not built: ${swarmDll} is missing. Build SwarmUI first (dotnet build src/SwarmUI.csproj --configuration Release -o src/bin/live_release).`);
    }
    let runDir = path.join(e2eDir, '.run');
    fs.rmSync(runDir, { recursive: true, force: true });
    fs.mkdirSync(runDir, { recursive: true });

    log('building MockLLM...');
    let mockProject = path.join(e2eDir, 'MockLLM', 'MockLLM.csproj');
    run('dotnet', ['build', mockProject, '-c', 'Release', '--nologo', '-v', 'q']);
    let mockDll = path.join(e2eDir, 'MockLLM', 'bin', 'Release', 'net8.0', 'MockLLM.dll');
    let mock = spawn('dotnet', [mockDll, '--port', String(MOCK_PORT)], {
        stdio: ['ignore', fs.openSync(path.join(runDir, 'mock.log'), 'w'), fs.openSync(path.join(runDir, 'mock.log'), 'a')],
        detached: !isWindows
    });
    await waitFor(`${MOCK_URL}/__health`, 'MockLLM', 30000, mock);
    log(`MockLLM up on ${MOCK_URL}`);

    let seeded = seedData(root, runDir);
    invalidateExtensionBuild(root);
    log('starting throwaway SwarmUI (it rebuilds the extension first, which takes a minute or two)...');
    let swarmLog = fs.openSync(path.join(runDir, 'swarm.log'), 'w');
    let swarm = spawn('dotnet', [
        swarmDll,
        '--environment', 'dev',
        '--launch_mode', 'none',
        '--host', 'localhost',
        '--port', String(SWARM_PORT),
        '--data_dir', seeded.dataDir,
        '--settings_file', seeded.settingsFile,
        '--backends_file', seeded.backendsFile
    ], {
        cwd: root,
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_CLI_UI_LANGUAGE: 'en', DOTNET_CLI_TELEMETRY_OPTOUT: '1' },
        stdio: ['ignore', swarmLog, swarmLog],
        detached: !isWindows
    });
    let stop = () => {
        killTree(swarm);
        killTree(mock);
    };
    try {
        await waitFor(`${SWARM_URL}/`, 'SwarmUI', 600000, swarm);
    }
    catch (e) {
        stop();
        throw e;
    }
    // Swarm answers even when the extension failed to build, which would make every spec fail with a confusing timeout.
    let swarmLogText = fs.readFileSync(path.join(runDir, 'swarm.log'), 'utf8');
    if (!swarmLogText.includes('[LLMAssistant] Initialized.')) {
        stop();
        let problems = swarmLogText.split('\n').filter(l => /error|failed/i.test(l)).slice(0, 8).join('\n');
        throw new Error(`SwarmUI is up but the LLM Assistant extension did not load. First errors in E2E/.run/swarm.log:\n${problems}`);
    }
    log(`SwarmUI up on ${SWARM_URL}`);
    return { stop, runDir };
}

/** Command line use: `node harness/stack.mjs` keeps the stack running until Ctrl+C, for poking at it by hand. */
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    let stack = await startStack();
    log('stack is running. Ctrl+C to stop.');
    let shutdown = () => {
        stack.stop();
        process.exit(0);
    };
    process.on('SIGINT', shutdown);
    process.on('SIGTERM', shutdown);
    await new Promise(() => { });
}

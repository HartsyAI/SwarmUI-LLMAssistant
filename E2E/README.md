# Browser e2e

Playwright specs that drive the real LLM Assistant tab in a real browser, against a **throwaway SwarmUI** and a **scripted mock LLM**. No GPU, no model, no network, and nothing in your own `Data/`, `Output/` or `Models/` is touched.

```
E2E/
  MockLLM/        scripted OpenAI-compatible server (C#, net8): chat, tool calls, usage, embeddings
  MockMCP/        a scripted MCP server (server.mjs; stdio, or Streamable HTTP with `--http`) the MCP specs connect to
  hooks/          a sample command hook (block-secrets.mjs) the hook specs run
  harness/        stack.mjs starts/stops MockLLM + the throwaway SwarmUI; Playwright global setup/teardown
  tests/          fixtures.ts + the specs (one spec file per feature area)
  .run/           (generated, gitignored) the throwaway data dir and logs
```

## Run it

Needs: Node 20+, the .NET SDK, and SwarmUI already **built** at `<SwarmUI>/src/bin/live_release` (the launch scripts do that on first start).

```bash
cd src/Extensions/SwarmUI-LLMAssistant/E2E
npm install
npx playwright install chromium
npm test
```

`npm test` runs the whole suite in Chromium. `npm run test:smoke` runs the specs tagged `@smoke` (load, send a message, open settings) in Firefox and WebKit; install those once with `npm run install-browsers`. A bare `npx playwright test` runs both, so it needs all three browsers.

Or `E2E/run-e2e.ps1` / `E2E/run-e2e.sh` from anywhere.

Each start takes a minute or two, because the extension is built from your working tree. SwarmUI runs from a **private root** in your system temp folder (links to the real SwarmUI folders, with its own `src/bin`, `src/obj` and `src/Extensions` holding only this extension). Core caches an extension's build keyed on its git HEAD and ignores uncommitted changes, and a SwarmUI you run yourself from the same checkout holds that cache locked; the private root avoids both, so the test instance never touches or waits on yours, and loads no other extension.

To poke at the stack by hand, or to iterate on specs without restarting SwarmUI each time:

```bash
npm run stack                       # terminal 1: MockLLM on :17802, SwarmUI on :17801 (Ctrl+C to stop)
E2E_REUSE_STACK=1 npm test              # terminal 2
```

Environment: `E2E_SWARM_PORT` (17801), `E2E_MOCK_PORT` (17802), `E2E_SWARM_ROOT` (the SwarmUI checkout; defaults to the one this extension sits in), `E2E_REUSE_STACK` (use an already-running stack).

## What the stack is

- SwarmUI runs with `--data_dir`, `--settings_file` and `--backends_file` under `E2E/.run/`, and `Paths.OutputPath` / `Paths.ModelRoot` inside it too.
- Only this extension is present in the private root, so only it is under test.
- One backend is seeded: `LLM: Remote (OpenAI-Compatible)` at the mock, default model `mock-model`. The mock also lists `mock-model-b` for compare mode.
- Four image models and one LoRA with no weights are seeded under `models/Stable-Diffusion` (header-only safetensors written by `stack.mjs`): `e2e-ideogram-4.safetensors`, whose keys make core classify it as Ideogram 4, `e2e-mystery.safetensors`, which core cannot place, and `e2e-declared-json.safetensors`, which core cannot place either but whose header carries `modelspec.prompt_*` keys. `e2e-hinted.safetensors` (unplaceable, with a title, trigger phrase and usage notes in its header) and `Lora/e2e-style-lora.safetensors` (core's default LoRA folder; the name is case-sensitive on Linux) (trigger phrase and tags) are for the image-tools specs. They exist so the prompt-styles and image-tools specs can check which style a model gets and what the list tools return; nothing can generate with them.
- The local GGUF engine (`LLM: Local`) is **not** covered here, since it needs a GPU. A PR that touches `HartsyLocalLLMProvider` needs a manual GPU smoke check on top.

## In CI

`.github/workflows/e2e.yml` runs on every pull request and every push to `main`. It checks out SwarmUI and this extension, builds SwarmUI, and runs:

- the unit tests (`dotnet test Tests/LLMAssistant.Tests.csproj`), once, in shard 1;
- the browser suite in Chromium split over **3 shards** (`npx playwright test --project=chromium --shard=N/3`, Playwright splits by spec file), each on its own runner with its own throwaway SwarmUI, with a **120-minute limit per shard** (`fail-fast` is off, so one failing shard does not cancel the others);
- an **`e2e-smoke`** job: the `@smoke` specs in Firefox and WebKit, on a runner of its own beside the shards;
- one aggregate job named **`e2e`**, the single check name to require: it passes only when every shard and the smoke job passed (a cancelled or skipped one fails it).

Each shard uploads its Playwright report and server logs as the `e2e-report-N` artifact. Specs inside one shard still run serially against one server (`workers: 1`), so a spec must not depend on another shard's state. No test counts are quoted in the docs: count them with `npx playwright test --list` and `dotnet test`.

## Scripting the mock

Specs set replies with `setScenarios([...])` from `tests/fixtures.ts`:

```ts
await setScenarios([{
    match: 'check my memory',            // substring of the LAST user message (or `regex`, or `model`)
    rounds: [
        { toolCalls: [{ name: 'memory_read', arguments: {} }] },   // round 1: the model calls a tool
        { text: 'I checked your memory.' }                          // round 2: the answer after the result
    ]
}]);
```

Round fields: `text`, `reasoning`, `toolCalls`, `usage`, `delayMs`, `chunkDelayMs` (slow streaming, for Stop tests), `error` (`{status, message}`). Which round answers is the number of assistant messages after the last user message, so tool loops walk the rounds in order. With no matching rule the reply is `Mock reply: <user text>`.

`mockRequests()` returns every request body the mock received, so a spec can assert what was actually sent to the model: the system prompt, a compaction summary, the tool list.

## Known gaps

- The local GGUF engine, CUDA and real embedding models are not exercised (see above): a change that touches them needs a manual check on real hardware.
- The harness has no text-to-image backend, so nothing here generates an image. `<llmprompt:...>` expansion is covered through core's `TestPromptFill` route, which runs the same prompt preparation without one. Image-bound features (the companion's reactions to a finished generation, `generate_image`) are not exercised end to end.
- Firefox and WebKit get the smoke specs only, so a browser-specific fault outside loading, sending and settings is not caught.
- `accessibility.spec.ts` runs axe on the tab's own elements in `modern_dark` and `modern_light`; other themes, core's page and real screen readers are not checked.
- The Playwright suite checks behaviour, not looks. Each PR also gets a manual pass in a browser at desktop and mobile widths and in light and dark themes.

# Browser e2e

Playwright specs that drive the real LLM Assistant tab in a real browser, against a **throwaway SwarmUI** and a **scripted mock LLM**. No GPU, no model, no network, and nothing in your own `Data/`, `Output/` or `Models/` is touched.

```
E2E/
  MockLLM/        scripted OpenAI-compatible server (C#, net8)
  harness/        stack.mjs starts/stops MockLLM + the throwaway SwarmUI; Playwright global setup/teardown
  tests/          fixtures.ts + the specs
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

Or `E2E/run-e2e.ps1` / `E2E/run-e2e.sh` from anywhere.

Each start takes a minute or two, because the extension is built from your working tree. SwarmUI runs from a **private root** in your system temp folder (links to the real SwarmUI folders, with its own `src/bin`, `src/obj` and `src/Extensions` holding only this extension). Core caches an extension's build keyed on its git HEAD and ignores uncommitted changes, and a SwarmUI you run yourself from the same checkout holds that cache locked; the private root avoids both, so the test instance never touches or waits on yours, and loads no other extension.

To poke at the stack by hand, or to iterate on specs without restarting SwarmUI each time:

```bash
npm run stack                       # terminal 1: MockLLM on :17802, SwarmUI on :17801 (Ctrl+C to stop)
E2E_REUSE_STACK=1 npx playwright test   # terminal 2
```

Environment: `E2E_SWARM_PORT` (17801), `E2E_MOCK_PORT` (17802), `E2E_SWARM_ROOT` (the SwarmUI checkout; defaults to the one this extension sits in), `E2E_REUSE_STACK` (use an already-running stack).

## What the stack is

- SwarmUI runs with `--data_dir`, `--settings_file` and `--backends_file` under `E2E/.run/`, and `Paths.OutputPath` / `Paths.ModelRoot` inside it too.
- Only this extension is present in the private root, so only it is under test.
- One backend is seeded: `LLM: Remote (OpenAI-Compatible)` at the mock, default model `mock-model`. The mock also lists `mock-model-b` for compare mode.
- Two image models with no weights are seeded under `models/Stable-Diffusion` (header-only safetensors written by `stack.mjs`): `e2e-ideogram-4.safetensors`, whose keys make core classify it as Ideogram 4, and `e2e-mystery.safetensors`, which core cannot place. They exist so the prompt-styles specs can check which style a model gets; nothing can generate with them.
- The local GGUF engine (`LLM: Local`) is **not** covered here, since it needs a GPU. A PR that touches `HartsyLocalLLMProvider` needs a manual GPU smoke check on top.

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

- The harness has no text-to-image backend, so nothing here generates an image. `<llmprompt:...>` expansion is covered through core's `TestPromptFill` route, which runs the same prompt preparation without one. Image-bound features (the companion's reactions to a finished generation, `generate_image`) are not exercised end to end.
- The Playwright suite checks behaviour, not looks. Each PR also gets a manual pass in a browser at desktop and mobile widths and in light and dark themes.

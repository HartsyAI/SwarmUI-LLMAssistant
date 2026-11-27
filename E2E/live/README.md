# Live smoke suite

Real scenarios against a SwarmUI **you are already running**, with the LLM and image backends **you** configured. It shows what the scripted mock of the main suite hides. It is opt-in: CI and `npm test` never run it.

The assertions are loose on purpose (the tool was called, its arguments were a JSON object, the turn ended, token counts were reported, nothing errored), never exact text. A failure is a finding about the extension, the engine or the model: read it, do not loosen it. What has been found so far is in [docs/PROVIDER-NOTES.md](../../docs/PROVIDER-NOTES.md).

## Run it

Needs Node 22 or newer (for its built-in WebSocket), `npm install` in `E2E/`, and a running SwarmUI with this extension and at least one local LLM backend. No browser is used.

```powershell
$env:LLMA_LIVE_BASE_URL = "http://localhost:7801"
```

```powershell
$env:LLMA_LIVE_MODEL = "Qwen3-4B-Q4_K_M.gguf"
```

Optional, for the image scenario:

```powershell
$env:LLMA_LIVE_IMAGE_MODEL = "SDXL/sd_xl_base_1.0.safetensors"
```

```powershell
npm run test:live
```

One scenario: add its file name, as in `npm run test:live -- 40-stop`. The report is printed at the end and saved to `E2E/.run/live/report.md`. Do not wrap a run in a time limit that kills it: clean-up and unloading happen at the end.

`npm run test:live-selftest` checks the suite's own safety rules (which backends it refuses, which image call it approves, what it deletes, unloads and counts) offline, with no server and no model.

## What is checked

| File | Scenario |
|---|---|
| `10-reply` | A plain reply streams, ends, reports usage and is saved with the model's name |
| `20-tool-call` | A tool call (`swarm_docs`, forced for the first round) with arguments that parse, a result, and an answer after it |
| `30-approval` | Ask mode: a denied `create_image_preset` call saves nothing; an allowed one saves the preset |
| `40-stop` | Stop mid-stream: the turn ends, the partial reply is saved, the next request is answered |
| `50-dry-run` | A headless dry run's token estimate against the real turn's count, with tools off and on (within a factor of 2) |
| `60-compaction` | A chat just past the threshold of the model's window is summarized before the reply |
| `70-rewrite` | A prompt rewrite with no model named (what automatic rewriting does) and with the chat model named |
| `80-knowledge` | Knowledge search matches by meaning (only with an embedding model) |
| `90-image` | An image made through the assistant in Ask mode, with automatic rewriting on for the chat; the file is not a flat frame |

A scenario whose needs are not met is **skipped with the reason**: nothing configured, a tool your assistant is not given, no free memory, a cap reached.

## Settings

All are environment variables. **None holds a key or password**, and a SwarmUI that requires a login is not supported.

| Variable | Default | Meaning |
|---|---|---|
| `LLMA_LIVE_BASE_URL` | *(required)* | The running SwarmUI |
| `LLMA_LIVE_MODEL` | *(required)* | Chat model id, as the extension lists it |
| `LLMA_LIVE_IMAGE_MODEL` | | Image model name, exactly as SwarmUI lists it. Without it no image is made |
| `LLMA_LIVE_EMBED_MODEL` | | Embedding model. The knowledge library must already be set to it (Settings > Knowledge) |
| `LLMA_LIVE_TRUSTED_BACKENDS` | | Backend ids (comma separated) you vouch are local; see below |
| `LLMA_LIVE_MAX_REQUESTS` | `40` | Most model requests in a run |
| `LLMA_LIVE_MAX_TOKENS` | `120000` | Most tokens in a run (reported counts, else estimates) |
| `LLMA_LIVE_MAX_REPLY_TOKENS` | `400` | Token cap of an ordinary reply |
| `LLMA_LIVE_MAX_IMAGES` | `1` | Most image scenarios in a run |
| `LLMA_LIVE_MIN_FREE_RAM_GB` / `_VRAM_GB` | `6` / `5` | Free memory wanted before the chat model is loaded |
| `LLMA_LIVE_MIN_FREE_RAM_IMAGE_GB` / `LLMA_LIVE_MIN_FREE_VRAM_IMAGE_GB` | `10` / `6` | The same before the image |
| `LLMA_LIVE_MAX_GPU_UTIL` | `30` | Percent GPU use above which the server counts as busy |
| `LLMA_LIVE_IDLE_TIMEOUT_S` | `600` | How long each scenario waits for a free server before it is skipped |
| `LLMA_LIVE_TURN_TIMEOUT_S` | `300` | Time limit of one reply |
| `LLMA_LIVE_IMAGE_TIMEOUT_S` | `720` | Time limit of the image turn |
| `LLMA_LIVE_KEEP_IMAGE` | | `1` keeps the generated image |
| `LLMA_LIVE_ALLOW_MCP` | | `1` runs the scenarios with tools although MCP tools are offered; see below |

A request's worst case (rounds, retries, a summary that fails) is held under the caps before it is sent and corrected from what the model reports; when the report is missing or incomplete, the larger figure stays. The rewrite inside the image generation reports no usage, so its worst case stays counted. **Not counted:** model requests a tool makes on its own, such as `caption_image` and `fuse_image_descriptions`, if the model calls one.

## Safety

- **Local backends only.** Before anything runs, before and after each scenario and at the end, the suite reads SwarmUI's backend list and refuses unless every backend that could take its requests is local. The local GGUF engine is accepted. A remote LLM backend (or ComfyUI / Auto1111 by address) needs both an address on this machine **and** its id in `LLMA_LIVE_TRUSTED_BACKENDS`: an address on this machine can still be a proxy to a paid service, which only you know. A paid image API backend may stay enabled, because SwarmUI sends it only its own `API Models/...`; the image scenario and the end of the run check that it was not used meanwhile (use by someone else in that time is reported too). Core's llama.cpp backend is local and ignored. Any other backend type refuses the run when it could take a request.
- **Only SwarmUI's API** is used, never an engine or model server directly.
- **No settings are changed**, no backend is added, edited, restarted or removed. Each chat has its own switches (tools, Ask mode, memory extraction off, rewrite).
- **The image call** is approved only when its prompt is exactly the requested text (a tag in a prompt can change the model and the backend after the approval), it names the configured model, pins one accepted backend and sets nothing else that matters. A default preset of the image tool, yours or the assistant's, skips the scenario.
- **Your data.** Every chat runs in Ask mode and every approval but the one a scenario asks for is denied. With tools on, a model can still run **read-only** tools of your assistant without asking, `web_search` among them, which reaches the network. A reading MCP tool would run unasked too and its server may charge, so scenarios with tools are skipped while MCP tools are offered, unless `LLMA_LIVE_ALLOW_MCP=1`.
- **Clean-up** deletes only the chats, preset, knowledge documents and image that this run made. It reads each chat for the images it made before deleting it, finds knowledge documents by the run id in their title, and says so when an image or document may remain that it cannot find. If a run is killed, `E2E/.run/live/state.json` lists what is left; the next run names it, keeps that record as `leftover-<run>.json` and deletes nothing of it.
- **What a run leaves behind.** Real turns behave as yours do: memory notes that go into a prompt are marked used, and a due retention sweep runs, which can forget notes unused longer than your retention setting (pause recall in Settings > Memory to avoid both). A first message ends your first-run guide if it was still showing; the tool calls count towards the hour's tool rate limits; SwarmUI's log has the requests.
- **A shared server.** Each GPU scenario waits until SwarmUI has no generation waiting or running and memory and GPU use leave room. A chat model request from elsewhere shows only as GPU use, so avoid running it beside heavy use. The suite unloads the chat model (all LLM providers' models) before the image and at the end, only if no chat model was loaded before the run, no other one is loaded now, and every GPU is seen to be quiet; if that cannot be seen, nothing is unloaded. It frees the image backend it used only if that was empty before, holds the suite's image model and every GPU is quiet. It cannot tell whether someone else loaded or is about to use the same model.

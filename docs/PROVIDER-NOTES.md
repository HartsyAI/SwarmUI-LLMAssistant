# Provider notes

What real backends do that the scripted mock does not, and what the extension does about each. The runs come from the opt-in [live smoke suite](../E2E/live/README.md).

## Tested

| | |
|---|---|
| Date | 2026-11-27 |
| Extension | `028e553` (`main` with #92), the build the running SwarmUI had loaded |
| SwarmUI | 0.9.8.4 (`d347f306`), Windows 11 |
| GPU | NVIDIA RTX 3060, 12 GB; 48 GB system RAM |
| Chat | `LLM: Local (HartsyInference GGUF)`, `Qwen3-4B-Q4_K_M.gguf`, window 40,960, device auto |
| Image | HartsyInference image backend, `SDXL/sd_xl_base_1.0.safetensors` |
| Not tested | Any `LLM: Remote (OpenAI-Compatible)` backend (Ollama, LM Studio, vLLM), Claude or another hosted provider, embeddings |

Scenarios ran with the composer's Thinking switch off. The local engine is the only provider covered, so nothing here says how an OpenAI-compatible server behaves.

## Results

One full run: 8 passed, 3 failed, 1 skipped in 4.6 minutes, 18 model requests, about 52,000 tokens, 1 image. The three failures are the two defects below. Afterwards the run's 11 chats, 2 presets and image were gone and the models it loaded were unloaded.

| Scenario | Result | Measured |
|---|---|---|
| Plain streamed reply | pass | 44 chunks; 460 prompt and 44 completion tokens |
| Tool-call round trip | pass | `swarm_docs({"action":"list"})`, 2 rounds |
| Ask mode, denied call | pass | nothing saved, the model told it was denied |
| Ask mode, allowed call | pass | preset saved (then deleted by the run) |
| Stop mid-stream | pass | ended at once; next request in 0.3 s |
| Dry run, tools off | pass | estimate 868, counted 811 (1.07) |
| Dry run, 11 tools | **fail** (defect 2) | estimate 868, counted 4,129 (0.21) |
| Compaction | pass | estimate 34,919 of 40,960; next request 586 prompt tokens |
| Rewrite, no model named | **fail** (defect 1) | fell back: `LLM model '' not found` |
| Rewrite, chat model named | pass | SDXL style, rewritten |
| Knowledge search | skipped | no embedding model |
| Image through the assistant | **fail** (defect 1) | the 768x768, 12-step SDXL image was made after one approval and is not a flat frame; its rewrite fell back. The turn took 109 s including the model load (163 s in an earlier run) |

The paid image API backend enabled on the same server was not used.

## Defects

"Fix open" means pull request #95, which was not merged or loaded when these runs were made, so its fixes are **not** verified here.

| # | Where | What happens | Tag |
|---|---|---|---|
| 1 | Automatic prompt rewrite, and `LLMAssistantRewritePrompt` without `rewriteModel` | With no rewriting model set, the request carries no model name. The local engine answers `LLM model '' not found in the LLM model folder(s)`, so the rewrite falls back and the prompt is used as written. The image the assistant made was generated from the unrewritten prompt. Naming the model works. Workaround: choose a rewriting model in the *Try a rewrite* box. | fix now (fix open) |
| 2 | Headless dry run (`estimatedPromptTokens`) | Tool definitions are not counted. The same message gives an estimate of 868 with 0 tools and with 11, while the real turns counted 811 and 4,129 prompt tokens: with tools on the estimate is 0.21 of the real figure. The "larger than the model's window" warning is built on the same figure. | fix now (fix open) |
| 3 | Long chats | The summarizer is given only the first 6,000 characters of each message, so the rest of a long message is not in the summary (a code at the end of a 34,407-character message was lost). Deliberate in `CompactionPlanner.BuildTranscript`; the README does not say so. | backlog |
| 4 | Local engine | Free GPU memory kept falling over a run while the model stayed loaded and new chats were made: 3.7 GB before the second scenario, 1.1 GB by the Stop scenario, 0.3 GB before the rewrites. Nothing failed, and the cause was not investigated (Reuse Chat Memory keeping one cache per chat is a guess). Other programs share the GPU. | backlog, engine: investigate |
| 5 | Local engine | `cachedTokens` was 0 on every turn, with Reuse Chat Memory on. Whether the prompt is reused and not reported was not checked. | backlog, engine |
| 6 | Usage | A turn stopped mid-stream got no usage from the local engine, so the reply has no token figures. | backlog |

## Local engine (HartsyInference GGUF), Qwen3 4B

| Behaviour | Seen | What the extension does |
|---|---|---|
| Usage | Prompt and completion tokens and rounds are reported for finished turns. `cachedTokens` was 0 on every turn, also with **Reuse Chat Memory** on. A stopped turn reports no usage. | A stopped reply shows no token figure for it. |
| Tool calls | A forced `swarm_docs` call and two forced `create_image_preset` calls had arguments that parsed, and the model answered after the result. | Nothing special needed. |
| Tool definitions | 11 tools added about 3,300 tokens to every request. | Turn tools off for a chat that does not need them. |
| Approvals | In Ask mode a write call waited, a denial reached the model as `denied`, and an allowed call ran. | As designed. |
| Stop | The reply ended at once, the partial text was saved with stop reason `cancelled`, and the next request answered in under a second. | As designed. |
| Long chats | The chat was summarized before the reply. The summary kept a name and a figure from the start of the first message (defect 3 for what it lost). | As designed. |
| Memory | Loading the 2.5 GB file at its full 40,960 window took free GPU memory from about 11 GB to under 4 GB. The image scenario starts with the model unloaded; the tool turn loads it again and SDXL still generated. | Nothing. Use the top bar's unload, or **Unload After Each Reply** on the backend, if an image model does not fit. |
| First reply | 5 to 28 s including the model load; later replies a few seconds. | |

Reported before these runs and not re-checked: `Qwen3.5-9B-Q4_K_M.gguf` returns garbled text on this engine, and a Z-Image generation came back as a black frame. Both are engine matters.

## Knowledge search

Not run: no embedding backend was configured. The local engine offers no embeddings to the extension, so this needs an OpenAI-compatible backend that has them. The scenario itself has therefore never run against a server.

## Not covered

Parallel tool calls, malformed tool arguments, skills, sessions and schedules made by the model, compare mode, images or documents sent to the model, and the browser interface (the suite uses the API only).

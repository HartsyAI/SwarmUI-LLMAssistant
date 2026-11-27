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

IMAGE_RESULTS_PLACEHOLDER

## Defects

| # | Where | What happens | Tag |
|---|---|---|---|
| 1 | Automatic prompt rewrite, and `LLMAssistantRewritePrompt` without `rewriteModel` | With no rewriting model set, the request carries no model name. The local engine answers `LLM model '' not found in the LLM model folder(s)`, so the rewrite falls back and the prompt is used as written. Naming the model works. Workaround: choose a rewriting model in the *Try a rewrite* box. | fix now |
| 2 | Headless dry run (`estimatedPromptTokens`) | Tool definitions are not counted. The same message gives an estimate of 868 with 0 tools and with 11, while the real turns counted 811 and 4,129 prompt tokens: with tools on the estimate is 0.21 of the real figure. The "larger than the model's window" warning is built on the same figure. | fix now |

IMAGE_DEFECTS_PLACEHOLDER

## Local engine (HartsyInference GGUF), Qwen3 4B

| Behaviour | Seen | What the extension does |
|---|---|---|
| Usage | Prompt and completion tokens and rounds are reported for finished turns. `cachedTokens` was 0 on every turn, also with **Reuse Chat Memory** on. A stopped turn reports no usage. | A stopped reply shows no token figure for it. |
| Tool calls | A forced `swarm_docs` call and two forced `create_image_preset` calls had arguments that parsed, and the model answered after the result. | Nothing special needed. |
| Tool definitions | 11 tools added about 3,300 tokens to every request. | Turn tools off for a chat that does not need them. |
| Approvals | In Ask mode a write call waited, a denial reached the model as `denied`, and an allowed call ran. | As designed. |
| Stop | The reply ended at once, the partial text was saved with stop reason `cancelled`, and the next request answered in under a second. | As designed. |
| Long chats | A chat estimated at 34,919 tokens in a 40,960 window was summarized and the next request was 586 prompt tokens. The summary kept a name and a figure from the start of the first message and not a code at the end of it: the summarizer is given only the first 6,000 characters of each message. | By design (`CompactionPlanner.BuildTranscript`), but the README does not say so: backlog, document it or split long messages. |
| Memory | With the model loaded at its full 40,960 window, free GPU memory fell from about 11 GB to about 3 GB for a 2.5 GB file. | Nothing. Use the top bar's unload, or **Unload After Each Reply** on the backend, before large image models. |
| First reply | About 28 s including the model load; later replies a few seconds. | |

Reported before these runs and not re-checked: `Qwen3.5-9B-Q4_K_M.gguf` returns garbled text on this engine, and a Z-Image generation came back as a black frame. Both are engine matters.

## Knowledge search

Not run: no embedding backend was configured. The local engine offers no embeddings to the extension, so this needs an OpenAI-compatible backend that has them.

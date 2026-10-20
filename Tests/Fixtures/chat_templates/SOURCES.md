# Chat template fixtures

Ground truth for `ReasoningHistoryTests`' `TakesReasoningContent` coverage (see the last section). Each file is the raw
`tokenizer.chat_template` string, byte-for-byte, with no header or commentary added, so a test reading one of these is
reading exactly what `GgufMetadata.GetString("tokenizer.chat_template")` (or the model's published
`tokenizer_config.json`/`chat_template.jinja`) returns.

## Read from the local GGUF itself (`tokenizer.chat_template` metadata key, via
`HartsyInference.ModelAssets.Gguf.GgufLoader`, the same reader the production code
uses), 2026-10-01:

| Fixture | Source file |
|---|---|
| `qwen3-4b.jinja` | `/mnt/model-storage/Models/llm/qwen3/Qwen3-4B-Q4_K_M.gguf` |
| `qwen2.5-1.5b-instruct.jinja` | `/mnt/model-storage/Models/llm/qwen25-1.5b/Qwen2.5-1.5B-Instruct-Q8_0.gguf` |
| `qwen3.5-0.8b.jinja` | `/mnt/model-storage/Models/llm/qwen35/Qwen3.5-0.8B-Q4_K_M.gguf` |
| `deepseek-r1-distill-qwen-1.5b.jinja` | `/mnt/model-storage/Models/llm/deepseek-r1/DeepSeek-R1-Distill-Qwen-1.5B-Q4_K_M.gguf` |
| `glm-4-9b-0414.jinja` | `/mnt/model-storage/Models/llm/glm4/GLM-4-9B-0414-Q4_K_M.gguf` |
| `llama-3.2-1b-instruct.jinja` | `/mnt/model-storage/Models/llm/llama32-1b/llama-3.2-1b-instruct-q8_0.gguf` |
| `mistral-7b-instruct-v0.3.jinja` | `/mnt/model-storage/Models/llm/mistral/Mistral-7B-Instruct-v0.3-Q4_K_M.gguf` |
| `gemma-4-e2b-it.jinja` | `/mnt/model-storage/Models/llm/gemma4/gemma-4-E2B-it-Q4_K_M.gguf` |
| `phi-3-mini-4k-instruct.jinja` | `/mnt/model-storage/Models/llm/phi/Phi-3-mini-4k-instruct-q4.gguf` |

These models are not distributed with this repo; the fixture is the extracted
template text only (a few KB), not the multi-GB checkpoint.

## Not available as a local GGUF, pulled from the model's own published HF repo,
2026-10-01:

| Fixture | Source |
|---|---|
| `deepseek-r1-distill-llama-8b.jinja` | `chat_template` field of `https://huggingface.co/deepseek-ai/DeepSeek-R1-Distill-Llama-8B/raw/main/tokenizer_config.json` |
| `glm-4.5.jinja` | `https://huggingface.co/zai-org/GLM-4.5/raw/main/chat_template.jinja` (GLM-4.5 ships its template as a separate file rather than inside `tokenizer_config.json`) |

## Copied from the HartsyInference engine's own test fixtures, 2026-10-17:

| Fixture | Source |
|---|---|
| `muse-spark-open.jinja` | `tests/HartsyInference.LLM.Tests/Fixtures/templates/muse-spark-open.jinja` in HartsyAI/HartsyInference at `36400d86` (the template llama.cpp's converter writes into the released Muse Spark GGUFs; the engine renders it byte-identical to transformers). Used by `ReasoningHistoryTests`. |

## Reasoning history (`TakesReasoningContent`, `ReasoningHistoryTests`)

- **True** (reads `reasoning_content` and has no `</think>` of its own, so a stored
  `<think>` block would reach it as answer text): `muse-spark-open`, `gemma-4-e2b-it`.
- **False**, because the template splits `<think>` out of the content itself:
  `qwen3-4b`, `qwen3.5-0.8b`, `glm-4.5`.
- **False**, because the template never reads `reasoning_content`: the two DeepSeek R1
  distills (they strip up to `</think>` themselves), `qwen2.5-1.5b-instruct`,
  `glm-4-9b-0414`, `llama-3.2-1b-instruct`, `mistral-7b-instruct-v0.3`,
  `phi-3-mini-4k-instruct`.

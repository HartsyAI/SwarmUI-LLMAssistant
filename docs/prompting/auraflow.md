# AuraFlow prompting guide

Status: reference draft, retrieved 2026-11-19. **No official prompting guide exists** in the retrieved sources; there are model cards, a launch blog and Diffusers docs only. Model is marked beta by its authors.

Sources:
- Official: https://huggingface.co/fal/AuraFlow-v0.3 and https://huggingface.co/fal/AuraFlow (model cards)
- Official blog: https://blog.fal.ai/auraflow/
- Integration docs: https://huggingface.co/docs/diffusers/main/en/api/pipelines/aura_flow (names the text encoder)
- Core: SwarmUI `src/Text2Image/T2IModelClassSorter.cs`, `docs/Features/Prompt Syntax.md`

## Swarm class ids

`auraflow-v1` (plus `auraflow-v1/tensorrt`). Compat class has no legacy prompt parser flag in core.

## Text encoder

Per the Diffusers docs: a single T5-family encoder, specifically EleutherAI `pile-t5-xl` (the model cards and blog do not name it). Diffusers default `max_sequence_length` is 256.
- Implication: full natural-language sentences; no CLIP-style 77-token limit or chunking.

## Prompt format and length

Natural language. The blog describes training on re-captioned data (no alt-text captions) following the DALL-E 3 approach, and a prompt-enhancement step that expands a prompt into a detailed lowercase caption covering spatial relations, object counts, colors and style (the blog says this raised GenEval from 0.64 to 0.703). Our inference (not a stated recommendation): detailed, explicit descriptions suit the model. Recommended length: not stated; Diffusers default cap is 256 tokens.

## What works / what does not

- Prompt adherence is the headline claim (GenEval 0.7+ for the v0.x series).
- Negative prompts: not mentioned on the cards. Diffusers exposes `negative_prompt` (ignored when guidance <= 1), so it runs through classifier-free guidance. Effectiveness: not stated.
- Text rendering: not mentioned in the sources.
- Layout/boxes: not stated.
- Weights: not stated. T5-style encoder, so see `_swarm-syntax.md`: weighting works poorly on T5 models.
- Style/quality keywords: the card examples use "cold color palette, muted colors, detailed, 8k"; effect not stated.

## Swarm compatibility

- Weights `<weight[x]:...>`: low confidence on a T5 encoder; avoid by default.
- Legacy `(x:1.2)`: not parsed for this class by core flag; do not emit.
- `<break>`: CLIP-only; do not emit.
- Regions: not stated for AuraFlow (core only notes SDXL strong, MM-DiT early-step); avoid unless asked. AuraFlow is described as MMDiT+DiT, so likely weak; unverified.
- Bbox: no registered JSON box format; do not emit.
- Embeddings: not applicable.

## Examples (our own wording, untested)

1. `a pale blue ceramic teapot on a wooden table, steam rising, soft window light from the left, shallow depth of field`
2. `a watercolor painting of three red kites flying over a green hillside, one kite in the upper left and two smaller kites on the right, pale sky`
3. `a rainy neon-lit alley at night, a woman in a yellow raincoat stands in the center holding a clear umbrella, puddles reflecting pink and teal signs, cinematic wide shot`

## Style file hints

- format: text
- negative: separate (supported mechanically, effectiveness not documented)
- supports: weights no (unreliable on T5), break no, regions no, boxes no, text-rendering unknown (not claimed)

## Open questions

- Which of the examples above, if any, the authors would endorse as style; no official tips exist.
- Whether the Swarm/Comfy AuraFlow path uses a 256 or other token cap.
- Negative prompt effectiveness and text rendering quality.

# PixArt-Sigma prompting guide (including 2K)

Status: reference draft, retrieved 2026-11-19. **No official prompting guide exists** in the retrieved sources; the GitHub README, model cards and Diffusers docs give encoder, token length and limitations, plus example prompts only. The 2K model card README is empty.

Sources:
- Official: https://github.com/PixArt-alpha/PixArt-sigma (README: token length 300, T5, SDXL-VAE, 256/512/1024/2K checkpoints)
- Official: https://huggingface.co/PixArt-alpha/PixArt-Sigma-XL-2-1024-MS (model card, limitations)
- Official: https://huggingface.co/PixArt-alpha/PixArt-Sigma-XL-2-2K-MS (model card, empty README)
- Paper abstract: https://arxiv.org/abs/2403.04692 (captions "more precise and detailed"; 4K claim; encoder and token details not in the abstract)
- Integration docs: https://huggingface.co/docs/diffusers/main/en/api/pipelines/pixart_sigma
- Core: SwarmUI `src/Text2Image/T2IModelClassSorter.cs`, `docs/Features/Prompt Syntax.md`

## Swarm class ids

`pixart-ms-sigma-xl-2` and `pixart-ms-sigma-xl-2-2k`. Both use the same compat class, which has no legacy prompt parser flag in core.

## Text encoder

A single T5 encoder (t5-v1_1-xxl per Diffusers). Token length 300 (README table; PixArt-alpha was 120). Diffusers default `max_sequence_length` is 300.
- Implication: complex, long natural-language prompts are fine up to about 300 T5 tokens; no CLIP chunking. Diffusers notes 8-bit text embeddings can cost quality.

## Prompt format and length

Natural language. Diffusers says T5 conditioning "makes the pipeline better at following complex text prompts with intricate details". The README sample long prompt covers subject, clothing, setting, lighting and camera style ("depth of field, cinematic 35mm film"); the short samples are a single sentence or comma style terms. Recommended length: not stated beyond the 300-token cap.

## What works / what does not

- Limitations listed on the card: cannot render legible text; struggles with compositional prompts (card example: a red cube on top of a blue sphere); hands and fingers may be wrong; not for factual depictions of people or events.
- Negative prompts: not mentioned on the cards. Diffusers says the PixArt negative prompt should be the empty string `""` (guidance runs against an empty prompt). So do not write negative lists unless the user insists; effect is not stated.
- Text rendering: stated as not supported. Do not rely on lettering in the image.
- Layout/boxes: not supported/stated.
- Quality keywords: only the "cold color palette, muted colors, detailed, 8k" example appears; effect not stated.

## Swarm compatibility

- Weights `<weight[x]:...>`: T5-only model; `_swarm-syntax.md` says T5 weighting does not work very well. Do not emit.
- Legacy `(x:1.2)`: not parsed for this class; do not emit.
- `<break>`: CLIP-only; do not emit.
- Regions: not stated for PixArt; treat as unsupported or weak.
- Bbox: no JSON box format registered; do not emit.
- Embeddings: not applicable.
- Tags like `<lora:...>` pass through normally.

## Examples (our own wording, untested)

1. `a small cactus with a round smiling face in a sandy desert, low sun, long shadows, soft pastel colors`
2. `a macro photograph of a dew-covered spider web between two reeds, shallow depth of field, backlit by golden morning light`
3. `a quiet harbor town at blue hour, wooden fishing boats moored along a stone quay, lit windows in narrow houses, calm water reflections, 35mm film look`

## Style file hints

- format: text
- negative: none (Diffusers says empty negative; not documented otherwise)
- supports: weights no, break no, regions no, boxes no, text-rendering no (stated limitation)

## Open questions

- Any official prompting recommendations (none found).
- Whether the 2K checkpoint has different token behaviour; its README is empty.
- Whether a non-empty negative prompt helps or hurts.
- Whether short comma-style or full-sentence captions work better; the training caption style is not stated in the retrieved pages.

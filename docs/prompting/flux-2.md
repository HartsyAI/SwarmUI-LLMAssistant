# Flux.2 prompting guide (dev, klein 4B, klein 9B)

Status: sources retrieved 2026-11-19. Official BFL prompting docs exist for the family, but the klein-specific guide that Swarm's docs link (`docs.bfl.ai/guides/prompting_guide_flux2_klein`) returned 404 on every BFL host tried; klein tips below come from BFL's Prompt Reference page. Several pages are written for [pro]/[max]; where a claim is only stated for those, it is marked. Sources:
- Official: https://docs.bfl.ai/guides/prompting_guide_flux2 (title: Prompting Guide FLUX.2 [pro] & [max]; also mirrored at docs.bfl.ml)
- Official: https://docs.bfl.ml/guides/prompting_unified_reference.md , https://docs.bfl.ml/guides/prompting_unified_building.md , https://docs.bfl.ml/guides/usecases_t2i_json_prompting.md , https://docs.bfl.ml/flux_2/flux2_overview.md
- Official: https://github.com/black-forest-labs/flux2 (README via CDN mirror), https://huggingface.co/black-forest-labs/FLUX.2-dev , https://huggingface.co/black-forest-labs/FLUX.2-klein-9B , https://huggingface.co/black-forest-labs/FLUX.2-klein-4B
- Official-adjacent: https://huggingface.co/docs/diffusers/api/pipelines/flux2
- Community (marked): https://deapi.ai/blog/prompting-flux-2-klein-what-works-what-doesnt-and-why (author's own testing, not BFL)
- Swarm docs: `docs/Model Support.md` Flux 2 section

## Swarm class ids

`flux-2` (Flux.2 Dev, `flux.2-dev`), `flux-2-klein-4b`, `flux-2-klein-9b`. Klein has distilled and Base variants plus a 9b-kv file (detected by `9b-kv` in the filename); all share the class id.

## Text encoders and what that implies

- Dev: Mistral-Small-3.2-24B-Instruct-2506 (BFL repo README, diffusers `Mistral3ForConditionalGeneration`). Klein: Qwen3 (9B card: "8B Qwen3 text embedder"; 4B: Qwen3 per diffusers, Swarm docs say Qwen 4B). Both are LLM encoders, so prose with relationships works; there is no CLIP chunking.
- Max prompt length in diffusers: 512 tokens (`max_sequence_length`). BFL docs: not stated.
- BFL klein tip: the encoder receives text literally, so be precise.

## Prompt format and length

- Natural language, subject first (earlier words get more attention per BFL), then action, style, context. Official bands: 10-30 words quick, 30-80 most projects, 80+ complex scenes.
- Klein: prose sentences over keyword lists; specify light source, direction and softness; end with a style and mood line for consistency (Prompt Reference page).
- JSON structured prompts are documented (fields `scene`, `subjects`, `style`, `color_palette`, `lighting`, `mood`, `background`, `composition`, `camera`) but the docs list support for [pro]/[max]; klein and dev support: not stated. Swarm docs say JSON "does not seem all that much more useful" and may trip prompt parsing. Prefer prose.
- Multilingual prompts work; English is most precise.

## What works and what does not

- Negative prompts: not supported (BFL, stated for [pro]/[max]; diffusers' klein pipeline uses an empty string as negative). Rewriter emits no negative and phrases positively ("sharp focus throughout", not "no blur").
- Weights: not documented. See compatibility below.
- Text rendering: put exact text in quotes, state placement, font style, size, colour; keep strings short and front-load them (BFL). The klein model cards warn text may be inaccurate or distorted.
- Hex colours: attach each hex code to one named object, e.g. "the vase in color #2B6CB0" (BFL).
- Photographic terms: camera model, lens, film stock help realism (BFL). Quality tags ("masterpiece") add nothing (BFL Building page).
- References: klein up to 4, dev recommended up to 6; name each image's role ("subject from image 1").
- Prompt upsampling: recommended for dev (BFL repo); klein has none, so write detailed prompts yourself (BFL overview).

## Swarm tag compatibility

- Weights `<weight[..]:..>` and legacy `(x:1.2)`: no legacy parser for `flux-2`; with an LLM encoder weights are a blunt multiplier. Do not emit.
- `<break>`: not applicable. Regions: MM-DiT, early-step only; avoid.
- `<bbox>`: no box format registered for Flux.2 (the generic JSON shape is not what it was trained on); do not emit. Klein guidance for boxes belongs to Flux.3 (see flux-3.md).
- Embeddings: not applicable. `<lora:>`, `<random:>`, `<wildcard:>`, `<trigger>`: pass through. Note Flux.1 LoRAs are incompatible (community).

## Examples (our own wording, unverified on a live model)

Prose: "Editorial photograph of a ceramic teapot steaming on a rough oak table, soft window light from the left, shallow depth of field, shot on a 50mm lens. Muted warm tones, calm morning mood."

Text and colour: "A flat-lay poster with the headline \"NIGHT MARKET\" in bold condensed sans-serif capitals at the top, color #F5E6C8 on a deep background color #1B2A41, paper lanterns below in warm orange."

Short klein style: "A corgi in a yellow raincoat splashing through a puddle on a grey city street, overcast soft light. Playful, bright, storybook mood."

## Style file hints

```
format: text   # json optional for dev/pro only, not recommended
negative: none
supports: weights=no, break=no, regions=limited, boxes=no, text-rendering=yes-short-quoted
```

## Open questions

- Klein-specific official prompting guide (URL 404): any klein length limits, JSON support, and negative handling for Base klein: not stated.
- Whether Base klein with CFG > 1 honours a real negative prompt: not stated by BFL.
- Official token limit (the 512 above is diffusers' default).

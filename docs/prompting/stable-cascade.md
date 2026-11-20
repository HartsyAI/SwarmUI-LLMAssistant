# Stable Cascade prompting guide

Status: P0 reference. No official prompting guide exists; the model card, GitHub README and Diffusers docs give sample prompts, stage descriptions and guidance defaults only. Treat style advice as community convention. Sources (retrieved 2026-11-19):
- Official: https://huggingface.co/stabilityai/stable-cascade (stages, resolution, limitations, license)
- Official: https://github.com/Stability-AI/StableCascade (stages; says nothing on prompting)
- Official: http://stability.ai/news-updates/introducing-stable-cascade (announcement; text encoder and aspect ratio not stated)
- Official docs: https://huggingface.co/docs/diffusers/v0.33.0/api/pipelines/stable_cascade (encoder ids, guidance defaults, negative_prompt)
- Community (only for context): a community fine-tune card says negative prompts are a must; not an official recommendation.
- Core: SwarmUI `docs/Obscure Model Support.md`, `src/Text2Image/T2IModelClassSorter.cs`, `docs/Features/Prompt Syntax.md`

## Swarm class ids covered

`stable-cascade-v1-stage-b`, `stable-cascade-v1-stage-c`, `stable-cascade-v1-stage-c/controlnet`, `stable-cascade-v1-stage-a/vae`. Compat `stable-cascade-v1`; standard size 1024x1024. Swarm needs the ComfyUI-format all-in-one `stage_b` / `stage_c` checkpoint pair in the same folder; either can be selected and both are used (Obscure Model Support doc). The compat class does not set the legacy `(x:1.2)` parser flag.

## Text encoder

- Stage C (the text-conditional prior) uses a frozen CLIP ViT-bigG/14 text encoder (Diffusers: `laion/CLIP-ViT-bigG-14-laion2B-39B-b160k`, with projection); the decoder pipeline has its own CLIP text encoder. The model card and README do not name the encoder.
- Stage C makes 24x24 latents from the text; Stage B and Stage A decode them to the image (compression factor 42, card).
- Implication: CLIP-style prompting. The Diffusers docs allow precomputed prompt embeds "for prompt weighting", so weighting is technically possible, but whether Swarm's ComfyUI path applies `<weight>` for Cascade is not stated.

## Prompt format and length

- Natural phrases or short sentences; official sample prompts: "Cinematic photo of an anthropomorphic penguin sitting in a cafe reading a book and having a coffee" (README) and "an image of a shiba inu, donning a spacesuit and helmet". Also "Astronaut in a jungle, cold color palette, muted colors, detailed, 8k".
- Token limit: not stated. CLIP encoders normally cap near 77 tokens (general knowledge, verify). Keep prompts compact; subject first.

## What works and what does not

- Weights: not stated for Cascade in Swarm docs; do not rely on them. Plain wording only.
- Negative prompts: supported by the pipeline when guidance is on, but the official example passes an empty string. Prompt and negative are ignored when guidance is disabled. Whether a non-empty negative helps is not stated; the "negative prompts are a must" claim comes from a community fine-tune only. Using a short negative is harmless; do not depend on it.
- Guidance (official defaults from Diffusers): prior guidance 4.0 (20 steps in the example), decoder guidance 0.0 (10 steps). Swarm exposes its own CFG; mapping to the two stages is not stated.
- Text rendering: not mentioned in any source; do not promise it.
- Layout / boxes: none. Regions: not stated for this family.
- Resolution: examples use 1024x1024; the card gives no aspect-ratio or size range. The announcement mentions a 2x super-resolution ControlNet (1024 to 2048). The latent grid is derived from height and width, so odd sizes are not guaranteed fine; stay near 1 megapixel and divisible by 64 (our caution, unstated).
- Prompt following: Stability claims best prompt alignment and aesthetics in its human evaluations (announcement); that is a claim, not guidance.
- People/faces: "may not be generated properly" (card). Lossy autoencoder.
- Quality/style keywords: only the sample "detailed, 8k" and "cinematic photo" phrases are official; no measured effect stated.
- License: non-commercial community license per the card; research use only.

## Compatibility with Swarm tags (`_swarm-syntax.md`)

- Weights `<weight>`: not stated for this family; avoid. Legacy `(x:1.2)`: parser not enabled for the Cascade class, so parentheses stay literal; do not emit.
- `<break>`: CLIP-only concept; not stated for Cascade. Do not emit.
- Regions/objects: not stated; avoid.
- `<bbox>`: no box format. Embeddings: none registered for Cascade in core; do not emit `<embed:>`.
- LoRA: Cascade-specific LoRAs only; otherwise rare. Segment, random, wildcard, preset, param, section tags: model independent.

## Examples (our own wording, to be checked against a real model)

```
cinematic photo of a red fox curled up asleep in a snowy forest clearing, soft blue morning light, shallow depth of field
```

```
a watercolor illustration of a small harbor town at sunset, boats at the dock, warm orange and teal palette
```

```
macro photograph of a dragonfly on a reed, dew on its wings, creamy blurred background
```

## Style file hints

- format: text (short natural phrases; CLIP-style, no JSON)
- negative: separate (optional; may be left empty)
- supports: weights unknown (avoid), break unknown (avoid), regions unknown (avoid), boxes no, text-rendering unknown (assume no)

## Open questions

- The text encoder named on the original card (only Diffusers names it).
- Official aspect-ratio and size range.
- Whether non-empty negative prompts, weights, `<break>` or regions behave in Swarm's Cascade path.
- Token limit.
- How Swarm's CFG maps onto prior guidance (about 4) versus decoder guidance (0).

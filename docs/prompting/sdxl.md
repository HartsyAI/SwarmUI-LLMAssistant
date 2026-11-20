# SDXL prompting guide (base, refiner, Turbo, SSD-1B)

Status: P0 reference. Stability published model cards and a paper but no dedicated prompting guide; the official Diffusers docs describe the encoder split and conditioning. Style advice below is marked where it is community convention. Sources (retrieved 2026-11-19):
- Official: https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0 (two encoders, refiner workflow, limitations)
- Official: https://huggingface.co/stabilityai/stable-diffusion-xl-refiner-1.0
- Official: https://arxiv.org/abs/2307.01952 (SDXL paper abstract only was read; size/crop conditioning details come from the Diffusers page)
- Official docs: https://huggingface.co/docs/diffusers/main/en/using-diffusers/sdxl (prompt vs prompt_2, 1024 default, micro-conditioning)
- Official: https://huggingface.co/stabilityai/sdxl-turbo (512, 1 to 4 steps, no guidance, no negative prompt)
- Vendor: https://huggingface.co/segmind/SSD-1B (distilled SDXL; recommends negative prompts, CFG about 9)
- Core: SwarmUI `src/Text2Image/T2IModelClassSorter.cs`, `docs/Model Support.md`, `docs/Features/Prompt Syntax.md`

## Swarm class ids covered

`stable-diffusion-xl-v1-base` (+ `/lora`, `/controlnet`, `/textual-inversion`, `/vae`, `/control-lora`, `/tensorrt`), `stable-diffusion-xl-v1-refiner` (+ `/tensorrt`), `stable-diffusion-xl-turbo-v1` (512 standard), `segmind-stable-diffusion-1b`. Standard size 1024x1024 (Turbo 512x512). The legacy `(x:1.2)` parser flag is set for the SDXL base and refiner compat classes, not for SSD-1B.

## Text encoders

- Two frozen encoders: CLIP ViT-L/14 and OpenCLIP ViT-bigG/14 (model card, Diffusers). Diffusers sends `prompt` to ViT-L and `prompt_2` to bigG; a different prompt per encoder "can improve quality" per the docs. Swarm exposes one prompt; do not try to split it (no Swarm syntax for it is listed).
- Refiner: same two encoders per its card, but it must share the base model's second encoder and VAE.
- SSD-1B: encoders not stated on its card (distilled from SDXL base).
- Implication: CLIP-style, keyword-friendly; weights and `<break>` apply. Natural sentences also work (convention).

## Prompt format and length

- Official sample prompts are short: "Astronaut in a jungle, cold color palette, muted colors, detailed, 8k" and "An astronaut riding a green horse".
- Token limit: not stated on the cards. CLIP chunks of 75 tokens; Swarm documents `<break>` as a manual chunk split. Put the subject and key attributes first.
- Format recommendation (community convention): a subject sentence followed by comma-separated style/lighting/camera phrases.

## What works and what does not

- Weights: work well (CLIP). Moderate values (about 0.7 to 1.4) are community convention; sources give no range.
- Negative prompts: supported by base and refiner. SSD-1B card: "Please do use negative prompting, and a CFG around 9.0"; its example negative is "ugly, blurry, poor quality" (short quote). **SDXL Turbo does not use negative prompts or guidance** (card); Swarm doc: CFG 1, Steps 1, Scheduler Turbo.
- Text rendering: cards say SDXL "cannot render legible text". Do not request exact lettering.
- Composition: cards say it struggles with compositional prompts (object on top of another). Short spatial statements; use regions for layout.
- People/faces: "may not be generated properly" per card; not photoreal-perfect.
- Resolution: 1024x1024 default; Diffusers says 768 or 512 can be tried but below 512 is unlikely to work. SSD-1B card lists 1152x896, 1344x768, 1536x640 style sizes. SDXL was trained on multiple aspect ratios (paper abstract); keep roughly one megapixel total. Turbo: 512x512.
- Micro-conditioning (original_size, target_size, crop coordinates): official, but a backend parameter, not part of the prompt. Swarm's SDXL handling applies enhanced settings by default ("scaled up rescond", Model Support doc). Prompt authors do not control it.
- Refiner: a second-stage model, final denoising steps or img2img over the base output, using the same prompt. Diffusers example: 40 steps, 80% base / 20% refiner. In Swarm route text to it with `<refiner>` section tags if different wording is wanted; otherwise the main prompt is reused.
- Quality keywords ("8k", "masterpiece"): "detailed, 8k" appears in the official sample, effect unmeasured. Keep few.
- Steps/CFG: only official numbers are Turbo (1 to 4 steps, CFG 0), SSD-1B (CFG about 9, vendor), Diffusers samples. Not stated otherwise.

## Compatibility with Swarm tags (`_swarm-syntax.md`)

- Weights: `<weight[1.1]:text>` fine; legacy `(text:1.2)` parsed for base/refiner (flag set). SSD-1B: use `<weight>`.
- `<break>`: valid. Regions: SDXL-like models "respond strongly" (Swarm doc), so SDXL is the best family for `<region>` and `<object>`.
- `<bbox>`: not applicable (no JSON box format); do not emit.
- Embeddings `<embed:name>`: supported (SDXL textual-inversion class registered; embeddings need both encoders, per Diffusers).
- LoRA: must be SDXL-family. `<refiner>` and `<base>` sections route text per stage.
- Turbo: negative prompt tags have no effect; use short positive prompts.

## Fine-tunes (Pony, Illustrious, etc.)

Popular community fine-tunes such as Pony Diffusion and Illustrious are SDXL-based but use different conventions (their own tag vocabularies and score/quality tags). Those are user-added custom styles and are not covered here. Do not apply this guide's wording to them without a style file of their own.

## Examples (our own wording, to be checked against a real model)

```
a fisherman mending a net on a wooden pier at sunrise, warm backlight, mist over the water, 35mm photograph, muted colors
```
negative: `blurry, oversaturated, extra fingers, watermark`

Regional layout:
```
a cozy reading room <region:0,0,0.5,1> a leather armchair by a window <region:0.5,0,0.5,1> a tall bookshelf full of old books
```

Turbo (no negative):
```
a macro photo of a dewdrop on a green leaf
```

## Style file hints

- format: tags (a subject sentence plus comma-separated phrases is fine)
- negative: separate (none for Turbo)
- supports: weights yes, break yes, regions yes (strong), boxes no, text-rendering no

## Open questions

- Official token limit and exact truncation (not stated on cards).
- Official recommended CFG/steps for base (not stated; Diffusers samples only).
- SSD-1B text encoders (not stated); whether Swarm's legacy parser applies to it (flag not set).
- Whether `<refiner>` text inherits the main negative prompt in current Swarm builds (not checked).

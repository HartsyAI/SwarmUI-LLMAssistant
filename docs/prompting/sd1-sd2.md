# Stable Diffusion 1.x / 2.x / 2 Turbo / Alt-Diffusion prompting guide

Status: P0 reference. No official prompting guide exists for these models; the model cards and release notes only give sample prompts and limitations. Everything about "how to write a good prompt" below that is not in a source is marked community/convention. Sources (retrieved 2026-11-19):
- Official: https://github.com/CompVis/stable-diffusion (README, raw) , SD v1 text encoder, 512 px, limits
- Official: https://huggingface.co/CompVis/stable-diffusion-v1-4 (no legible text, weak compositionality)
- Official: http://stability.ai/news-updates/stable-diffusion-v2-release (OpenCLIP, 512 and 768 defaults)
- Official: http://stability.ai/news-updates/stablediffusion2-1-release7-dec-2022 (SD 2.1: new prompting style, weights, negative prompts)
- Official: https://huggingface.co/stabilityai/sd-turbo (1 step, no guidance, no negative prompt)
- Official: https://huggingface.co/BAAI/AltDiffusion (AltCLIP, Chinese + English)
- Not retrievable: the SD 2 / 2.1 Hugging Face cards and the Stability-AI/stablediffusion GitHub README returned 401/404, so per-card details for 2.x (v-prediction, 768 vs base) come from the release notes and Swarm's own class registry.
- Core: SwarmUI `src/Text2Image/T2IModelClassSorter.cs`, `docs/Model Support.md`, `docs/Features/Prompt Syntax.md`

## Swarm class ids covered

`stable-diffusion-v1` (+ `/lora`, `/controlnet`, `/textual-inversion`, `-inpainting`), `stable-diffusion-v2-512`, `stable-diffusion-v2-768-v` (+ `/lora`, `/textual-inversion`), `stable-diffusion-v2-turbo` (detected by header keys; matches the SD-Turbo card), `alt_diffusion_v1_512_placeholder` (compat `alt_diffusion_v1`). Standard sizes in core: v1 and v2-512 and Turbo 512x512, v2-768-v 768x768.

## Text encoders

- SD1.x: frozen CLIP ViT-L/14 (CompVis README). SD2.x: OpenCLIP (LAION) per the release notes; the ViT/H size is community knowledge, not stated on the pages read.
- SD2 vs SD1 prompts are not interchangeable: the 2.1 announcement says 2.1 "supports the new prompting style and brings back many of the old prompts too", i.e. 2.0 dropped many SD1-era prompt habits (artist names, style tags). Treat that as the official hint; the exact list of lost tags is not stated.
- Alt-Diffusion: AltCLIP bilingual encoder (Chinese and English). English and Chinese prompts both work per the card.
- Turbo (SD 2 Turbo): the card does not name the encoder; assume the SD2 family encoder (unverified).
- Implication: CLIP-style models want short keyword-ish phrases, not paragraphs. Weights and `<break>` work.

## Prompt format and length

- Format: tags / short phrases, comma separated (convention, not stated officially). Official sample prompts are short: subject plus style terms, e.g. "a photograph of an astronaut riding a horse".
- Token limit: not stated in the model cards. Swarm documents `<break>` as a manual 75-token CLIP chunk split; core handles longer prompts by chunking. Keep the important material in the first ~75 tokens.
- Alt-Diffusion: Chinese or English text accepted; mixing is not described.

## What works and what does not

- Weights: work well on CLIP models (Swarm doc). The 2.1 announcement also endorses weighted prompts.
- Negative prompts: supported and useful on SD1/SD2 (2.1 notes: they "often eliminate" mangled hands and extra fingers, and work better in 2.1). Write them as a separate field, comma-separated defects ("blurry, extra fingers, low quality" is our wording). Exception: **SD 2 Turbo / SD-Turbo does not use negative prompts or guidance** (card: set `guidance_scale` 0.0, 1 to 4 steps; Swarm: CFG 1, Steps 1, Scheduler Turbo).
- Text rendering: not possible; v1-4 card says the model "cannot render legible text". Do not request exact lettering.
- Layout / boxes: no. Weak compositional ability ("red cube on top of a blue sphere" fails per v1-4 card). Regions help instead (below).
- Resolution: SD1 pretrained at 256 then fine-tuned at 512 (CompVis README); stay near 512x512. SD2: 512 (base) and 768 (768-v) defaults; 2.1 notes say non-standard resolutions and extreme aspect ratios work. Turbo is fixed at 512 (card: higher sizes "work as well" but 512 is the limitation). Do not rely on large sizes; use upscale/refine passes.
- People: faces and people "may not be generated properly" on all of these cards. SD 2.0 was trained with a LAION NSFW filter that cut many people images; 2.1 relaxed it.
- Quality/style keywords ("highly detailed", "8k", "digital painting"): appear in official sample prompts (Alt-Diffusion card lists "highly detailed, digital painting, trending on artstation"). Whether they help is community folklore; the sources do not claim a measured effect. Use sparingly.
- CFG/steps: only official numbers are SD1 sample guidance 7.5 (CompVis README), Alt-Diffusion sample guidance 7.5 and 25 steps with DPM-Solver (card), Turbo as above. Not stated as recommendations otherwise.

## Compatibility with Swarm tags (`_swarm-syntax.md`)

- Weights: `<weight[1.1]:text>` fine. Legacy `(text:1.2)` parsing is registered for SD1, SD2 and SD2 Turbo (`SupportLegacyPromptParser`), not for Alt-Diffusion (flag not set in core). Prefer the `<weight>` tag for Alt-Diffusion.
- `<break>`: valid (CLIP 75-token chunking).
- Regions `<region:...>`, `<object:...>`: allowed; SDXL-like models respond more strongly, SD1-era response per Swarm doc is "not stated per family". Small regions are weak.
- `<bbox[...]>`: no JSON box format for these models; do not emit.
- Embeddings `<embed:name>`: the main home of this feature (SD1-era); core registers textual-inversion classes for v1 and v2-768. Embeddings are encoder-specific: an SD1 embedding does not work on SD2.
- LoRA: `<lora:...>` must match the family (v1 LoRAs do not work on v2).
- Alternate/fromto/random/wildcard/preset/param: model independent.

## Examples (our own wording, to be checked against a real model)

SD1.x, tags:
```
a lighthouse on a rocky coast at dusk, dramatic clouds, golden hour light, oil painting, <weight[1.2]:highly detailed waves>
```
negative: `blurry, low quality, text, watermark, extra limbs`

SD2.1, short descriptive phrase:
```
a red fox sitting in fresh snow, soft morning light, shallow depth of field, photograph
```
negative: `cartoon, deformed, extra legs, jpeg artifacts`

SD2 Turbo (no negative, one step):
```
studio photo of a ceramic teapot on a wooden table
```

## Style file hints

- format: tags (short comma-separated phrases; text for Turbo and Alt-Diffusion is acceptable as a short sentence)
- negative: separate (none for SD2 Turbo)
- supports: weights yes, break yes, regions yes (weak), boxes no, text-rendering no

## Open questions

- Token limit and truncation behaviour for each card (not stated; Swarm chunks at 75).
- Which SD1-era prompt tags 2.0/2.1 lost (only "new prompting style" stated).
- Official prompting advice for 2.x beyond the 2.1 announcement (HF cards unreachable).
- SD-Turbo text encoder (not named on the card).
- Alt-Diffusion training resolution and whether Swarm applies legacy `(x:1.2)` parsing to it (not enabled in core).

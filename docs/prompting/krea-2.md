# Krea 2 prompting guide

Status: P0 reference. Sources retrieved 2026-11-19, obtained as tool-generated summaries (not literal page text). Krea's newer Krea 3 exists (per a search result); this doc covers Krea 2 only.
- Official: https://github.com/krea-ai/krea-2 (README; `docs/prompting.md` is the official prompting guide, raw: https://raw.githubusercontent.com/krea-ai/krea-2/main/docs/prompting.md)
- Official: https://huggingface.co/krea/Krea-2-Turbo (model card)
- Official (hosted product, not the open weights): https://www.krea.ai/blog/explorative-prompting-krea-2 , https://www.krea.ai/docs/user-guide/features/krea
- Docs from Comfy: https://docs.comfy.org/tutorials/image/krea/krea-2.md (text encoder file, `prompt_enhance` toggle)
- Host (third party): https://fal.ai/learn/tools/krea-2-prompting-guide (Medium vs Large advice)
- Core: SwarmUI `docs/Model Support.md`, `docs/Features/Prompt Syntax.md`, `WorkflowGenerator.cs`, `ExtraNodes/SwarmComfyCommon/SwarmText.py`.

## Swarm class ids
`krea-2` (and `krea-2/lora`), default 1024x1024. Swarm supports the Turbo and the Raw (base) checkpoints.

## Text encoder
Qwen3-VL (Krea's page: with multi-layer feature aggregation); Comfy's docs and Swarm name the 4B size. VAE: Qwen-Image VAE. LLM/VLM encoder, so no CLIP chunking.

## Prompt format
- Natural language. Official guide: long, detailed prompts give the best results, but good images come with little effort. Two patterns appear in its examples: comma-separated descriptive phrases (subject, attributes, lighting, background) and full sentences.
- Good prompts cover subject and details, clothing or materials, colors, lighting, background, composition or camera framing, rendering style.
- Token or character limit: not stated. Supported languages: not stated (the card only says phrasing and language can influence adherence).
- Official tip for LLM-assisted writing: the repo ships an expansion system prompt (`expansion.txt`); we write our own instead of copying it. Comfy's workflow has an LLM `prompt_enhance` toggle (default on) with a max-token setting.

## What works / does not
- Style: name it directly and add rendering details (brushwork, shading, grain, texture). The guide's examples span 3D render, anime, cel animation, flat illustration, digital painting, ink, collage, film or macro photography. Cinematic and photographic terms are common (shallow depth of field, low or high angle, macro lens, golden hour).
- Text rendering: put the words to render in quotation marks inside the prompt. Reliability is not stated.
- Negative prompts: not mentioned by the README or the guide. Treat as unsupported; Turbo runs with guidance 0 anyway. Write exclusions inline only if needed.
- Weights/emphasis: nothing in the official docs. See the Swarm hack below.
- Layout and boxes: no JSON or bbox format documented. Describe placement in words.
- Hosted-product features (Creativity levels, Generative Sliders, style references, moodboards, Medium/Large names): not applicable to the local checkpoints; the open weights are Raw and Turbo.
- Resolution: Raw trained up to about 1K; Turbo about 1K to 2K; the official CLI accepts 1024 to 2048, padded to multiples of 16. Swarm docs say anywhere from 128 to 4096 works, default 1024.
- Steps/CFG (official): Turbo 8 steps with guidance 0 and `--mu 1.15` (Swarm: sigma shift 1.15, CFG 1). Raw: full sampler with guidance (README example 52 steps at 3.5; CLI defaults 28 steps at 4.5); Swarm docs: 20+ steps, CFG around 4+ (untested).
- Safety: the weights are gated and deployers must filter. Swarm docs say NSFW terms are stripped by a built-in text refiner (Swarm's claim, not upstream's).
- Licence: code Apache-2.0; weights under the Krea 2 Community License (commercial licence available).

## Swarm tag compatibility (per `_swarm-syntax.md`)
- `<weight[1.2]:text>`: Swarm's docs say Krea 2 has dedicated hacks. In code, when **Model Specific Enhancements** is on (default), per-token weights are applied through attention: weights below 1 scale those tokens' value vectors, weights above 1 add an attention bias of `(w-1)*2` at those positions, on the conditional pass. Strength and quality are untested; keep weights modest. Legacy `(x:1.2)`: not stated for Krea 2.
- `<break>`: CLIP-only; do not emit. Regions: not stated. `<bbox>`: no registered format, so core uses the generic fraction format; do not rely on it. Embeddings: not applicable.
- `<lora>`: fine (style and identity LoRAs exist; the official raw-to-turbo LoRA too). `<random>`, `<wildcard>`, `<fromto>`: normal Swarm behaviour.
- Reference images in the prompt are seen only by the text encoder by default (Swarm docs); not an editing model.

## Examples (our own wording; untested)
1. Photo, comma style: `A weathered fisherman mending a blue net on a wooden pier at golden hour, salt-stained yellow raincoat, low-angle 35mm shot, shallow depth of field, warm backlight, fine film grain, quiet documentary mood.`
2. Illustration with text: `Flat-color poster illustration of a fox reading under a street lamp in the snow, thick clean outlines, limited palette of navy, cream and orange, and the words "WINTER BOOKS" in bold hand-lettered capitals across the bottom.`
3. Loose start (exploratory style the hosted guide recommends): `A cat riding a bicycle` then add `retro cartoon illustration` once a direction appeals.

## Style file hints
- format: text
- negative: none
- supports: weights=yes (Swarm attention hack, modest values), break=no, regions=unknown, boxes=no, text-rendering=yes (quote the words)

## Open questions
- Token limit, language support, negative prompt behaviour on Raw, weight effect size, and whether legacy `(x:1.2)` reaches the same hack: not stated.
- Whether Raw needs a different prompt style than Turbo (the official guide only mentions Turbo).
- Krea's `docs/prompting.md` was read through a summary; the exact wording of its tips should be re-read before we paraphrase more of it.

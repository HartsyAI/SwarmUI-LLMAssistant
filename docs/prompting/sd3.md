# Stable Diffusion 3 / 3.5 prompting guide

Status: reference draft, retrieved 2026-11-19. There is **no official long-form prompting guidance** in the sources actually retrieved; Stability's model cards give encoder facts and example settings only. Stability's "Stable Diffusion 3.5 Prompt Guide" (https://stability.ai/guides/stable-diffusion-3-5-prompt-guide) appears in search results, but fetching it returned 404 from this environment, so nothing in this doc is taken from it. Treat everything marked "community" as unverified.

Sources:
- Official: https://huggingface.co/stabilityai/stable-diffusion-3.5-large (model card)
- Official: https://huggingface.co/stabilityai/stable-diffusion-3.5-medium (model card)
- Official: https://huggingface.co/stabilityai/stable-diffusion-3-medium (model card)
- Official integration docs: https://huggingface.co/docs/diffusers/main/en/api/pipelines/stable_diffusion/stable_diffusion_3
- Paper: https://huggingface.co/papers/2403.03206 (abstract only, via the Diffusers page)
- Core: SwarmUI `src/Text2Image/T2IModelClassSorter.cs`, `docs/Features/Prompt Syntax.md` (see `_swarm-syntax.md`)
- Community (search snippet, not verified): negative prompts are said to work on 3.5 Large and Medium because they use classifier-free guidance.

## Swarm class ids

`stable-diffusion-v3-medium`, `stable-diffusion-v3.5-large`, `stable-diffusion-v3.5-medium` (plus `/lora`, `/controlnet` variants and the generic compat id `stable-diffusion-v3`). All four compat classes have the legacy prompt parser enabled in core.

## Text encoders

Three fixed encoders: OpenCLIP-ViT/G, CLIP-ViT/L, and T5-xxl. CLIP context is 77 tokens. T5 context is "77/256 tokens at different stages of training" (3.5 Large card). Diffusers defaults T5 to 256 tokens and shows `max_sequence_length=512` for long prompts.
- Implication: the CLIP encoders see only the first ~77 tokens; the T5 encoder carries the long detail. Put the subject, style and key nouns first.
- 3.5 Medium card: artifacts "on the edge of generations" can appear when T5 tokens go over 256; if so, shorten the prompt.
- SD3 Medium can run without T5 (`sd3_medium_incl_clips.safetensors`); its card says performance "will differ". Diffusers docs call the loss slight. A prompt for a no-T5 setup should be short and CLIP-style.

## Prompt format and length

Natural language. The cards show both a short phrase and a multi-paragraph description, plus comma-style keywords ("cold color palette, muted colors, detailed, 8k"). Recommended length: not stated. Practical upper bound from the cards: about 256 T5 tokens (3.5 Medium warns beyond that).

## What works / what does not

- Text rendering: the cards claim improved typography and use the sign prompt "a capybara holding a sign that reads Hello World". No tips given; limits not stated.
- Negative prompts: the cards do not discuss them. Diffusers exposes `negative_prompt` and ignores it when guidance <= 1 (`guidance_scale` default 7.0). Example negatives in Diffusers docs: "lowres, low quality, worst quality" (IP-Adapter example). Community: works on the CFG models; style negatives written as plain phrases.
- Weights: not covered by official sources. See compatibility below.
- Layout / boxes: not stated.
- Resolution: Diffusers default 1024x1024 "for the best results". 3.5 Medium trained progressively 256 to 1440 with mixed-scale final stage; a maximum megapixel count is not stated in the retrieved text.
- Settings (examples only, not recommendations): 3.5 Large 28 steps, CFG 3.5 (4.5 for the long-prompt example); 3.5 Medium 40 steps, CFG 4.5; SD3 Medium 28 steps, CFG 7.0. 3.5 Medium card recommends Skip Layer Guidance for better structure and anatomy (Diffusers lists layers [7, 8, 9], scale 2.8, start 0.01, stop 0.2 as Stability's values for Medium).
- Different Medium vs Large data: the same prompt can give different results.
- Quality keywords: only the "detailed, 8k" style appears in examples; effect not stated.

## Swarm compatibility

- Weights: SD3 has CLIP encoders, and per `_swarm-syntax.md` CLIP weighting works well while T5 weighting does not; SD3's split means partial effect. Per-family behaviour: not stated. Legacy `(text:1.2)` is parsed (core flag on).
- `<break>`: CLIP chunking only; whether it helps SD3 is not stated. Avoid unless the prompt exceeds 77 tokens and the user asked.
- Regions: core says MM-DiT models (SD3, Flux) mostly use regions in early steps then ignore them; keep regions simple and large.
- Bbox: no JSON box format registered for SD3; do not emit `<bbox>`.
- Embeddings: SD1-era feature; not recommended here.

## Examples (our own wording, untested)

1. Short: `a red fox curled up asleep in fresh snow, soft morning light, shallow depth of field, photograph`
2. Text: `a hand-painted wooden shop sign reading "BLUE HERON BAKERY" above a green door, overcast daylight, 35mm photograph`
3. Long (T5 detail, subject first): `A cozy attic studio at dusk. A ceramicist in a linen apron shapes a vase at a wheel, clay-spattered hands, warm lamp light on shelves of glazed bowls, rain on the skylight, muted earth tones, documentary photograph.`

## Style file hints

- format: text
- negative: separate (supported via CFG; quality of results unverified for official guidance)
- supports: weights yes (CLIP-side, partial), break yes (CLIP chunk only), regions yes (weak), boxes no, text-rendering yes (modest, no official tips)

## Open questions

- Official Stability prompt guide content (page not retrievable here).
- Official negative prompt guidance and recommended negatives.
- Recommended prompt length and whether CLIP vs T5 sections should be written differently in Swarm.
- Whether `<break>` or `(x:1.2)` weights measurably help on SD3.
- Maximum supported resolution for 3.5 Large and Medium.

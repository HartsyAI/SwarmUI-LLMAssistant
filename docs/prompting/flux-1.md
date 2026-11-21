# Flux.1 prompting guide (schnell, dev, Kontext dev, Fill/Depth/Canny)

Status: sources retrieved 2026-11-19. Official BFL prompting material for Flux.1 text-to-image is thin (the model cards only say prompt following depends heavily on prompting style); the Kontext editing page is official. Everything marked (community) or (Swarm docs) is not BFL guidance. Sources:
- Official: https://huggingface.co/black-forest-labs/FLUX.1-dev and https://huggingface.co/black-forest-labs/FLUX.1-schnell (model cards)
- Official: https://docs.bfl.ml/kontext/kontext_image_editing.md and https://docs.bfl.ml/guides/prompting_unified_reference.md (Kontext editing tips)
- Official-adjacent: https://huggingface.co/docs/diffusers/api/pipelines/flux (encoders, token limits, Fill/Canny/Depth/Kontext example settings; these are code examples, not BFL recommendations)
- Swarm docs: `docs/Model Support.md` (Flux.1 section), `src/Text2Image/T2IModelClassSorter.cs`
- Not retrievable: the Kontext prompting page that Swarm's docs link (`docs.bfl.ai/guides/prompting_guide_kontext_i2i`) returned 404 on 2026-11-19.

## Swarm class ids

Compat class `flux-1` (short code Flux). Covers Flux.1 schnell, dev, and the Tools/Kontext variants. Per the Swarm docs, Kontext is misdetected by default and the user sets architecture `Flux.1 Kontext Dev` manually; Fill, Depth and Canny are ordinary model files.

## Text encoders and what that implies

- Two encoders: CLIP-L (`clip-vit-large-patch14`) and T5-XXL (`google/t5-v1_1-xxl`) (diffusers docs). By default the same prompt goes to both.
- T5 does the heavy lifting, so natural-language sentences are right. CLIP-style comma tag piles gain nothing.
- Token limits (diffusers docs): schnell max 256 T5 tokens, dev default 512. Keep schnell prompts clearly under ~200 words.

## Prompt format and length

- Natural language prose; subject first, then setting, style, lighting. No official length band for Flux.1 (BFL's 10-30 / 30-80 / 80+ word bands are published for the newer unified docs, not for Flux.1; treat as a reasonable guide only).
- The dev/schnell model cards use a short comma-separated example ("Astronaut in a jungle, cold color palette, muted colors, detailed, 8k"), so terse prompts work; nothing official recommends it as best practice.

## What works and what does not

- Negative prompts: do not work. Both models are distilled (dev guidance-distilled, schnell timestep-distilled); diffusers only applies a negative prompt when `true_cfg_scale > 1`, which Swarm does not normally use (Swarm docs: CFG 1, "negative prompt won't work"; dev may tolerate slightly higher CFG, community-level observation). Rewriter must phrase everything positively and emit no negative.
- Weights: Swarm docs say T5-based models do not weight well. Do not add weights.
- Text rendering: works for short quoted strings; the model cards state no limits. Not officially documented for Flux.1.
- Quality keywords ("8k", "masterpiece"): the card example uses "detailed, 8k"; BFL's newer docs say such tags add little (those are for Flux.2/3, not Flux.1). Treat as optional.

## Kontext dev (editing)

- Prompt describes the change only; the image supplies the rest. Official tips (BFL docs): one edit per prompt for iterative work; quote text and use "Replace 'old' with 'new'"; keep exact casing; state what must stay the same (face, hair); use specific verbs; give each change its own explicit detail.
- Bright annotation boxes drawn on the input image can steer local edits and are removed from the output (BFL docs).
- Multi-image input is "hacky and limited quality" per Swarm docs.

## Fill / Depth / Canny

- Fill is an inpaint model: describe what should fill the masked area.
- Depth/Canny need a depth map / edge image as init image; the prompt describes the desired result, not the control image.

## Swarm tag compatibility

- Weights (`<weight[1.1]:...>`): blunt at best; do not emit. Legacy `(x:1.2)`: the `flux-1` compat class does not set the legacy parser flag, so it would be sent literally; do not emit.
- `<break>`: CLIP chunking concept only; do not emit.
- `<region>`/`<object>`: MM-DiT models mostly honour regions in early steps; weak for small regions. Avoid unless the user asked.
- `<bbox>`: no JSON box format registered for Flux.1; the tag would compile to the generic format, which the model was not trained on. Do not emit.
- Embeddings: not applicable. `<lora:>`, `<random:>`, `<wildcard:>`, `<trigger>`: pass through.

## Examples (our own wording, unverified on a live model)

Dev, text-to-image: "A weathered fisherman mending a blue net on a wooden pier at dawn, low golden sunlight from the left, mist over the water behind him, 35mm photograph with shallow depth of field and fine film grain."

Schnell, short: "A paper-cut style fox curled up asleep in a snowy forest clearing, soft blue evening light, layered pale blue and white paper."

Kontext edit: "Change the red jacket to dark green leather while keeping her face, hairstyle and the street background exactly the same."

## Style file hints

```
format: text
negative: none
supports: weights=no, break=no, regions=limited, boxes=no, text-rendering=short-quoted-strings
```

## Open questions

- Official BFL recommended length and style guidance for Flux.1 text-to-image: not stated.
- Whether legacy `(x:1.2)` is parsed for Flux.1 in practice (compat flag suggests no).
- Official limits on text-rendering length for Flux.1: not stated.
- Whether `<segment>`/`<object>` inpaint paths behave well with Fill: not stated.

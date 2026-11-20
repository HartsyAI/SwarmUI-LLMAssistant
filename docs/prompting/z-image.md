# Z-Image prompting guide (Z-Image base, Z-Image Turbo)

Status: P0 reference. Official sources give settings and one long-descriptive-prompt style but no written prompting guide. Sources (retrieved 2026-11-19; fetched via a summarizing tool):
- Official: https://github.com/Tongyi-MAI/Z-Image (variants table, CFG/steps, resolution, Prompt Enhancer mention)
- Official: https://huggingface.co/Tongyi-MAI/Z-Image and https://huggingface.co/Tongyi-MAI/Z-Image-Turbo (model cards)
- Official paper: https://arxiv.org/abs/2511.22699 (abstract only; the page did not name the encoder)
- Official (Comfy): https://docs.comfy.org/tutorials/image/z-image/z-image-turbo (names the text encoder)
- Core (community, Swarm maintainer): SwarmUI `docs/Model Support.md` Z-Image section
- Community conclusions are marked (community).

## Swarm class ids

`z-image` (covers Turbo and base, both load as the same class), `z-image/lora`, `z-image/control-diffpatch`. Edit and Omni-Base are listed by the official repo as "to be released"; no class exists for them.

## Text encoder

- Qwen3 4B (`qwen_3_4b.safetensors`), per the Comfy docs. The GitHub/HF pages do not name it; they describe text, visual-semantic tokens and VAE tokens joined in one stream (Single-Stream DiT, 6B).
- Implication: an LLM encoder, so sentence-style prompts are natural; CLIP-era tricks (weights, `<break>`, embeddings) do not apply.

## Prompt format and length

- Natural language, long and structured. Official examples are multi-sentence: subject, clothing, pose, lighting, color palette, camera style, background, mood.
- Languages: English and Chinese; Turbo is highlighted for accurate bilingual (Chinese/English) text rendering. Swarm docs (community): other languages understood decently.
- Length limit: not stated.
- Prompt Enhancer: the official pages mention a PE that adds reasoning/world knowledge but give no usage details and no template. Our rewriter should therefore supply the missing world knowledge itself (name the landmark, the species, the era) and write it out concretely.

## What works / what does not

| | Z-Image Turbo | Z-Image (base) |
|---|---|---|
| Negative prompt | not covered / not used | supported, "Robust Negative Control"; optional |
| Diversity | lower (RL-trained, distilled) | higher |

- Negative prompts (base): write a plain list of what to remove ("blurry, extra fingers, watermark" style, our wording). The official example leaves it empty, so it is optional. Turbo effectively ignores it.
- Text rendering: put literal strings in quotes inside the prompt and describe placement/style. Official example places literal English words in the prompt.
- Quality keywords / style tags: not stated; long descriptive prompts are the documented pattern.
- Layout/boxes: not stated; no JSON box format.

## Swarm tag compatibility (see `_swarm-syntax.md`)

- Weights (`<weight>` and `(x:1.2)`): LLM-encoder family, a blunt multiplier on conditioning; may do nothing. Do not emit; preserve if the user wrote them.
- `<break>`: not applicable. Regions: avoid (MM-DiT-style models mostly use them early only; Z-Image behaviour is not stated). `<bbox>`: not registered for `z-image`. Embeddings: not applicable.
- `<lora:>`, `<wildcard:>`, `<random:>`, `<trigger>`, `<param:>`, `<segment:>`: pass through unchanged.
- `<base>`/`<refiner>` sections only if the user already uses them.

## Examples (our own wording, untested)

Idea "old fisherman at dawn":
```
A weathered fisherman in his seventies stands at the bow of a small wooden boat at dawn, wearing a faded yellow oilskin coat and a knitted grey cap, salt-and-pepper beard, deep wrinkles around calm eyes. Low golden sunlight skims the flat sea from the left, mist hangs in the distance, a coiled rope in the foreground. Photographic, 50mm lens, shallow depth of field, quiet and contemplative mood.
```

Bilingual sign (text rendering):
```
A neon sign on a rainy night street reading "OPEN 24H" in English with the Chinese "全天营业" beneath it, magenta and cyan glow reflecting on wet asphalt, a lone cyclist passing in the blurred background.
```

Base-model negative (separate field): `blurry, low resolution, distorted hands, watermark, oversaturated`.

## Style file hints

- format: text
- negative: separate for base; none for Turbo
- supports: weights=no (do not emit), break=no, regions=no, boxes=no, text-rendering=yes (quoted text, English and Chinese)

## Open questions

- Exact text encoder per official docs (only the Comfy page names it); max prompt length (not stated).
- PE template and behaviour (mentioned, not documented).
- Whether Edit/Omni-Base prompts need a different form (not released as of the repo page).
- Official language list beyond English and Chinese (not stated; the base model card tags English only).

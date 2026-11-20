# Mage-Flow prompting guide

Status: draft reference. Official GitHub README and paper give settings only; no prompting guide. The Hugging Face card `microsoft/Mage-Flow` returned HTTP 401, so it was not read. Sources (retrieved 2026-11-19; fetched through a summarizing tool):
- Official: https://github.com/microsoft/Mage (and `mage_flow/README.md`), paper https://arxiv.org/abs/2607.19064 ("Mage-Flow: An Efficient Native-Resolution Foundation Model for Image Generation and Editing")
- Integration: https://docs.comfy.org/tutorials/image/mage-flow/mage-flow.md ; SwarmUI `docs/Obscure Model Support.md` (MageFlow), `WorkflowGeneratorModelSupport.cs`.

Identification: Microsoft's 4B native-resolution model family (MIT licence; research use, "not intended for product or service deployment"). The paper page lists authors but no affiliations; Microsoft attribution comes from the repo and HF org.

Swarm class id: `mage-flow` (Base, RL-aligned, Turbo; edit variants not covered).

## Text encoder
- **Qwen3-VL** (README; size not stated there). ComfyUI docs and Swarm core: Qwen3-VL-4B. The paper abstract does not name the encoder; one third-party summary claiming CLIP is contradicted by the README.
- Implication: LLM/VLM encoder; natural language.

## Prompt format and length
- Natural language. The README example prompts are long and descriptive (lighting, camera, style) but no explicit guidance or limit is given ("not stated"). `prompt_template` selects the encoder template, not an enhancer. Prompt enhancement: referenced in the README Usage list, details not stated.
- Languages: README showcase shows legible English and Chinese text.

## What works / what does not
- **Weights:** not stated; treat as unsupported.
- **Negative prompt:** **supported**: `neg_prompts` (CLI `--neg_prompt`), default a blank string, applied only when CFG is above 1. So separate negative field for Base/RL, none for Turbo.
- **Text rendering:** shown in the showcase; no settings or rules documented. Quote literal text.
- **Layout/boxes:** not stated.
- **Quality:** Swarm doc rates it poor against contemporaries; do not over-promise in rewrites.

## Swarm tag compatibility (see `_swarm-syntax.md`)
- `<weight>`, legacy `(x:1.2)`: legacy parser off. Avoid.
- `<break>`: do not emit. Regions: weak. `<bbox>`: no format registered. Embeddings: n/a.
- `<lora:>`, `<wildcard:>`, `<random:>`: fine.

## Video notes
Not a video model.

## Examples (our own wording, to be checked against a real model)

"city street":
```
A rainy night in a busy city street seen from a low angle, reflections of neon signs in the puddles, a cyclist with a yellow jacket passing under a streetlight, shallow depth of field, cinematic color grading, fine detail.
```

"wide banner":
```
A wide panoramic banner of a desert at dusk, long dunes in orange and purple light, a single camel caravan far in the distance, the words "SILK ROAD" in thin white letters centered near the top.
```

## Style file hints
- format: text
- negative: separate (Base, RL), none (Turbo)
- supports: weights=no, break=no, regions=weak, boxes=no, text-rendering=basic (quote the text)

## Open questions
- Official model card contents (not retrievable); encoder size from an official source.
- What the README's "prompt enhancement" feature does.

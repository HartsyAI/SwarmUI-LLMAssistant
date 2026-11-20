# Anima prompting guide

Status: P1 reference. The only family in this batch with an official, specific prompting section and documented weights. Sources (retrieved 2026-11-19; the model card was read through a summarizing tool, so verify tag spellings before shipping a validator):
- Official: https://huggingface.co/circlestone-labs/Anima (model card, "Prompting" and "Generation settings" sections)
- Integration: SwarmUI `docs/Model Support.md` (Anima), `src/Text2Image/T2IModelClassSorter.cs`; Diffusers has an Anima pipeline page (not read).

Swarm class id: `anima` (2B anime/illustration model by CircleStone Labs, built on NVIDIA Cosmos-Predict2; non-commercial licence for the model, outputs not restricted).

## Text encoder
- **Qwen3 0.6B base** (`qwen_3_06b_base`) through an LLM adapter, plus the **Qwen-Image VAE**. Small encoder: short or vague prompts misbehave.

## Prompt format and length
- Trained on Danbooru-style tags, natural language, and mixtures. Lowercase tags with spaces, not underscores (score tags are the exception). Where Danbooru and Gelbooru differ, prefer Gelbooru.
- Tag order, section by section (order inside a section is arbitrary): quality/meta/year/safety tags; `1girl`/`1boy`/`1other`; character; series; artist; general tags. Random tag dropout was used in training, so not every tag is needed.
- Quality: human-score tags (`masterpiece`, `best quality`, `good quality`, `normal quality`, `low quality`, `worst quality`), PonyV7-style `score_9`..`score_1`, both, or neither. Do not put `score_*` in both positive and negative on the Aesthetic variant (over-stylizing); `masterpiece, best quality` in the positive is safe.
- Year tags: `year 2025`, or `newest`/`recent`/`mid`/`early`/`old`. Meta: `highres`, `absurdres`, `anime screenshot`, `official art`. Safety: `safe`, `sensitive`, `nsfw`, `explicit`.
- **Artist tags need an `@` prefix** (`@name`), otherwise the effect is very weak.
- Natural language: capitalize character and series names normally; use at least 2 descriptive sentences; when naming a character also describe their appearance, especially with several characters. Tags and sentences can mix in any order; quality/artist tags may lead a sentence prompt.
- Non-anime datasets used a first caption line `ye-pop` or `deviantart` (then alt-text or title).
- Official positive prefix: `masterpiece, best quality, score_7, safe, `. Official negative: `worst quality, low quality, score_1, score_2, score_3, artist name, blurry, jpeg artifacts, chromatic aberration`.

## What works / what does not
- **Weights:** official: weighting works but needs **higher values than SDXL**, e.g. `(chibi:2)`. Core enables Swarm's legacy `(text:1.2)` parser for this class (`SupportLegacyPromptParser`), so both `(x:1.5)` and `<weight[1.5]:x>` are reasonable; start higher than for SDXL.
- **Negative prompt:** supported; write it as a tag list in the separate negative field (see above). Anima-Turbo runs at CFG 1 where negatives have no effect.
- **Text rendering:** weak for long text; single words and short phrases are more reliable. Realism is weak by design.
- **Layout/boxes:** not stated.
- **Resolution (official):** 512 to 1536 px per side; Swarm doc recommends 1024 and warns higher values degrade.
- **Settings (official):** 30-50 steps, CFG 4-5; Anima-Turbo CFG 1, 8-12 steps. Samplers: `er_sde` default, `euler_a`, `dpmpp_2m_sde`, `euler`; Swarm doc defaults match (Simple scheduler, ER-SDE).

## Swarm tag compatibility (see `_swarm-syntax.md`)
- Weights, legacy `(x:1.2)`: supported (see above). `<break>`: not CLIP, do not emit. Regions: weak. `<bbox>`: no format registered. Embeddings: not stated.
- `<lora:>`, `<wildcard:>` (good fit for tag lists), `<random:>`: fine.

## Video notes
Not a video model.

## Examples (our own wording, to be checked against a real model)

Tags:
```
masterpiece, best quality, score_7, safe, year 2024, 1girl, silver hair, red scarf, standing in snow, night, street lamps, falling snow, looking at viewer, soft lighting
```

Mixed:
```
masterpiece, best quality, safe. A girl with short black hair and a yellow raincoat jumps over a puddle on a quiet town street. The sky is gray and the colors are soft, watercolor-like. 1girl, rain, from side
```

## Style file hints
- format: tags (natural-language sentences also fine; mix allowed)
- negative: separate (tag list)
- supports: weights=yes (use larger values than SDXL), break=no, regions=weak, boxes=no, text-rendering=short-only

## Open questions
- Which official variant (Base, Aesthetic, Turbo, v1.0) the Swarm default points at, and whether quality-tag advice differs by variant.
- Whether `<embed:>` has any use; not stated.

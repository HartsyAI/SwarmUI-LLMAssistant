# MOSS Image prompting guide

Status: P0 reference. Sources retrieved 2026-11-19, obtained as tool-generated summaries (not literal page text); verify against the repo before shipping a validator.
- Official: https://github.com/OpenMOSS/MOSS-Image (README)
- Official: https://huggingface.co/OpenMOSS-Team/MOSS-Image-Base (model card)
- Official skill: https://github.com/OpenMOSS/MOSS-Image/tree/main/skills/moss-image-prompt-enhancer (`SKILL.md`, `references/layout.md`, `scripts/compile_prompt.py`)
- Core: SwarmUI `src/Text2Image/T2IBoundingBoxFormat.cs` (`moss-image`), `T2IPromptHandling.cs`, `docs/Model Support.md`.

## Swarm class ids
`moss-image` (MOSS Image Base and SFT, default 1024x1024). The SFT variant is quality tuned; Base is less trained and more variable (Swarm docs).

## Text encoder
Not named in the README or card. Swarm's loader uses Qwen 3.5 9B (Swarm-sourced, not upstream-confirmed). LLM encoder: no CLIP chunking, no CLIP-style weighting.

## Prompt format
Three accepted forms per the card: natural-language prose, tag sequences, JSON layouts. Plain prompts up to 4K tokens (README). The official skill produces two formats:
1. **Plain caption**: cohesive English prose (or the user's language). Describe subject, setting, framing, medium, lighting, materials, palette, mood. Avoid redundant quality tags, unrequested style changes, word-count targets and process notes. Express spatial relations in words; no coordinates inside captions. Keep short briefs short.
2. **Layout JSON**: use only when exact text placement, posters, multi-panel images or controlled object relations need spatial control. A detailed scene alone does not call for it. An explicit user request for either format wins.

Layout draft (what the skill writes, then compiles). Key order: `width`, `height`, `bbox_coord_space` (`"abs"`), `bbox_format` (`"xyxy"`), `summary`, `items`.
- `width`/`height`: positive multiples of 16, equal to the final image size.
- `summary`: `caption` (required, whole image), optional `meta` (strings), `canvas` (`single`, `multi_panel`, `collage`), `palette`.
- `items`: flat, non-empty array. Item keys: `label` (`object`, `character`, `text`), `caption` (appearance; for text also typography), `bbox` (absolute pixels `[x_min,y_min,x_max,y_max]`, top-left origin), `text` (required for `text` items: exact characters, `\n` for line breaks), optional `tags`, `palette`. No `children`, `id`, `parent`; unknown fields are rejected by the compiler.

Compiled prompt (the string given to the model): top-level keys `summary`, `items`, `bbox_coord_space`: `"abs_centralized"`, `bbox_format`: `"yxyx"`; `width`/`height` are removed; boxes become `[y1,x1,y2,x2]` with the canvas centre subtracted (floor for min edges, ceil for max edges, then subtract `size//2`); compact JSON, `ensure_ascii=False`. Boxes must lie inside the canvas with positive size; the compiler rejects rather than clips. The skill says never centre or reorder coordinates by hand.

## What works / does not
- Text rendering: preserve wording exactly (language, punctuation, case, line breaks); write real copy, not placeholders. Quoted lines in an instruction are not automatically visible text. If text does not fit, enlarge or recompose; do not delete copy.
- Boxes mark placement only (not borders); overlap allowed. Each text item's `text` holds the exact string.
- Negative prompt: empty by default; a `negative_prompt` can be set (README). The skill defines no negative field; keep exclusions in the positive wording. Official note: negative-branch layer skipping at layer 22 is used by default.
- Resolution: multiples of 16; README examples 1024x1024 and 1456x1456; Swarm docs: 1024 default, down to 256 fine, higher is variable.
- Steps/CFG (official): 50 steps, CFG 3, shift 5, flow-matching Euler. Swarm docs: about 20 steps, CFG 3 to 7, and optional skip-layer guidance (scale 0, layer 22) of questionable benefit.
- Languages: English and Chinese per card tags. Quality keywords: none documented.
- Licence: Apache 2.0 for pipeline code (README) and weights (card).
- The skill is written for an agent with local file and Python tools; it only creates prompt files.

## Swarm tag compatibility (per `_swarm-syntax.md`)
- `<weight>`, legacy `(x:1.2)`, `<break>`, embeddings: not meaningful; do not emit. Regions: not stated; use `<bbox>`.
- `<bbox[x1,y1,x2,y2]:text>` (fractions 0..1): core replaces the tag in place with one compact object `{"label":"object","caption":text,"bbox":[y1,x1,y2,x2]}` using centred pixels (`floor(frac*size)-size//2` for min, `ceil` for max, y first). Core adds no `summary`, `items` wrapper or `bbox_*` keys.
- `<lora>`, `<random>`, `<wildcard>`: keep only inside caption strings if they survive JSON escaping.

## Examples (our own wording; untested on a real model)
Plain caption: `A rain-soaked night street in a small port town, a yellow bicycle leaning on a lamp post, neon reflections on wet cobblestones, shot at eye level on a 35mm lens, teal and amber palette, quiet and cinematic.`

Compiled layout (1024x1024 canvas; centred coordinates computed by hand: the first item's draft box x1=128, y1=128, x2=896, y2=512 becomes `[-384,-384,0,384]` in y1,x1,y2,x2 order):
```json
{"summary":{"meta":["poster"],"canvas":"single","caption":"A minimal travel poster with a tiny sailboat on a calm sea and a small title below."},"items":[{"label":"object","caption":"A small white sailboat on a flat pale-blue sea.","bbox":[-384,-384,0,384]},{"label":"text","caption":"Thin geometric sans-serif capitals in navy.","text":"CALM WATERS","bbox":[250,-300,330,300]}],"bbox_coord_space":"abs_centralized","bbox_format":"yxyx"}
```
(In practice a pipeline should compute these numbers with code, as the official compiler does.)

## Style file hints
- format: text (plain caption by default) or json (layout, only when placement is needed)
- negative: separate (empty by default; unproven benefit)
- supports: weights=no, break=no, regions=unknown, boxes=yes, text-rendering=yes

## Open questions
- Core cross-check: core's centring maths (`floor`/`ceil` on `frac*size`, rounded to 9 decimals, minus `size/2` integer division, order y1,x1,y2,x2) matches `compile_prompt.py`. Mismatches: (1) core always emits `label:"object"` and no `text`, so `<bbox>` can never create a `text` item; text items must be written separately, and then the author must do the centring by hand, which the skill forbids. (2) Core emits items only, with no `summary` or `bbox_coord_space`/`bbox_format` keys, so a valid compiled prompt needs a wrapper built elsewhere. (3) Core does not check that width/height are multiples of 16. (4) Core's `bbox` needs `0<=x<=1`, which always fits in-canvas, so the official out-of-bounds rejection is not an issue.
- Text encoder identity; token limits for layouts; whether plain prose and layout JSON mix well; whether the model works without the wrapper keys.

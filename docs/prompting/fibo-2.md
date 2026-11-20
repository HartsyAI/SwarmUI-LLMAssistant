# Fibo 2.0 prompting guide

Status: P0 reference. Sources retrieved 2026-11-19. Most pages were obtained as tool-generated summaries, not literal text; only the diffusers page was read in full. Verify field names against the repos before shipping a validator.
- Official: https://huggingface.co/docs/diffusers/main/en/api/pipelines/bria_fibo2 (Fibo 2.0 pipeline doc: schema example, short forms, negative, text images)
- Official: https://huggingface.co/briaai/Fibo-2.0-dev (model card; license, 50 steps / guidance 5, prompt-to-JSON via `bria-vgl`)
- Official docs, **dialect unclear**: https://docs.bria.ai/vgl (VGL schema reference; the only place `bounding_box` appears)
- Host docs (third party): https://fal.ai/models/bria/fibo-2/generate/api (field list for `vgl.objects` / `vgl.text_render`)
- Mirror, unverified: https://skills.cat/skills/bria-ai/bria-skill/vgl (copy of Bria's VGL skill; key order and word limits)
- Different model, do not mix: the first-generation FIBO card (SmolLM3 encoder, ~1000-word captions) and the BBQ paper https://arxiv.org/html/2602.20672 (`fibo-bbq-preview`, corner boxes).
- Core: SwarmUI `src/Text2Image/T2IBoundingBoxFormat.cs` (`fibo-2`), `T2IPromptHandling.cs`, `docs/Model Support.md`.

## Swarm class ids
`fibo-2` (compat class `fibo-2`, "Fibo 2.0", default 1024x1024). Covers the Dev and Edit checkpoints; Swarm docs say the Edit one also does plain text-to-image.

## Text encoder
Qwen3-VL-4B-Instruct (official). A Perceiver compresses its hidden states into gist tokens, and some transformer blocks also cross-attend to the encoder. It is an LLM/VLM encoder, so there is no CLIP chunking or CLIP-style weighting.

## Prompt format
- Trained on structured JSON captions, passed as one JSON string. The official page says freeform text "will not work well". Swarm's docs say plain text runs but quality and understanding are poor.
- Two short forms were also trained and are passed through unchanged: the short description as plain text, and exactly `{"short_description":...,"text_render":[...]}`.
- The pipeline drops empty values plus `aesthetic_score` and `preference_score`, then serializes compactly. Emit compact JSON (`separators=(",",":")`, `ensure_ascii=False`).
- Length: long captions are intended (first-gen FIBO: 1000+ words). Fibo 2.0 token limit: not stated. Mirror limits (unverified): `short_description` up to 200 words, up to 5 objects, object `description` up to 100 words. A host says its field takes up to 4000 characters (third party).

## Schema (Fibo 2.0 dialect)
Top-level key order seen in the official diffusers example: `short_description`, `objects`, `background_setting`, `lighting`, `aesthetics`, `photographic_characteristics`, `style_medium`, `context`, `artistic_style`. The mirrored skill lists `artistic_style` before `context` and puts `text_render` after them (then `edit_instruction`). No formal order rule is stated; keep the diffusers order and put `text_render` last.
- `short_description`: string. `background_setting`: string for what is not in `objects`. `style_medium`: e.g. photograph, oil painting, 3D render. `artistic_style`: short phrase. `context`: image purpose, e.g. a travel photograph.
- `lighting`: `conditions`, `direction`, `shadows` (strings).
- `aesthetics`: `composition`, `color_scheme`, `mood_atmosphere` (strings).
- `photographic_characteristics`: `depth_of_field`, `focus`, `camera_angle`, `lens_focal_length`.
- `objects[]`: `description`, `location` (words such as "center", "top-left"), `relative_size` (words such as "large within frame"), `shape_and_color`, `texture`, `appearance_details`, `relationship`, `orientation`, `number_of_objects`; people add `pose`, `expression`, `clothing`, `action`, `gender`, `skin_tone_and_texture`.
- `text_render[]`: `text` (required), `location`, `size`, `color`, `font`, `appearance_details`. An explicit empty list tells the model the image has no text.
- The older docs.bria.ai VGL page uses another dialect: `color_palette` as RGB triplets, `bounding_box` as `[x, y, w, h]` "normalized" (range not stated), `monk_scale`, `relative_distance`. Which dialect Fibo 2.0 was trained on beyond the fields above is not stated.

## What works / does not
- Negative prompt: the default is the null caption (same structure, every value empty). A free-text negative is not documented; avoid it. Hosted styles use their own structured negatives.
- Text rendering: list each string in `text_render`. The pipeline also draws each text as an image for the encoder (black on white, one line, long text split at 256 characters); this is automatic in diffusers.
- Layout: officially described with words (`location`, `relative_size`). Numeric boxes: see Open questions.
- Resolution: multiples of 16 near 1 MP; 1024x1024 default, 1424x800 and 1248x832 shown. Swarm docs: 256 to 1536 works well.
- Steps/CFG: official Dev defaults are 30 steps with guidance 5 (card text says 50); Edit Turbo uses 4 steps at guidance 1. Swarm docs recommend CFG 1 and 8+ steps for dev. These conflict; not resolved here.
- Prompt rewriting: official route is Bria's prompt-to-JSON VLMs (`bria-vgl`, Gemini-based or a local VLM) which turn a short idea into the full caption. The mirrored skill says to fill `text_render` only when the user supplied the exact wording.
- License: CC BY-NC 4.0 (commercial use needs a Bria agreement).

## Swarm tag compatibility (per `_swarm-syntax.md`)
- `<weight>` and legacy `(x:1.2)`: not meaningful here (LLM encoder, blunt multiplier at best, and JSON would break). Do not emit.
- `<break>`: CLIP-only; do not emit. Regions: not stated for this model; prefer `<bbox>` or words. Embeddings: do not emit.
- `<bbox[x1,y1,x2,y2]:text>` (fractions 0..1): core replaces the tag in place with one compact object `{"description":text,"bounding_box":[x,y,w,h]}` where values are percentages (0-100, one decimal), top-left origin. Core adds no `objects` wrapper; the writer must place the tag inside an `objects` array.
- `<lora>`, `<random>`, `<wildcard>`: keep only inside string values and only if they survive JSON escaping.

## Examples (our own wording; untested on a real model)
"a lighthouse at dusk":
```json
{"short_description":"A white lighthouse on a rocky headland at dusk under a violet sky.","objects":[{"description":"A tall white lighthouse with a red lantern room, lit window glowing.","location":"center-right","relative_size":"large within frame"}],"background_setting":"Dark sea with long swells, distant fading horizon.","lighting":{"conditions":"low dusk light with a warm lantern glow","direction":"from the west","shadows":"long and soft"},"aesthetics":{"composition":"vertical, lighthouse on the right third","color_scheme":"violet, deep blue, warm amber","mood_atmosphere":"quiet and lonely"},"photographic_characteristics":{"depth_of_field":"deep","focus":"sharp on the tower","camera_angle":"slightly low","lens_focal_length":"35mm"},"style_medium":"photograph","context":"A fine-art landscape photograph.","artistic_style":"realistic"}
```
Short text form: `{"short_description":"A chalkboard menu outside a cafe.","text_render":[{"text":"SOUP OF THE DAY","location":"top of the board","color":"white","font":"hand-drawn chalk lettering"}]}`
Plain short form (trained, weaker control): `A red kayak on a still alpine lake at sunrise.`

## Style file hints
- format: json
- negative: none
- supports: weights=no, break=no, regions=unknown, boxes=yes (core format registered; schema fit unconfirmed), text-rendering=yes

## Open questions
- Core mismatch: core emits `{"description","bounding_box":[x,y,w,h]}` in 0-100 percent. The official Fibo 2.0 sources (diffusers page, card, fal schema) never show `bounding_box` and describe placement with `location` / `relative_size`. docs.bria.ai/vgl shows `[x, y, w, h]` "normalized" with no stated range, so 0-1 versus 0-100 is unconfirmed. Whether Fibo 2.0 reads numeric boxes at all is not stated. Prefer `location` words until tested. Also, core's element has no `location`.
- Which VGL version Fibo 2.0 was trained on (key order, `color_scheme` vs `color_palette`).
- Token limit for Fibo 2.0 captions; behaviour of a free-text negative; best steps/CFG (official 30-50 at 5 vs Swarm 8 at 1).
- Whether numeric colors (RGB lists) help in this dialect.

# Ming Image prompting guide

Status: P0 reference. Sources retrieved 2026-11-19, obtained as tool-generated summaries (not literal page text); verify field names against the repo before shipping a validator.
- Official: https://github.com/inclusionAI/Ming-Image (README, `assets/t2i_rewriter_system_prompt.txt`, `assets/t2i_four_seasons_cabin_prompt.json`)
- Official: https://huggingface.co/inclusionAI/Ming-Image-0.1-Design (model card)
- Official licence file: https://raw.githubusercontent.com/inclusionAI/Ming-Image/main/LICENSE (MIT)
- Core: SwarmUI `src/Text2Image/T2IBoundingBoxFormat.cs` (`ming-image`), `T2IPromptHandling.cs`, `docs/Model Support.md`.

## Swarm class ids
`ming-image` (Ming-Image-0.1-Design, default 2048x2048, supports alpha/transparency). The companion Design-Layer (layer decomposition) model is not supported by Swarm.

## Text encoder
Not named in the official README or card (it mentions an LLM component only). Swarm's loader fetches a file named `ming_image_0.1_ling_mini_2.0_int8_convrot`, so it is an LLM encoder (Ling family) per Swarm, not per upstream docs. Implication: no CLIP chunking and no CLIP-style weighting.

## Prompt format
- Accepts raw text or a JSON file. The README recommends a Figma-style structured prompt for text-to-image. Prompt enhancement runs outside the model, using Ling-3.0-flash-VL or qwen3.8-27B with the repo's rewriter system prompt.
- Structured prompt (from the rewriter and the sample file): one JSON object with exactly two top-level keys in this order, `canvas_settings` then `layers`.
  - `canvas_settings` has exactly `aspect_ratio`, `ambient_lighting`, `image_style` (strings). The sample writes `aspect_ratio` as a ratio plus pixel size in one string, e.g. `1:1, 2048 × 2048 px`; the rewriter says the ratio should match the chosen size.
  - `layers`: ordered from background to topmost overlay. One layer is one selectable visible group (background, a person, an object, a panel, a card, a row, a text block); use the fewest layers that keep the layout; no invisible parents, placeholders or empty layers.
  - Each layer has exactly four keys, in order: `description`, `coordinates`, `hierarchy_and_relation`, `color_specs`.
  - `coordinates`: one string (never an object or array) shaped like `cx: 0.500, cy: 0.500, w: 1.000, h: 1.000`; normalized 0..1 centre and size; the box must fully enclose the element and stay inside the canvas.
  - `hierarchy_and_relation`: only ownership, alignment, containment, stacking, occlusion (free text).
  - `color_specs`: array of hex strings (the sample uses `#RRGGBB`, around ten per layer).
- Length: the rewriter asks for one precise caption but gives no word limit. Token limit: not stated.
- Language: the rewriter prompt and examples are English; user text keeps its own language. Supported languages for the main model: not stated.

## What works / does not
- Built for text-heavy design: UI, infographics, posters, multi-panel layouts.
- Text rendering: each user-supplied string must appear exactly once across all `description` fields, character for character and contiguous (unless copies are requested), and not in `hierarchy_and_relation`. List all intended copy; never hide content behind "etc.". Cards, rows and tables: one layer per card or row with row/column stated, every cell listed.
- Transparency: put exactly one fixed phrase at the very start of the prompt and never combine two. English ones include "RGBA, 4-channel, transparent background" and "cutout PNG, alpha=0 outside the object"; Chinese variants also exist. How the phrase combines with a JSON prompt is not stated. Swarm notes output breaks if the image format is jpg.
- Negative prompts: not mentioned. Treat as unsupported (official CFG is 1.0, which ignores a negative anyway).
- Resolution: 2048x2048 default or 1024x1024 for speed; examples 2560x1440, 2432x1824, 1664x2496; other sizes within 1:4 to 4:1 are accepted and resized. Swarm default 2048.
- Steps/CFG (official): 12 steps, CFG 1.0. Swarm docs agree.
- Quality or style keywords: none documented; style goes in `image_style` / `ambient_lighting` and layer `description`s.
- Licence: MIT (repo LICENSE and model card). Note one README summary stated no licence; the LICENSE file and HF card say MIT.

## Swarm tag compatibility (per `_swarm-syntax.md`)
- `<weight>`, legacy `(x:1.2)`, `<break>`, embeddings: do not emit (LLM encoder, and JSON). Regions: not stated for this model; use `<bbox>`.
- `<bbox[x1,y1,x2,y2]:text>` (fractions 0..1): core replaces the tag in place with one compact object `{"description":text,"coordinates":"cx: 0.500, cy: 0.500, w: 1.000, h: 1.000","hierarchy_and_relation":"","color_specs":[]}` (three decimals). It adds no `layers` wrapper and no `canvas_settings`; the writer must place tags inside a `layers` array and order them background first.
- `<lora>`, `<random>`, `<wildcard>`: keep only inside string values if they survive JSON escaping.

## Examples (our own wording; untested on a real model)
"poster for a jazz night, title at the top":
```json
{"canvas_settings":{"aspect_ratio":"2:3, 1664 × 2496 px","ambient_lighting":"flat, even studio light","image_style":"Bold mid-century screen-print poster with limited inks."},"layers":[{"description":"Deep navy background with a subtle halftone texture.","coordinates":"cx: 0.500, cy: 0.500, w: 1.000, h: 1.000","hierarchy_and_relation":"Bottom layer covering the whole canvas.","color_specs":["#14213D","#FCA311"]},{"description":"A saxophone silhouette in warm orange with thin cream highlights.","coordinates":"cx: 0.500, cy: 0.560, w: 0.600, h: 0.600","hierarchy_and_relation":"Centered below the title, above the background.","color_specs":["#FCA311","#F5E6C8"]},{"description":"Large condensed sans-serif capitals reading \"JAZZ NIGHT\" in cream.","coordinates":"cx: 0.500, cy: 0.120, w: 0.800, h: 0.140","hierarchy_and_relation":"Top of the canvas, above the saxophone.","color_specs":["#F5E6C8"]}]}
```
Transparent sticker (raw text): `RGBA, 4-channel, transparent background. A smiling cartoon avocado sticker with a thick white outline.`

## Style file hints
- format: json
- negative: none
- supports: weights=no, break=no, regions=unknown, boxes=yes (core format registered), text-rendering=yes

## Open questions
- Core cross-check (element level): the keys, their order (`description`, `coordinates`, `hierarchy_and_relation`, `color_specs`) and the `cx/cy/w/h` string shape match the rewriter. Core always leaves `hierarchy_and_relation` empty and `color_specs` empty; whether empty values hurt quality is not stated. The wrapper (`canvas_settings`, `layers`) is not built by core. Core's `F3` formatting may be culture dependent (decimal comma); not checked.
- Core validates 0..1 and min<max but not the rewriter's rule that boxes enclose the object; layer order is the author's job.
- Text encoder identity; accepted languages; length/token limits; whether plain text works as well as JSON; how the RGBA phrase combines with JSON.

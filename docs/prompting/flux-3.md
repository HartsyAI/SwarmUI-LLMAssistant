# Flux.3 Image Klein prompting guide

Status: sources retrieved 2026-11-19 and mostly official; fetched as summaries by a small model, so verify field names against the HF repo before a validator ships. BFL's prompting guides on docs.bfl.ml are written for "FLUX 3 Image" (the API flagship); Klein is the open-weights sibling and the Klein pages say it shares the bounding-box feature, so the guides are applied to Klein here (flagged where that is an inference). Sources:
- Official: https://huggingface.co/black-forest-labs/flux-3-image-klein (model card)
- Official: https://huggingface.co/black-forest-labs/flux-3-image-klein-pup (prompt upsampler card) and its `system_prompts.json` (keys `t2i`, `i2i`, `t2i_bbox`, `i2i_bbox`)
- Official: https://docs.bfl.ml/flux_3/flux3_image_klein.md , https://docs.bfl.ml/flux_3/flux3_image_bounding_boxes.md , https://docs.bfl.ml/guides/prompting_layout.md , https://docs.bfl.ml/guides/prompting_unified_basics.md , .../prompting_unified_style.md , .../prompting_unified_technical.md , .../prompting_summary.md
- Core: SwarmUI `src/Text2Image/T2IBoundingBoxFormat.cs` (`flux-3`), `T2IPromptHandling.cs` (`ProcessPromptForModel`), `docs/Model Support.md` (Flux 3 Image)
- The reference inference code (`flux3/bbox.py` per the upsampler card) was not retrievable; no GitHub repo found.

## Swarm class id

`flux-3-image-klein` (short code Fl3K; model class `flux-3-image-klein`; box format id `flux-3`). Variants: distilled ("sd") and Base, bf16/int8/nvfp4 files.

## Text encoder

Qwen3-VL-4B-Instruct (HF card), an LLM/VLM encoder: prose with relationships is understood; no CLIP chunking. VAE is the Flux.2 VAE (Swarm docs). Token limit: not stated.

## Prompt format and length

- Klein was trained on full scene captions. The card says short prompts give generic output and to "always run a prompt upsampler"; the recommended one is the separate 9B `flux-3-image-klein-pup`, run by default in BFL's API, reference code, Diffusers and ComfyUI. Any LLM with a suitable system prompt can substitute (card). This is exactly the LLMAssistant's job: rewrite into a full caption.
- The plain-caption system prompt (`t2i`) asks for one English prose paragraph, roughly 100-350 words (longer for complex scenes), no markdown or quotes, opening with medium and style, then scene, lighting, camera, and each subject with concrete colours, materials and states; definite tone, no hedging, no praise words, no "this image shows". Default style when none is requested: an actual photograph. Paraphrased from the file.
- BFL basics page: describe only what is visible; avoid "beautiful"/"masterpiece"; quote text to render; use `\n` for line breaks.

## Bounding-box (layout) format

The box feature is real and official. There is no separate box parameter; the layout is part of the prompt string.
- Prompt = global caption + single space + a JSON array, all on one line (`prompt = caption + " " + json.dumps(rows)`). The caption names each element at first mention with an angle-bracket id, e.g. `<person_1>`; every `<id>` must match an `id` in the array, and only table ids are cited. The caption carries no coordinates (BFL layout guide, `t2i_bbox`).
- Row keys in this order: `id`, `bbox`, `desc`.
  - `id`: bare class noun plus index numbered left-to-right then top-to-bottom, starting at 1 (`person_1`, `dog_2`). Text blocks: two-letter language code + `_Text_` + index (`En_Text_1`).
  - `bbox`: `[top, left, bottom, right]` = `[y_min, x_min, y_max, x_max]`, integers 0-1000, origin top-left, clamped to the border. Full frame is `[0,0,1000,1000]`. The grid stretches with the aspect ratio (on a 2:3 canvas a square needs 150 units of width per 100 of height), so fix the aspect ratio before placing boxes.
  - `desc`: one paragraph on appearance (material, colour, pose, expression), no position. Text rows hold the full quoted wording plus typography, `\n` for line breaks.
- One row per distinct object, person, animal, salient region and text block; none for style/lighting/atmosphere. Many interchangeable items (~10+) become one collection row with a count and arrangement. Overlap is expected for overlapping objects. No stated maximum number of rows (a doc example uses 32).
- Boxes guide placement and scale; they are not clipping masks. Very small boxes (~40x25 px) often fail to render new elements; leave room for text. Add a small margin around text boxes. If something lands wrongly, check id matching, y-before-x order, and also describe the position in words in the caption.
- Edit mode (reference images, `i2i_bbox`): instruction refers to the source as `<ref_image_0>`; rows use `id`, `from`, `src_bbox`, `tgt_bbox`, `desc`. Keep: src=tgt. Move: tgt differs. New: `from` and `src_bbox` null. Remove: `tgt_bbox` null.

## What works and what does not

- Negative prompts: no field exists in the API and none is documented; describe the wanted scene instead and name replacements positively (BFL technical page). Base variant uses CFG 3.5 (card) but BFL gives no negative-prompt guidance.
- Weights: not documented; LLM encoder means a blunt multiplier at best. Do not emit.
- Text rendering: quote exact words, state placement, weight, case, colour, font character; one row per text block in layout prompts; proofread results. Non-English text can be quoted directly (BFL style page).
- Resolution: model card says 1024x1024 example; BFL docs: Klein max 4K; API sides 256-6256 (FLUX 3 Image), Swarm docs say 128-3072 works. Iterate small (768sq) then raise; changing size changes the composition.
- Steps/CFG: distilled 16 steps, CFG 1; Base 50 steps, CFG 3.5 (card). Swarm docs: distilled can go to 4 steps with quality loss.
- Up to 10 reference images, referred to as "image 1", "image 2", with roles stated.

## Swarm tag compatibility and an important mismatch

- Core compiles `<bbox[x1,y1,x2,y2]:desc>` (fractions 0-1, x first) in place to a single JSON object `{"id":"object_N","bbox":[y1,x1,y2,x2] (0-1000),"desc":"..."}`. It does NOT collect rows into an array, does NOT append them at the end of the prompt, and does NOT add `<object_N>` references to the caption. The result differs from BFL's documented layout structure. Recommended: the Flux.3 style should emit the complete BFL form (caption with `<id>` cites + space + JSON array) itself rather than rely on `<bbox>` tags. Unverified whether the inline object form works at all.
- Weights, `<break>`: not applicable. Legacy `(x:1.2)`: not parsed for this class. Regions: avoid; use boxes. Embeddings: n/a.
- `<lora:>`, `<random:>`, `<wildcard:>`, `<trigger>`: pass through in the caption text; tags inside `desc` strings need JSON-escape checking (open).

## Examples (our own wording, unverified on a live model)

Plain caption: "Documentary-style photograph of a small bakery at 6 a.m., warm tungsten light spilling from the shop window onto a wet cobblestone street. A baker in a flour-dusted apron stands in the doorway holding a wooden tray of loaves, framed at the left of the image, while a tiled sign above the door reads \"BOULANGERIE\" in cream serif capitals on dark green. Shallow depth of field, cool blue pre-dawn sky."

Layout (single line when sent), canvas 3:2:
`Flat vector poster of a lighthouse on a cliff <lighthouse_1> under a pale yellow sky <background_1>, with the title <En_Text_1> across the top in bold navy capitals. [{"id":"background_1","bbox":[0,0,1000,1000],"desc":"A smooth gradient from pale yellow at the horizon to soft peach at the top, with a faint paper grain."},{"id":"lighthouse_1","bbox":[250,550,900,800],"desc":"A white lighthouse with a red band and a small lit lantern room, standing on a dark rocky cliff, drawn in clean flat shapes."},{"id":"En_Text_1","bbox":[60,100,200,900],"desc":"The words \"NORTH POINT\" in bold navy sans-serif capitals, centered, wide letter spacing."}]`

## Style file hints

```
format: text   # caption, optionally followed by " " + JSON array of {id,bbox,desc}
negative: none
supports: weights=no, break=no, regions=no, boxes=yes(BFL layout form; core <bbox> output differs), text-rendering=yes
```

## Open questions

- Text-encoder token limit and any caption-length cap: not stated.
- Whether Swarm's inline `<bbox>` JSON object (no array, no `<id>` cites) is accepted by the model.
- Exact definition in `flux3/bbox.py` (not retrieved); the summarised schema here is from docs and `system_prompts.json`.
- Whether the Klein distilled model matches FLUX 3 Image in layout fidelity: not stated.
- Escaping rules for Swarm tags inside JSON strings.
- Licence is FLUX Kommunity License v1.0 (non-commercial; small-business carve-out per the card); the upsampler is Apache-2.0. Our own prompts must not copy the BFL system prompts verbatim; this doc only paraphrases their structure.

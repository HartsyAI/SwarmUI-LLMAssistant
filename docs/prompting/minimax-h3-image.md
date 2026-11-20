# MiniMax H3 Image prompting guide

Status: P0 reference (image model only; the video model is covered in `minimax-h3.md`). Sources retrieved 2026-11-19, obtained as tool-generated summaries (not literal page text).
- Official: https://huggingface.co/MiniMaxAI/MiniMax-H3-Image (model card)
- Official: https://huggingface.co/MiniMaxAI/MiniMax-H3-Image-PE (prompt expander)
- Host docs (third party, Runware): https://runware.ai/docs/models/minimax-h3-image/guides/prompting , .../text-and-layouts , .../prompt-optimizer
- Core: SwarmUI `docs/Model Support.md`, `WorkflowGeneratorModelSupport.cs`, `T2IModelClassSorter.cs`.
- No official MiniMax prompting guide for the image model was found; the Runware pages are the best prompting detail and describe the hosted API.

## Swarm class ids
`minimax-h3-image` (and `minimax-h3-image/vae`), default 2048x2048, resolution precision 32. Not the video class `minimax-h3`.

## Text encoder
The full Qwen3-VL text encoder from MiniMax-H3, unchanged; the DiT reads its 50th-layer hidden states (card). Swarm downloads a Qwen3 VL 32B fp4 build (Swarm docs). LLM/VLM encoder, so no CLIP chunking or CLIP-style weighting.

## Prompt format
- The card says the model "expects a detailed English description of the whole image". Prompts are used as given and truncated at 4096 tokens (card; Runware says the same for the part actually read, though its field accepts 32,000 characters).
- Short or non-English prompts are meant to be expanded first: the MiniMax API uses an H3-Image-Context-IR step; locally there is the MiniMax-H3-Image-PE expander (T2I only), which outputs JSON with `expanded_caption` and `aspect_ratio` under a schema. Whether Swarm runs this expander: not verified (Swarm docs do not mention it), so assume the checkpoint receives the prompt as written and write the full description ourselves.
- Reference images: refer to uploads as `<Picture 1>`, `<Picture 2>` ... in upload order, up to 16 (card). Swarm docs also mention `<Subject 1>`; that syntax is not on the image card (it appears in video-model discussions).

## What works / does not
- Structure (Runware, third party): start with the purpose and where it will be used, then layout (how many subjects, where, facing direction, empty areas), then materials, lighting and detail; put exclusions last as positive phrasing ("no logos").
- Write counts as numbers ("six desks in two rows of three"); place things relative to the frame ("right half", "upper third"), not relative to a subject's left or right; state facing direction explicitly; ask for specific empty space when text will be added later.
- Describe visible qualities (light direction, finish, material) rather than words like "premium"; avoid contradictory requirements.
- Text rendering (Runware): put exact text in quotation marks and give its position; one clause per text element with relative size and weight; state line breaks; supply facts if the model should write copy. Infographics: units for every value, time range in the title; bar heights may be approximate. Prompts without quoted text tend to give garbled lettering when the optimizer is off.
- Negative prompts: not supported (per the card as summarized, API requests containing one are rejected, and guidance is off at 6 steps). In Swarm, keep the negative empty and put exclusions in the positive text.
- Aspect ratio: chosen from the prompt by the expansion step; with fixed sizes, name the orientation anyway.
- Resolution (official): works from about 4 MP to 20 MP, best around 16 MP; sizes from 4096x4096 to 6272x2688 listed. Swarm docs: 2048 default (the VAE is a 16x downscale, so 2048 is like 1024 elsewhere), usable from about 768 up to 4096.
- Steps/CFG (official): 6 steps, no guidance (omit or 1). Swarm docs agree.
- Layout/boxes: no JSON box format documented; describe placement in words.
- Licence: MiniMax-H3-Image Community License Agreement; section IV needs separate written authorization for some uses (check before commercial use).
- Section 7 (video conventions): not applicable to this image model.

## Swarm tag compatibility (per `_swarm-syntax.md`)
- `<weight>`, legacy `(x:1.2)`: not stated for this model; likely blunt or ignored. Do not emit. `<break>`: CLIP-only. Embeddings: do not emit. Regions: not stated.
- `<bbox>`: no registered format for this class, so core would use the generic fraction JSON; do not use. Describe positions in words.
- `<Picture N>` looks like a Swarm tag but is a model reference token: it must survive any rewrite unchanged, and the number must match a real prompt image.
- `<lora>`, `<random>`, `<wildcard>`: normal Swarm behaviour; resolve or keep as the user intended.

## Examples (our own wording; untested)
1. Product shot: `A studio photograph of a matte black ceramic coffee mug centered in the lower two thirds of a square frame, handle pointing right, resting on a pale gray seamless surface. The upper third is empty. Soft window light from the left, gentle shadow to the right, subtle steam rising. No text and no logos.`
2. Text and layout: `A vertical café menu board. The title "MORNING MENU" in large bold cream capitals across the top of a dark green chalkboard. Below it, three lines left-aligned: "Espresso  3.00", "Flat White  3.80", "Croissant  3.20", in smaller hand-lettered chalk. A thin chalk border frames the board.`
3. Edit with references: `Place the sneaker from <Picture 1> on the marble shelf from <Picture 2>, keep the sneaker's colors and laces unchanged, and match the shelf's soft overhead lighting.`

## Style file hints
- format: text (long English prose)
- negative: none
- supports: weights=no, break=no, regions=unknown, boxes=no, text-rendering=yes (quote exact text and position it)

## Open questions
- Whether Swarm runs the prompt expander, and what happens to the prompt when it does not: not verified; the card says the model expects detailed English.
- Weighting, regions, and legacy `(x:1.2)` behaviour; supported in-image languages (card does not say).
- Swarm docs say 20B while the card gives a 33.1B DiT; encoder size is not given on the card.
- Runware's guidance describes the hosted optimizer path; how much of it holds for the raw checkpoint is not stated.

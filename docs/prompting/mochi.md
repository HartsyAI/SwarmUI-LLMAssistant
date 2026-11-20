# Genmo Mochi 1 prompting guide

Status: **no official prompting guide exists.** The official README and model card contain no prompting section; only facts from examples and the architecture table. Everything about style is community-sourced or inferred and marked as such. Sources (retrieved 2026-11-19):
- Official: https://github.com/genmoai/mochi (README)
- Official: https://huggingface.co/genmo/mochi-1-preview (model card)
- Official docs (Hugging Face): https://huggingface.co/docs/diffusers/main/api/pipelines/mochi
- Core/Swarm notes: SwarmUI `docs/Obscure Model Support.md` (Mochi section), `src/Text2Image/T2IModelClassSorter.cs`
- Community (low confidence): tutorial blogs surfaced by search (runpod, ori, codersera, filmora); one cites a "keep under 150 tokens" figure that is unsourced, not used here.

## Swarm class ids
`genmo-mochi-1` (compat `genmo-mochi-1`, short code Mochi, text-to-video only), `genmo-mochi-1/vae`.

## Text encoder and what it implies
- A single T5-XXL (README: "simply encodes prompts with a single T5-XXL language model"; Diffusers docs name google/t5-v1_1-xxl).
- Implication: natural-language sentences, no CLIP tag lists. Per `_swarm-syntax.md`, `<weight>` does not work very well on T5 models; do not emit weights or `<break>`.

## Prompt format, length, limits
- Plain English prose. The model card says nothing about style; its example prompts are one or two descriptive sentences, for example a close-up of a chameleon's eye with colour-changing skin, with "Ultra high resolution 4k" appended.
- Token limit: 256 text tokens (README architecture table; Diffusers `max_sequence_length` default 256). That is roughly 150-190 English words; the conversion is our estimate, not stated. Keep prompts well under it; anything beyond is truncated by the pipeline (inferred from the parameter).
- Recommended length: not stated. Community advice: concrete, specific, focused on the moving elements.

## What works and what does not
- Negative prompt: supported by the pipeline (CFG), but official examples leave it empty (`negative_prompt=""`). No guidance on contents; not stated. Safe default: empty.
- Text rendering: not stated; do not rely on it.
- Audio: none, video only.
- Resolution: official 480p (example 480 x 848). Swarm default 848x480.
- Frames and fps: Swarm docs: frame count up to about 200 in multiples of 6 plus 1 (6x temporal VAE compression), trained at 24 fps (Swarm doc). Official examples differ: 84/85/163 frames and 30 fps export; 31 frames in the API example. Treat Swarm's 6n+1 rule as authoritative for Swarm.
- Guidance: Diffusers default 4.5; example 3.5; README example uses cfg 6.0 with 64 steps; Swarm docs say standard values like 7. Community: above about 7 may over-saturate (low confidence).
- Style: optimized for photorealism; performs poorly on animated content (official limitation). Extreme motion can show warping (official).

## Swarm tags
- Safe: `<lora:>`, `<trigger>`, `<wildcard:>`, `<random:>`, `<preset:>`, `<param:>`.
- Do not emit: `<weight>` (T5), `<break>`, `<region>`, `<object>`, `<bbox>`, `<alternate>`/`<fromto>` (no benefit expected, not stated).
- `<video>`, `<videoswap>`, `<extend:frames>`: Mochi is text-to-video only in Swarm, so there is no image-to-video stage for these to target. If a user selects Mochi as the main model, the whole scene goes in the plain prompt. If Mochi is somehow used as a video model on an image, behaviour is not stated.

## Motion, camera and shot language
- No official vocabulary. Official examples are short single-shot descriptions. Community advice that works with any T5 video model: state one subject, one action, one camera move (aerial shot, close-up, tracking), lighting and setting, in present tense.
- Official line: the model "focuses on prompt adherence and motion quality" (Diffusers docs). No claim about multi-shot prompts; avoid cuts and scene changes.
- Duration: about 2-8 s depending on frame count (frame count / fps; our arithmetic from the Swarm doc).
- Prompt extension/rewriting: no official tool or system prompt. Our assistant may expand terse input to 2-4 descriptive sentences within the 256-token limit.
- VAE decoding is very memory hungry (Swarm docs), so long clips may need VAE tiling. This is a setup note, not a prompt matter.

## Examples (our own wording)
```
A slow aerial shot over a foggy pine forest at sunrise, golden light breaking through the mist as a flock of birds lifts off from the treetops and wheels across the frame. Photorealistic, natural colours.
```
```
Close-up of a blacksmith's hands hammering a glowing orange blade on an anvil, sparks flying toward the camera, the camera holding steady with shallow depth of field. Warm forge light, realistic.
```
```
A chef flips a pancake in a sunlit kitchen, the pancake spinning in the air and landing back in the pan. Medium shot, static camera, soft morning light.
```

## Style file hints
- format: text
- negative: none (empty is the only documented usage; separate field is technically accepted)
- supports: t2v only, no audio, photoreal bias, 256-token cap, 6n+1 frames

## Open questions
- Any official prompt style, caption format or recommended length: not stated.
- Whether negatives help and what to write: not stated.
- Native fps (Swarm says 24, official examples export at 30): conflicting.
- Behaviour on stylized/animated prompts beyond "performs poorly": not stated.

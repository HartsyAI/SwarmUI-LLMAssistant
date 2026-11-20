# Stable Video Diffusion (SVD) prompting guide

Status: **there is no text prompt to write.** SVD is image-to-video only; the model card states it "cannot be controlled through text." Official guidance on conditioning exists in the model card and Diffusers docs. Sources (retrieved 2026-11-19):
- Official: https://huggingface.co/stabilityai/stable-video-diffusion-img2vid-xt (model card)
- Official docs (Diffusers): https://huggingface.co/docs/diffusers/main/en/api/pipelines/stable_video_diffusion (the fetch redirected; conditioning parameter table came from the fetch tool's own knowledge of `StableVideoDiffusionPipeline`, not from the page, so verify against Diffusers before relying on defaults)
- Core/Swarm notes: SwarmUI `docs/Obscure Model Support.md` (Stable Video Diffusion), `src/Text2Image/T2IModelClassSorter.cs`

## Swarm class ids
`stable-video-diffusion-img2vid-v1` (also detected as `stable-video-diffusion-img2vid-v0_9`; a `/tensorrt` variant exists). Compat id `stable-video-diffusion-img2vid-v1` (short code SVD, image-to-video only).

## Text encoder and what it implies
- None. SVD conditions on the input image (CLIP image embeddings and VAE latents of the image; Diffusers knowledge, unverified on the fetched page). The text prompt is not read.
- Implication for the assistant: do not rewrite or expand the user's text for SVD. Prompt-side tags such as `<weight>`, `<break>`, `<lora:>` text edits, wildcards, bbox, region have no effect on motion.

## Prompt format, length, limits
- Not applicable. No prompt format, token limit, negative prompt, text rendering or audio.
- Model facts: may produce little or no motion; cannot render legible text; faces and people may be generated poorly (model card).

## What conditions the output
- Input image (the whole content and style of the clip). Swarm: use an Init Image with Creativity 0, or use the Image To Video group; the model cannot be the main text2image model.
- To get text-driven video: generate the first frame with any text-to-image model from the user's prompt (SDXL in Swarm's own example), then animate it with SVD. Swarm docs describe exactly this.

## Swarm tags
- `<video>`: text after `<video>` would be an alternate prompt for the video stage, but SVD ignores text, so it should be empty or omitted. Put the real description in the main prompt for the first-frame model.
- `<videoswap>`, `<extend:frames>`: not stated for SVD; extension relies on image-conditioned continuation and is expected to have quality issues (Swarm says extend is experimental in general).
- Main-prompt tags (`<lora:>`, `<wildcard:>`, `<random:>`) affect only the text-to-image first-frame model.

## Motion, camera and shot language
- No text control. Motion comes from the backend conditioning and the image itself. Camera moves cannot be requested in words.
- Prompt rewriting: not applicable.

## Examples
No prompts apply. A first-frame prompt for the companion text-to-image model, our own wording, is the only useful text:
```
A lone sailboat on a calm sea at sunrise, wide shot, soft pink sky, long clear composition with room for the boat to drift forward.
```
Then animate that image with SVD.

## Style file hints
- format: none (no prompt consumed; if a field is required use text)
- negative: none
- supports: i2v only, no text control, no audio

## Open questions
- Whether Swarm ignores or forwards the `<video>` text for SVD: not stated.

# Kandinsky 5.0 Video (Lite 2B, Pro 19B) prompting guide

Status: **no official prompting guide.** The GitHub README and model cards have no prompt section; the README does state that Video Pro accepts English and Russian prompts. Useful facts come from the pipeline docs (Diffusers) and the technical paper. Sources (retrieved 2026-11-19):
- Official: https://github.com/kandinskylab/kandinsky-5 (README)
- Official: https://huggingface.co/kandinskylab/Kandinsky-5.0-T2V-Lite-sft-5s (model card; example prompts only)
- Official paper: https://arxiv.org/abs/2511.14993 (HTML read only to about section 6; inference sections not read)
- Official docs (Diffusers, written with the authors' citation): https://huggingface.co/docs/diffusers/main/en/api/pipelines/kandinsky5_video
- Core/Swarm notes: SwarmUI `docs/Obscure Model Support.md` (Kandinsky 5), `src/Text2Image/T2IModelClassSorter.cs`

## Swarm class ids
`kandinsky5-video-lite`, `kandinsky5-video-lite/lora`, `kandinsky5-video-pro`, `kandinsky5-video-pro/lora` (compat `kandinsky5-vidlite`, `kandinsky5-vidpro`, both text and image to video; LoRAs do not target the text encoder). The image model (`kandinsky5-image-lite`) is a separate family.

## Text encoder and what it implies
- Dual encoders: Qwen2.5-VL 7B (main, an LLM/VLM) plus CLIP ViT-L/14 giving one pooled embedding of the whole description (paper; Diffusers docs).
- Implication: write natural-language sentences; the LLM encoder reads them semantically. The CLIP branch is only a pooled summary, so CLIP tricks (`<break>`, 75-token chunks, weight syntax) do not apply. Per `_swarm-syntax.md`, `<weight>` is a blunt multiplier on LLM-encoder models; do not emit it.

## Prompt format, length, limits
- Plain prose. Official example prompts are very short ("A dog in red hat"; "A cat and a dog baking a cake together in a kitchen."). The side-by-side benchmarks used expanded, detailed prompts from the Movie Gen benchmark (Diffusers docs), which hints that rich prompts are the intended use of the quality tier (our inference).
- Languages: English and Russian (README).
- Token limits conflict: the paper gives the Qwen branch a maximum context of 256 tokens and CLIP 77 (and CLIP embeds the whole description); the Diffusers pipeline defaults `max_sequence_length` to 512. Not reconciled in the sources; stay near 200 words or fewer.
- Training caption styles (paper): video captions from Tarsier2-7B; SFT image captions exist in five lengths (very long to very short). Whether video SFT has the same ladder: not stated.

## What works and what does not
- Negative prompt: supported (Diffusers pipelines accept `negative_prompt`, ignored when guidance_scale is below 1). Official example: "Static, 2D cartoon, cartoon, 2d animation, paintings, images, worst quality, low quality, ugly, deformed, walking backwards". On the CFG-distilled variants ("nocfg" and distilled) a negative has no effect (our inference).
- Text rendering: the paper says SFT curation targeted text clarity, mainly for images; video text quality not stated. The Pro i2v example asks for a sign with a word on it, but results are not discussed.
- Audio: none, video only.
- Quality/style keywords: none documented. The sample negative suggests the model leans toward static or cartoon output unless motion and realism are stated (inference, low confidence).

## Swarm tags
- Safe: `<lora:>`, `<trigger>`, `<wildcard:>`, `<random:>`, `<preset:>`, `<param:>`.
- Do not emit: `<weight>`, `<break>`, `<region>`, `<object>`, `<bbox>`, `<alternate>`, `<fromto>`.
- `<video>`: with an image-to-video model selected, text after `<video>` is the prompt for the i2v stage. The Diffusers i2v examples pass a short action-style prompt next to the image (the astronaut example mentions subject, setting, "cinematic shot"). Reasonable guidance: describe the motion and camera, keep subject names consistent with the image. Detailed rules: not stated.
- `<videoswap>`: not stated for Kandinsky (swap stage is for Wan 2.2-style swap models).
- `<extend:frames>`: not stated. Keep each extension prompt to one action.
- T2V prompt: full scene (subject, setting, light, camera, motion). I2V prompt: motion and camera only.

## Motion, camera and shot language
- No official vocabulary. The paper only shows editing instructions of the form "Change the camera view to a wider shot" and scores camera movement and object movement in data filtering, so describing both explicitly is sensible (inference).
- Single-take clips; avoid multi-shot prompts (not stated, inferred from clip structure).
- Rewriting: no official prompt expander is documented. Our assistant can expand terse input to 2-5 sentences covering subject, action over time, setting, light and camera, then stop under about 200 words.

## Examples (our own wording)
Text-to-video:
```
A red fox trots through fresh snow at dawn, breath steaming in the cold air, pausing to look toward the camera. The camera tracks alongside at low height, pale pink light on the horizon, realistic fur detail.
```
Image-to-video (after `<video>`):
```
The fox lowers its head, sniffs the snow, then leaps forward out of frame as the camera slowly pans right.
```

## Style file hints
- format: text
- negative: separate (short defect list; skip for nocfg/distilled)
- supports: t2v, i2v (Pro and Lite 5 s), English and Russian, no audio

## Open questions
- Official prompt style, recommended length, token limit (256 vs 512 conflict): not stated.
- Any official prompt expansion step at inference: not documented (paper's inference sections not read).
- i2v prompt guidance: not stated. Video text rendering: not stated.

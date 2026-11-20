# HunyuanVideo (v1), SkyReels and I2V variants prompting guide

Status: P0 reference, video family. The official repo has a prompt-rewrite mechanism and sampling settings but no written prompt-writing guide; the I2V repo has one short tip; SkyReels has one format rule. Sources (retrieved 2026-11-19; read through a summarizer, so verify numbers before shipping a validator):
- Official: https://github.com/Tencent-Hunyuan/HunyuanVideo (README; `hyvideo/prompt_rewrite.py`)
- Official: https://github.com/Tencent-Hunyuan/HunyuanVideo-I2V (README)
- Official: https://github.com/SkyworkAI/SkyReels-V1 (README)
- Official-adjacent: https://huggingface.co/docs/diffusers/api/pipelines/hunyuan_video (encoders, 256-token default, default prompt template)
- Core: SwarmUI `docs/Video Model Support.md`, `docs/Features/Prompt Syntax.md`, `src/Text2Image/T2IModelClassSorter.cs`
- Newer model: see `hunyuan-video-1_5.md` (different encoders and a real handbook).

## Swarm class ids

- `hunyuan-video` (T2V, ~12-13B), `hunyuan-video/lora`, `hunyuan-video/vae`
- `hunyuan-video-i2v` (v1, "concat" guidance), `hunyuan-video-i2v-v2` ("Fixed", "replace" guidance)
- `hunyuan-video-skyreels` (T2V finetune), `hunyuan-video-skyreels-i2v`
- Compat class `hunyuan-video` (also covers FastVideo/FastHunyuan checkpoints, which Swarm does not auto-detect or auto-tune). Hunyuan Video can also make single images with 1 frame (Swarm docs).

## Text encoders

- A decoder-only multimodal LLM (Diffusers: Llava-Llama-3-8B) with a bidirectional token refiner, plus CLIP-L (clip-vit-large-patch14) for a pooled vector. Official README describes the MLLM encoder; the CLIP-L detail is from the Diffusers pipeline.
- The LLM sees the prompt wrapped in a fixed system-style template that asks for a description of content and theme, objects, actions and events, background, light, style, atmosphere, and camera angles and movements (Diffusers default template; wording not reproduced here). Plain descriptive prose therefore fits.
- Implication: natural sentences; weights are a blunt multiplier at best (`_swarm-syntax.md`) so do not emit `<weight>`; `<break>` is CLIP-only, do not emit.
- Token limit: Diffusers default 256 for the LLM prompt. The CLIP branch limit: not stated on the pages read (CLIP-L normally 77).

## Prompt format and length

- Plain text, one paragraph, English or Chinese (the rewriter translates Chinese to English, so English is the safe default).
- Length: the original README states no limit. The Diffusers 256-token default suggests keeping the prompt well under about 200 words; anything past it may be truncated (inference, not stated).
- Official rewriter modes (Hunyuan-Large fine-tune, weights `tencent/HunyuanVideo-PromptRewrite`): "Normal" improves comprehension of intent; "Master" enriches composition, lighting and camera movement. Both keep all the user's information including style words, translate to English, may infer missing style, lighting or atmosphere; Normal expands a one-or-two-word input into one or two short sentences of visual traits. No output structure, element order or camera vocabulary was found in those templates.

## What works and what does not

- Negative prompt: not covered by the official README. The model uses embedded (distilled) guidance, Swarm sets CFG to 1 and uses Flux Guidance around 6, so a negative prompt has no effect in the normal setup. Diffusers only applies a negative when `true_cfg_scale` > 1. Treat as unsupported.
- Text rendering: not stated. Do not promise legible text.
- Resolution (official, 129 frames): 720p tier 1280x720, 720x1280, 1104x832, 832x1104, 960x960; 540p tier 960x544, 544x960, 832x624, 624x832, 720x720. Swarm: trained at ~720p area, 848x480 works with quality loss.
- Frames: official default 129 (about 5 s); must be 4n+1 (Swarm). FPS: not stated in the README; Swarm says trained at 24 and not changeable (changing it gives slow motion). Shorter clips are fine (25, 49, 73, 97, 121 per Swarm); 201 reportedly loops.
- Quality keywords: not documented. Embedded CFG default 6.0 (flow shift 7.0).

## I2V variants

- Official I2V repo tip: "use concise prompts" covering the main subject, the action, and optionally the background and camera angle. Long detailed prompts can cause unwanted transitions. Up to 720p and 129 frames. The `--i2v-stability` flag with flow shift 7 gives steadier video; without it, flow shift 17 gives more dynamic video (a sampler choice, not a prompt matter).
- Image is the first frame. The prompt should describe the change: what the subject does, camera move. Do not redescribe the image.
- No negative prompt option is documented.

## SkyReels V1

- Finetune of HunyuanVideo on film clips, T2V and I2V. Official rule: start the prompt with the fps tag, for example `FPS-24, ` then the description (trained with fps control). Keep that literal prefix when rewriting; do not wrap or move it. No other tags documented. Default 544x960, 97 frames, 24 fps; 960x544 and 720x720 recommended. Guidance 6.0 in the official example. The README's prompt-tips section is an unfinished TODO.

## Swarm tag compatibility

- Keep `<lora:>`, `<trigger>`, `<random>`, `<wildcard>`, `<setvar>`, `<param>`, `<preset>`. Never emit `<weight>`, `<break>`, `<region>`, `<object>`, `<bbox>`, `<segment>`.
- `<video>`: for I2V stages, text before it goes to the image model, text after it to the video model; write the latter as motion only. `<videoswap>`: only used by swap-model pairs like Wan 2.2; not applicable to Hunyuan. `<extend:N>`: experimental continuation segments, write the next action only.
- Flux Guidance and CFG are parameters (`<param[...]:...>`), not prompt text.

## Motion, camera, duration

- Camera vocabulary is not defined by the v1 sources. Use ordinary terms (static, push in, pull back, pan, tilt, orbit, tracking). One move per clip.
- Keep the action short enough to fit the clip (about 5 s at 129 frames); one main action.

## Examples (our own wording, to be checked on a real model)

T2V: "A ceramic artist shapes a tall vase on a spinning wheel, wet clay climbing between her palms. Close-up on the hands, then a slow pull back to show the sunlit workshop. Warm window light, realistic, shallow depth of field."

I2V: "The horse lowers its head and begins to graze, tail swishing once. The camera holds still."

SkyReels: "FPS-24, A courier on a bicycle weaves through a rainy street at dusk, neon signs reflecting on wet asphalt, tracking shot from the side."

## Style file hints

- format: text
- negative: none (embedded guidance; separate only if the user runs a real-CFG variant)
- supports: none of weights, break, regions, boxes, text-rendering. SkyReels style needs the literal `FPS-24, ` prefix.

## Open questions

- CLIP branch token limit and whether the 256-token limit includes the template.
- FPS for the official model and I2V (README silent; Swarm says 24).
- Whether Normal/Master rewrite templates have structure beyond what was read.
- Text rendering ability; any negative-prompt effect in real-CFG setups.
- I2V v2 ("Fixed") prompting differences.

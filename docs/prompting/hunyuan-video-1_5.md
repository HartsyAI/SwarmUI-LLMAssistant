# HunyuanVideo 1.5 prompting guide

Status: P0 reference, video family. Official: there is a real Prompt Handbook plus an official rewrite pipeline. Sources (retrieved 2026-11-19; read through a summarizer, so verify numbers before shipping a validator):
- Official: https://github.com/Tencent-Hunyuan/HunyuanVideo-1.5 (README; `hyvideo/utils/rewrite/t2v_prompt.py`, `i2v_prompt.py`)
- Official: https://github.com/Tencent-Hunyuan/HunyuanVideo-1.5/blob/main/assets/HunyuanVideo_1_5_Prompt_Handbook_EN.md (Prompt Handbook, EN)
- Official: https://huggingface.co/tencent/HunyuanVideo-1.5 (model card), https://arxiv.org/abs/2511.18870 (tech report; only the abstract was read)
- Official-adjacent: https://huggingface.co/docs/diffusers/api/pipelines/hunyuan_video15 (encoders)
- Core: SwarmUI `docs/Video Model Support.md`, `src/Text2Image/T2IModelClassSorter.cs`

## Swarm class ids

- `hunyuan-video-1_5` (T2V and I2V; the repackaged 480p/720p, T2V/I2V, distilled and non-distilled files all map here), `hunyuan-video-1_5/lora`
- `hunyuan-video-1_5-sr` (super-resolution refiner, 720p and 1080p; behaves like a basic model used as Refiner)
- Swarm notes both the "T2V" and "I2V" files can do both tasks.

## Text encoders

- Per Diffusers: Qwen2.5-VL-7B-Instruct (main LLM encoder) plus a ByT5-based glyph encoder (`T5EncoderModel` with a `ByT5Tokenizer`); I2V adds a SigLIP vision encoder. The README and card name only "glyph-aware text encoding" and optional byT5 inputs; the Qwen2.5-VL identification comes from Diffusers.
- Implications: natural-language prose, English or Chinese. The glyph path exists for rendering quoted text. Per `_swarm-syntax.md`, LLM-encoder models treat `<weight>` as a blunt, unreliable multiplier; do not emit. No `<break>`.
- Token limit: not stated.

## Prompt format and length

- Plain text paragraph(s). The README says longer, more detailed prompts "significantly improve" results, and rewriting is on by default (`--rewrite false` disables; lower quality without it).
- The handbook sets no word limit, advises concise direct wording, and its samples run from one sentence to several paragraphs.
- Handbook T2V formula: subject, then motion, then scene, with optional shot type, camera movement, lighting, style, atmosphere. For multi-step action: set the scene, list the actions in order, add key details. Use time-order words, concrete physical actions rather than abstract emotion, and directional words (left, right, foreground, background); give each character a distinct cue.
- Official rewriter (T2V) target structure, our wording: one-sentence summary naming all main subjects; subject and scene; appearance details; time-ordered actions; environment; a closing sentence on camera, shot, mood, lighting and style. Rough budget: about 140 words for subject plus action, 70 for background, 140 or fewer for the remaining attributes. Present tense, objective tone, no "this video shows". Default style when none is given: cinematic realistic, mentioned a few times and restated at the end.

## What works and what does not

- Negative prompt: a `--negative_prompt` argument exists (default empty) and the Diffusers pipeline accepts one; the README and handbook give no guidance. Non-distilled models use real CFG (about 6) so a negative can act; CFG-distilled and step-distilled variants run at CFG 1, where a negative has no effect. Swarm: CFG 1 for distilled, normal CFG otherwise. Write negatives as a short comma list if used; effectiveness not stated.
- Text rendering: glyph-aware encoding is advertised. The official rewriter keeps quoted on-screen text verbatim in its own language, in quotes, and never spells it letter by letter. For typography prompts it starts with the text, uses a front or top-down view, centres it, avoids words that invite cropping ("huge", "close-up", "intricate"), and ends by stating the whole text is fully visible. Reliability: not stated.
- Resolution: 480p or 720p base (aspect default 16:9; 16:9, 4:3, 1:1, 3:4, 9:16 in the handbook), 1080p via the SR model. Swarm treats all as 960x960 area (set 640x640 for the 480p models); mixing resolutions is tolerated. The VAE is a 16x16 downsample (Swarm docs).
- Frames: default 121 (5 s), 4n+1; Swarm says up to 241 works, Swarm default 73. FPS 24 (Diffusers export, Swarm). Settings (official table): CFG 6, steps 50, flow shift 5 for 480p, 9 for 720p T2V, 7 for 720p I2V; step-distilled 480p I2V uses 8 or 12 steps at CFG 1.
- Quality keywords: not documented beyond the style list. Handbook style words: photorealistic or cinematic, cyberpunk, pixel art, low-poly 3D, ink wash, watercolour, 2D animation, Ghibli-style, cel-shaded anime, film noir, retro film look; lighting: golden hour, backlight, silhouette, hard or soft light, colour temperature.

## Swarm tag compatibility

- Keep `<lora:>`, `<trigger>`, `<random>`, `<wildcard>`, `<setvar>`, `<param>`, `<preset>`. Never emit `<weight>`, `<break>`, `<region>`, `<object>`, `<bbox>`, `<segment>`.
- `<video>`: in an image-then-video flow, text after `<video>` goes to the video model; write it as motion only (see below). `<videoswap>`: swap-model pairs only (Wan 2.2 style); not applicable here. `<extend:N>`: experimental continuation; write the next action only. The SR model is a Refiner: do not add video text for it beyond what the user wrote.

## T2V versus I2V

- T2V: full scene plus style, per the formula above.
- I2V: the image is the first frame; the handbook centres the prompt on motion (subject motion, scene motion, optional camera movement). The official I2V rewriter: strictly describe what happens, no static re-description of the image, keep the user's camera instruction, never invent camera movement, never add "static camera" unless asked, state where things enter, exit and move, resolve pronouns to concrete entities, do not add key events or lighting not requested. It writes in Chinese by default; English input is accepted by the model (README shows both).

## Motion, camera, shot language, duration

- Handbook camera moves: crane/pedestal up and down, truck or tracking left and right, dolly in and out, tilt, pan, orbit, 360 rotation, follow, static. Shot types: aerial, wide, panoramic, long, medium, medium close-up, close-up, low angle. The T2V rewriter defaults to eye-level with a subtle move (static, push-in, pull-back, pan, dolly) to limit distortion, and caps vague crowd counts at three. Keep to what fits about 5 s.

## Examples (our own wording, to be checked on a real model)

T2V: "A baker in a flour-dusted apron scores a round loaf with a razor, then slides it into a stone oven. Medium shot, slow dolly in on his hands. Warm tungsten light, steam rising, cinematic realistic style."

I2V: "The dancer spins once, her red skirt flaring outward, then settles into a bow. The camera orbits slowly to the left."

Text: "A neon sign on a brick wall reads \"OPEN LATE\". Front view, sign centred and fully visible, the letters flicker once then glow steadily."

## Style file hints

- format: text
- negative: separate (only with non-distilled CFG > 1; none for distilled)
- supports: text-rendering. No weights, break, regions or boxes.

## Open questions

- Token limit, and whether negatives measurably help.
- Reliability of glyph text rendering; exact handbook sample lengths.
- Whether English I2V prompts do as well as the rewriter's Chinese.
- Encoders as named by the authors (the README and card do not name Qwen2.5-VL or the glyph model; only Diffusers does).
- Handling of rewrite-endpoint absence: the repo runs without rewriting if none is configured, so our rewriter fills that role.

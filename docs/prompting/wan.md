# Wan 2.1 / Wan 2.2 prompting guide

Status: P0 reference, video family. The official repos give prompt-extension guidance and a negative prompt, but no written prompt-writing guide in text form (the Wan 2.2 "prompting guide book" linked from Swarm's docs is an image-only DingTalk page that could not be read). Sources (retrieved 2026-11-19; most pages were read through a summarizer, so check field names and schemas against the source before a validator relies on them):
- Official: https://github.com/Wan-Video/Wan2.1 and https://github.com/Wan-Video/Wan2.2 (READMEs; `wan/utils/prompt_extend.py`, `wan/utils/system_prompt.py`)
- Official: https://huggingface.co/Wan-AI/Wan2.2-T2V-A14B , https://huggingface.co/Wan-AI/Wan2.2-TI2V-5B , https://huggingface.co/Wan-AI/Wan2.1-T2V-14B (model cards)
- Official-adjacent: https://huggingface.co/docs/diffusers/api/pipelines/wan (Diffusers docs: UMT5 encoder, 512-token default, negative prompt example)
- Core: SwarmUI `docs/Video Model Support.md`, `docs/Features/Prompt Syntax.md`, `src/Text2Image/T2IModelClassSorter.cs`
- Community (not official): MimicPC, VEED, ImagineArt, Scenario Wan 2.2 prompt guides (found by search, summarized); marked "community" below.

## Swarm class ids

- `wan-2_1-text2video-1_3b`, `wan-2_1-image2video-1_3b`, `wan-2_1-vace-1_3b`
- `wan-2_1-text2video-14b` (the class sorter has no separate Wan 2.2 T2V 14B id, so the 2.2 high/low T2V pair most likely lands here; not confirmed), `wan-2_1-image2video-14b`, `wan-2_1-flf2v-14b`, `wan-2_1-vace-14b`
- `wan-2_2-image2video-14b` (Wan 2.2 I2V high/low pair), `wan-2_2-ti2v-5b` (single dense model, T2V and I2V)
- Compat classes: Wan 2.1 1.3B, Wan 2.1/2.2 14B, Wan 2.2 5B. Text-to-image is also possible (Swarm docs).

## Text encoder

- UMT5-XXL (umt5-xxl; Diffusers `UMT5EncoderModel`), multilingual, T5-style cross-attention. Official: English and Chinese prompts both work.
- Implication: natural prose, not tag lists. Per `_swarm-syntax.md`, T5-based models respond poorly to `<weight>`; do not emit weights. `<break>` is CLIP-only; do not emit.
- Token limit: Diffusers defaults to 512 tokens and truncates beyond that (official Wan code limit: not stated).

## Prompt format and length

- One plain-text paragraph, present tense. No JSON, no tags.
- Official extender targets: T2V roughly 60-200 words (English) and I2V at most ~100 words (see below). Community guides suggest 80-120 words for general use. Treat as ranges, not limits.
- Official README advises enabling prompt extension for richer detail; the benchmarks were run without it.
- Community formula: subject + scene + motion + aesthetic control (light, lens, time of day, colour grade) + style. The Wan 2.2 card says training data carried aesthetic labels for lighting, composition, contrast and colour tone, so naming these is reasonable (official, but only as an implication).

## What works and what does not

- Negative prompt: supported when CFG > 1. The repo's reference negative is a Chinese word list ("bright tones, overexposed, static, blurred details, subtitles, ... three legs, many people in the background, walking backwards" in the English rendering shown in Diffusers docs). Plain comma-separated descriptors, not sentences. With distilled/Lightning LoRAs at CFG 1 the negative has no effect.
- Text rendering: Wan 2.1 card claims Chinese and English on-screen text. Wan 2.2: not stated. Put the literal text in quotes.
- Quality keywords ("masterpiece", "4k"): not documented; the official extender does not add them. Style words go first if used.

## Wan 2.2 high/low-noise design

- A14B models are two experts (~14B each, ~27B total, ~14B active per step): high-noise sets layout early, low-noise refines detail. The switch is by SNR.
- Prompting consequence: write one prompt for both experts. Whether the two stages benefit from different text: not stated. Use `<videoswap>` only if the user explicitly wants stage-specific text or LoRAs.

## Swarm tag compatibility

- Keep `<lora:>`, `<trigger>`, `<random>`, `<wildcard>`, `<setvar>`, `<param>`, `<preset>`; never `<weight>`, `<break>`, `<region>`, `<object>`, `<bbox>`, `<segment>` in the video prompt.
- LoRAs for Lightning-style pairs go in sections: T2V `<base> <lora:HIGH> <refiner> <lora:LOW>`; I2V `<video> <lora:HIGH> <videoswap> <lora:LOW>` (Swarm docs). Do not drop or reorder these.
- `<video>`: with a text-to-image stage followed by I2V, text before `<video>` goes to the image, text after goes to the video model. The rewriter should keep the split and write each half for its job.
- `<extend:N>`: each extend segment is a new I2V continuation; write the next action only (Swarm example: "the cat starts running through the forest"). Experimental in core; expect quality issues.

## T2V versus I2V prompts

- T2V: full scene (subject, setting, action, light, shot, style).
- I2V (including 5B with an image, FLF2V): the image already fixes subject and look. Describe only motion: what moves, how, camera move, pace. Do not re-describe what is visible. The official I2V extender does exactly this and can run from an empty prompt plus the image.

## Motion, camera, duration

- Camera terms from the community: pan, tilt, dolly in/out, orbit, crane, tracking/follow, handheld, static. One community ComfyUI test found orbit moves often ignored; keep to one camera move per clip and state it plainly.
- Keep the action achievable in a short clip; one main action per clip.

## Official prompt extender (format, our wording)

- T2V: acts as a director. Adds shot size, camera angle, composition, light and colour tone (pick a few, default daytime / medium or wide shot / centred), enriches subject and background detail, always includes a described motion process (invents one if missing), forbids mood-and-atmosphere prose, adds no style unless asked, and puts any style first. Output is a single plain paragraph, about 60-200 words, language set by the target-language flag. Quirks: skies rewritten as clear blue; some content replaced silently (do not copy this behaviour).
- I2V: turns a request plus image into a motion-focused description of at most about 100 words, keeps the user's camera instructions, drops static detail already visible, identifies the subject from the image when only an action is given.
- Our rewriter should follow the same shape: one paragraph, motion explicit, camera stated, no atmosphere filler.

## Examples (our own wording, to be checked on a real model)

T2V: "A fisherman in a yellow raincoat pulls a net over the side of a small wooden boat at dawn, grey sea swelling around him. Medium shot, slow push-in. Cool diffuse morning light, gulls wheeling in the background, realistic film look."

I2V: "The woman turns her head toward the window and smiles, hair shifting slightly in a breeze. The camera drifts slowly to the right. Gentle, unhurried motion."

Wan 2.2 14B Lightning pair, T2V: "a red fox trots across fresh snow, tracking shot, soft overcast light <base> <lora:Wan2.2-Lightning_T2V-A14B-4steps-lora_HIGH_fp16> <refiner> <lora:Wan2.2-Lightning_T2V-A14B-4steps-lora_LOW_fp16>" (filenames are the Swarm docs' example).

## Style file hints

- format: text
- negative: separate (only meaningful at CFG > 1; none for distilled/Lightning runs)
- supports: text-rendering (Wan 2.1 stated; 2.2 not stated). No weights, break, regions or boxes.

## Open questions

- Wan 2.2 official guide-book contents (image-only page).
- Whether Wan 2.2 renders text as Wan 2.1 does.
- Whether stage-specific prompts help the two experts.
- Whether the Swarm class sorter assigns the 2.2 T2V high/low pair to `wan-2_1-text2video-14b`.
- Whether the Wan 2.1 and 2.2 extender system prompts differ (only 2.2 was read).

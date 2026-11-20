# MiniMax H3 (Hailuo 3) prompting guide

Status: official guidance exists and is detailed (base-mode and reference-mode writing guides shipped in the model repo). I read the base guide through a fetch tool (summary, not verbatim) and the ComfyUI docs page; the reference-mode guide was not read. Sources (retrieved 2026-11-19):
- Official: https://huggingface.co/MiniMaxAI/MiniMax-H3 (model card)
- Official: https://huggingface.co/MiniMaxAI/MiniMax-H3/blob/main/docs/VIDEO_PROMPT_WRITING_GUIDE_base_en.md (base guide; also on ModelScope, linked from SwarmUI docs). The companion `VIDEO_PROMPT_WRITING_GUIDE_ref_en.md` covers reference mode.
- Official docs (ComfyUI): https://docs.comfy.org/tutorials/video/minimax/minimax-h3-prompt-guide
- Core/Swarm notes (maintainer docs, not MiniMax): SwarmUI `docs/Video Model Support.md` (MiniMax H3), `src/Text2Image/T2IModelClassSorter.cs`
- Community guides (fal, Luma, rundiffusion, etc.) were surfaced by search but not used for facts.

## Swarm class ids
`minimax-h3` (video+audio, compat flags: text, image to video, joint AV latents), `minimax-h3/lora`, `minimax-h3/embedding`, `minimax-h3/vae`, `minimax-h3/audio-vae`, and `minimax-h3-image` (+ `/vae`, an image-only variant). Two checkpoints: FL2VA (text, first-frame, first/last frame to audio+video) and Ref2VA (reference-conditioned, up to 9 images, 3 videos, 3 audio clips per the card; Swarm says images up to 10).

## Text encoder and what it implies
- H3-Encoder = Qwen3-VL-32B, hidden states from layer 50 passed to the transformer; special tokens such as `<d>` are added to the tokenizer (model card).
- Implication: an LLM encoder, so long structured natural language works and short prompts also work (Swarm). Per `_swarm-syntax.md`, `<weight>` is a blunt multiplier on such models and often does nothing; do not emit weights, `<break>`, or CLIP tricks.

## Prompt format, length, limits
- Official structure (three fields, plain text, not JSON):
  1. `integrated_multimodal_description`: the timeline of visuals, camera, actions, spoken lines and sounds the characters make or hear in the scene.
  2. `overall_soundscape`: 1-4 sentences of ambience, action sounds and non-verbal human sounds for the whole clip (`N/A` only for true silence).
  3. `non_diegetic_music`: 1-3 sentences describing score the characters cannot hear, by instrumentation, tempo, rhythm and dynamics, not mood words (`N/A` if none).
- Image tasks (image-to-video, first/last frame, last-frame) start with a fixed alignment line stating which picture matches which timestamp, then a blank line, then the fields. Text-to-video omits it.
- Length: the guide gives no recommended length or token limit (not stated). Swarm: works best with long LLM-style prompts, short ones still fine. Shots may be spaced across the timeline.
- Rewriting: the model card recommends H3-Context-IR, a hosted preprocessor that converts multimodal inputs to this structured form and may fill in missing detail while keeping intent (enhanced prompts go through moderation). Our assistant plays that role locally.

## What works and what does not
- Negative prompt: **not supported.** ComfyUI docs: guidance runs at CFG 1 with no negative branch; describe what should appear, do not list bans. The base guide does not address negatives.
- Text on screen: put the verbatim text in English double quotation marks; do not translate (guide).
- Audio: central. Speech uses `<d>[Language] verbatim line</d>`: only the language tag and the spoken words go inside the tags, identity and action stay outside. Speaker IDs `(S1)`, `(S2)`, groups `(S1,S2)`, kept consistent across shots; establish voice traits (pitch, timbre, pace, accent) at first appearance. Off-screen voiceover is stated explicitly, with the speaker's lips closed. `<scenetrans>` marks a line carried across a cut, `<cutoff>` marks speech cut by the clip end. Do not put dialogue in the soundscape. Swarm warns: a prompt with no audio description gives nonsense audio, and a stray noise at clip start can be tamed by the "Audio Silent Prefix Duration" param (Swarm note).

## Swarm tags
- Safe: `<lora:>`, `<trigger>`, `<wildcard:>`, `<random:>`, `<preset:>`, `<param:>`. Keep the field labels and `<d>`, `<scenetrans>`, `<cutoff>` markers exactly as written; they are model tokens, not Swarm tags (Swarm only parses its own tag names; confirm `<d>` is passed through untouched).
- Do not emit: `<weight>`, `<break>`, `<region>`, `<object>`, `<bbox>`, `<alternate>`, `<fromto>`.
- `<video>`: with FL2VA used as an image-to-video model, text after `<video>` is the stage prompt. Content should be the structured form above, anchored to the supplied first frame: keep identity, clothing and layout consistent, describe forward development, plus audio. For first/last-frame, describe the motion path between the two frames, prefer one shot, and do not restate what the images show (guide).
- `<videoswap>`: not stated for H3. `<extend:frames>`: not stated.
- Ref2VA in Swarm: reference attachments are cited in the prompt as `<Picture 1>`, `<Audio 1>`, `<Video 1>` (Swarm docs). The official reference guide uses `<Subject 1>` style definitions and retention markers (such as `fully_preserved`) and audio markers (such as `partially_copy`, `reference`); details not read here.

## Motion, camera and shot language
- Shots: `[Shot 1]` has no timestamp; later shots start with an increasing cut time `MM:SS.mmm` inside the clip length plus a phrase like "the camera cuts to". Cut only when subject, space, state, viewpoint or time changes; otherwise use camera motion in the same shot. Dissolves, fades, wipes only if requested.
- Camera terms (official): zoom in/out, push in/pull out, pan left/right, truck, tilt up/down, pedestal, arc, tracking, static, shake slightly/strongly, POV, roll; amplitude "small/large", speed "slow/fast"; write them as natural phrases inside the sentence, not trailing labels.
- Styles: cinematic, live-action, 2D animation, 3D CG, claymation, watercolor, vintage film; for image tasks the style comes from the reference image.

## Examples (our own wording)
Text-to-video:
```
integrated_multimodal_description:
[Shot 1] Medium shot, static camera. A baker in a flour-dusted apron kneads dough on a steel counter in a bright morning bakery, steam rising behind her. She looks up and (S1) says warmly, <d>[English] First batch is almost ready.</d>
[Shot 2] At 00:04.000, the camera cuts to a close-up of golden loaves sliding out of an oven, pushing in slowly as crust crackles.
overall_soundscape: Soft kitchen hum, the scrape of a peel on stone, crackling crust.
non_diegetic_music: Gentle acoustic guitar at a relaxed tempo with light brushed percussion.
```
Image-to-video (after `<video>`), alignment line first:
```
Picture 1 (from Shot 1) is fully referenced at 0.00 seconds.

integrated_multimodal_description:
[Shot 1] The woman in the red coat starts walking forward along the platform as the camera tracks backward at a slow speed, her scarf lifting in the wind.
overall_soundscape: Distant train rumble, steady footsteps, wind.
non_diegetic_music: N/A
```

## Style file hints
- format: text (structured plain-text fields; not JSON)
- negative: none (no negative branch)
- supports: t2v, i2v, first/last frame, reference (Ref2VA), audio, dialogue, multishot, image variant

## Open questions
- Whether Swarm inserts the image alignment line for i2v or the user/assistant must: not stated.
- Recommended prompt length or token cap: not stated.
- Whether Swarm tag parsing leaves `<d>...</d>` untouched: not stated here.
- Reference-mode guide fields in full: not read.

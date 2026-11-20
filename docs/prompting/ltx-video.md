# LTX Video prompting guide (LTX Video 1, LTX-2, LTX-2.3, LTX-2.5)

Status: official guidance exists (Lightricks), but the dedicated prompting guide now documents LTX-2.5. Its applicability to LTX-2 and 2.3 is plausible (same family, same authors) but is **not stated** in the pages I could read. Sources (retrieved 2026-11-19):
- Official: https://docs.ltx.io/open-source-model/usage-guides/prompting-guide (LTX-2.5 prompting guide; summarized via fetch tool)
- Official: https://github.com/Lightricks/LTX-2 (README: 200-word advice, Gemma encoder, enhance_prompt)
- Official: https://github.com/Lightricks/LTX-Video (LTX Video 1 README, now legacy) and https://huggingface.co/Lightricks/LTX-Video
- Official model cards: https://huggingface.co/Lightricks/LTX-2 , https://huggingface.co/Lightricks/LTX-2.3 , https://huggingface.co/Lightricks/LTX-2.5
- Official docs (ComfyUI): https://docs.comfy.org/tutorials/video/ltx/ltx-2-3
- Core/Swarm notes (maintainer docs, not Lightricks): SwarmUI `docs/Video Model Support.md`, `src/Text2Image/T2IModelClassSorter.cs`
- Not readable: docs.ltx.video and ltx.io blog guide (HTTP 403 / fetch error); the LTX-2.3 guide is an X post. Their content is not used here.

## Swarm class ids
`lightricks-ltx-video` (LTX Video 1, text and image to video), `lightricks-ltx-video-2` (LTX-2, joint audio+video), `lightricks-ltx-video-2-3`, `lightricks-ltx-video-2-5`, plus matching `/vae` and (LTX-2) `/lora` entries. Compat: `lightricks-ltx-video`, `lightricks-ltx-video-2` (LTXV2, joint AV latents).

## Text encoder and what it implies
- LTX-2.5: custom Gemma 4 12B encoder bundled with a text projection (model card, README). LTX-2.3: Gemma 3 12B (ComfyUI docs, model card). LTX-2: Gemma-family LLM encoder; the LTX-2 card does not name it ("not stated" there).
- LTX Video 1: encoder **not stated** on the fetched pages (commonly reported as T5-XXL; unverified here).
- Implication (LLM-style encoder): natural-language paragraphs, no CLIP tricks. Per `_swarm-syntax.md`, `<weight>` is a blunt multiplier on LLM-encoder models and often does nothing; do not emit weights, `<break>`, `<alternate>`-style tricks, `(word:1.2)`, or comma tag lists.

## Prompt format, length, limits
- One flowing paragraph, chronological, present tense (official guide: roughly 4-8 sentences for a single shot; LTX Video 1 and LTX-2 READMEs: "Keep within 200 words").
- Official content checklist, in order of attention: establish the shot (type/framing), set the scene (place, light, colour, atmosphere), the action over time, the characters (look, clothing, emotion shown through movement), camera movement, audio. Start with the main action in one sentence, then add details.
- Longer screenplay-style prompts (scene headers, character cues, quoted dialogue) are sanctioned for dialogue, multiple beats or precise timing.
- Token limit: not stated. Swarm notes say LTX-2 "really needs long prompts" (maintainer observation, not official).
- Prompts written for other models should be rewritten into this paragraph style, not pasted with their tag syntax.

## What works and what does not
- Negative prompt: the model cards' Diffusers examples ship default negatives (LTX Video 1 example: "worst quality, inconsistent motion, blurry, jittery, distorted"). The prompting guide does not cover negatives. Swarm docs reproduce a very long reference negative for LTX-2. Distilled checkpoints run at CFG 1 (8 steps), where a negative is expected to have no effect (inference, not stated). Write negatives as short quality/artifact phrases, no sentences.
- Text rendering: LTX-2.5 improved short text, but exact spelling and cross-frame consistency are not guaranteed; keep text short and prominent, add titles in post (official).
- Audio (LTX-2 and later): describe ambient sound, music, speech or singing. Dialogue goes in quotation marks; name language/accent; delivery (whisper, announcer, monotone) and volume can be stated. LTX-2 card warns audio without speech may be lower quality.
- Resolution: width and height divisible by 32; frames 8n+1 (9, 17, ... 121, 257). Examples: 768x512 at 121 frames (LTX-2 card); 1216x704 at 30 fps default for LTX Video 1; 24 fps in LTX-2.5 examples; native portrait improved in 2.3 (ComfyUI docs). LTX Video 1 works best under 720x1280 and under 257 frames.
- Complex or chaotic physics causes artifacts; simpler motion is more reliable (official).
- Quality/style keywords: no official keyword list. Prefer concrete film/lighting description over "masterpiece".

## Swarm tags
- `<lora:>`, `<trigger>`, `<wildcard:>`, `<random:>`, `<preset:>`, `<param:>`: fine, they resolve before encoding. LoRAs from LTX-2 may cross-apply to 2.3/2.5 (Swarm docs).
- `<weight>`, `<break>`, `<alternate>`, `<fromto>`, `<region>`, `<object>`, `<bbox>`: do not emit (no CLIP chunking; no JSON box format registered for LTX).
- `<video>`: with an image-to-video model selected, text after `<video>` replaces the prompt for the video stage. For i2v, describe what changes and moves; do not re-describe what the image already shows (Swarm maintainer note; the official guide has no i2v section beyond "prefer one continuous take unless you describe a cut"). Add audio description since LTX-2+ makes sound.
- `<videoswap>`: not stated for LTX (swap stage applies to Wan 2.2-style swap models).
- `<extend:frames>`: Swarm's extend is experimental; keep each segment prompt to one new action continuing the same scene, and choose frame counts of the 8n+1 family.
- Text-to-video prompt: the whole scene including subject, setting, camera and audio. Image-to-video prompt: motion, camera and sound only.

## Motion, camera, duration, extension
- Camera vocabulary (official): follows, tracks, pans across, circles around, tilts upward, pushes in, pulls back, overhead view, handheld, over-the-shoulder, wide establishing shot, static frame. Temporal: slow motion, time-lapse, lingering shot, continuous shot, freeze-frame, fade in/out. After a camera move, describe how the subject looks at the end of it.
- Multi-shot (LTX-2.5): one chronological paragraph, 2-4 shots, name each transition (hard cut, match cut, dissolve), re-establish framing, reuse identifiers for recurring subjects, say whether audio continues. Use a single continuous take for lip-synced dialogue or unbroken camera motion.
- Duration: 24 fps, 121 frames is about 5 s. LTX-2.5 has an optional duration predictor that times the clip to the written action (include pauses in the prompt or set an explicit duration).
- Prompt enhancer (official, own words): an optional LLM rewrite pass run before generation (`enhance_prompt` in pipelines, a Comfy node, Gemma 4 E2B in LTX-2.5; separate system prompts for t2v and i2v). It helps short or foreign-style prompts most. Do not use it on direct API requests. Our assistant should do the same job and Swarm should not double-enhance.

## Examples (our own wording)
Text-to-video:
```
A medium shot of a lighthouse keeper in a yellow raincoat climbing a spiral iron staircase at dusk. He pauses, wipes salt from the lens with his sleeve, then lifts a lantern. The camera tracks upward beside him in a slow handheld move, amber light sweeping the wet walls. Waves boom below, the stairs clang under his boots, and a low wind howls through the tower.
```
Image-to-video (after `<video>`):
```
The woman turns toward the window and smiles slightly, her hair lifting in a breeze. The camera pushes in slowly to a close-up. Soft piano plays, curtains rustle, and she says quietly, "It is finally spring."
```

## Style file hints
- format: text
- negative: separate (short artifact list; ignored at CFG 1 on distilled models)
- supports: t2v, i2v, audio, multishot (2.5), dialogue in quotes, enhancer-compatible

## Open questions
- Whether the LTX-2.5 guide (multi-shot, duration predictor) fully applies to LTX-2 and 2.3: not stated.
- Text encoder of LTX Video 1 and exact name for LTX-2: not stated in fetched pages. Token limit: not stated.
- Official negative-prompt guidance: not stated. Official i2v prompt guidance beyond one-continuous-take: not stated.
- LTX-2.3 official guide (X post) not read.

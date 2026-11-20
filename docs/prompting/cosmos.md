# NVIDIA Cosmos prompting guide (Cosmos 1 Text2World / Video2World, Cosmos Predict2 text-to-image)

Status: partial official guidance. Cosmos 1 model cards give a prompt-length rule and content checklist; the Predict2 text-to-image doc gives prose advice. The upstream Cosmos-Predict1 repo is no longer actively developed (NVIDIA points to Cosmos 3); Swarm still targets Cosmos 1 and Predict2 weights. Sources (retrieved 2026-11-19):
- Official: https://huggingface.co/nvidia/Cosmos-1.0-Diffusion-7B-Text2World and https://huggingface.co/nvidia/Cosmos-1.0-Diffusion-7B-Video2World (model cards)
- Official: https://github.com/nvidia-cosmos/cosmos-predict1 (`examples/inference_diffusion_text2world.md`, `inference_diffusion_video2world.md`)
- Official: https://github.com/nvidia-cosmos/cosmos-predict2 (`documentations/inference_text2image.md`) and https://huggingface.co/nvidia/Cosmos-Predict2-2B-Text2Image
- Official paper: https://arxiv.org/abs/2501.03575 (HTML v3 read for upsampler, T5, caption details)
- Official docs (Hugging Face Diffusers): https://huggingface.co/docs/diffusers/main/en/api/pipelines/cosmos
- Core/Swarm notes: SwarmUI `docs/Obscure Model Support.md` (Cosmos section), `src/Text2Image/T2IModelClassSorter.cs`

## Swarm class ids
Cosmos 1: `nvidia-cosmos-1-7b-text2world`, `nvidia-cosmos-1-14b-text2world`, `nvidia-cosmos-1-7b-video2world`, `nvidia-cosmos-1-14b-video2world`, `nvidia-cosmos-1/vae` (compat `nvidia-cosmos-1`, text and image to video). Predict2 text-to-image: `nvidia-cosmos-predict2-t2i-2b`, `nvidia-cosmos-predict2-t2i-14b` (Swarm flags these text-to-video compat, but they output images). Cosmos Autoregressive and Predict2 video are not covered by Swarm classes.

## Text encoder and what it implies
- T5-XXL (Cosmos 1 paper; Diffusers docs list the t5-11b variant for Cosmos 1 and Predict2 pipelines). Embeddings are zero-padded to 512 tokens in the paper; the Diffusers pipelines default to `max_sequence_length` 512, truncating longer prompts. Swarm docs: it is "old T5-XXL v1", not the one other models share.
- Implication: plain sentences, no CLIP tag lists. Per `_swarm-syntax.md`, `<weight>` works poorly on T5 models; do not emit it or `<break>`.

## Prompt format, length, limits
- Plain text string. Model cards: fewer than 300 words, describing the scene, key objects or characters, background, and any actions.
- Why long prompts: the paper says training captions came from a video language model and differ from human wording; it then provides a prompt upsampler (Cosmos-UpsamplePrompt1-12B, built on Mistral-NeMo-12B-Instruct) to convert short human prompts into that style. Average training caption is about 559 characters (roughly 97 words; our conversion). Swarm docs: Cosmos "responds poorly to standard prompts" and wants long LLM-style prompts (maintainer note).
- The repo's own example is long and concrete: subject, setting, lighting, static camera, shallow depth of field.
- Predict2 text-to-image advice (official, paraphrased): be detailed, stress physical realism and real-world behaviour, name materials, lighting and spatial relations, prefer concrete photographic wording ("natural light", "wide-angle lens") over abstract terms, unless you want surreal output.

## What works and what does not
- Negative prompt: the Cosmos 1 paper says negative prompts work well at inference (they skip random text dropout). The Predict2 doc has a `--negative_prompt` option (default empty) and advises naming undesired elements; the Predict2 card's Diffusers example lists blur, over-saturation, low resolution. The Cosmos 1 cards do not mention negatives. Write negatives as a short list of defects.
- Text rendering, audio: not stated. No audio (video only).
- Guardrails: the pipeline uses a Llama Guard 3 safety model; a rejected prompt's behaviour is not stated.
- Resolution (Cosmos 1): default 1280x704; also 960x960, 960x704, 704x960, 704x1280. Swarm: cannot go below 704x704, 960x960 base square. Predict2 image: aspect ratios 1:1, 4:3, 3:4, 16:9 (default), 9:16 (repo doc); the 2B card states default output 1280x704, while Swarm registers 1024x1024 as standard size.
- Frames and fps (Cosmos 1): 121 frames (5 s) at 24 fps, adjustable fps 12-40; Swarm says effectively fixed at 121 frames. CFG 7 and 35 steps recommended (Swarm doc citing NVIDIA defaults). Very slow models.

## Swarm tags
- Safe: `<lora:>`, `<trigger>`, `<wildcard:>`, `<random:>`, `<preset:>`, `<param:>`.
- Do not emit: `<weight>`, `<break>`, `<region>`, `<object>`, `<bbox>`.
- `<video>`: for Video2World, the image comes from the Image To Video group and text after `<video>` is the video-stage prompt. Official: the text should describe the scene, key objects, background, and actions within the 5-second window. Whether to re-describe the image content: the cards suggest describing the scene, so for Cosmos it is reasonable to keep a full scene description plus the action (differs from Wan-style i2v advice; this is our inference, not stated).
- Text2World: the whole prompt carries everything.
- `<videoswap>`: not applicable. `<extend:frames>`: Video2World can continue from conditioning frames (9 input frames for video input per the card), so extension is technically plausible; Swarm behaviour not stated. Keep each segment prompt to one action.
- Predict2 text-to-image: images only; video tags do nothing.

## Motion, camera and shot language
- No official camera vocabulary. The repo example uses "static camera" and "shallow depth of field". Physical, concrete motion fits the model's physical-AI focus (robots, vehicles, warehouse scenes).
- Single continuous 5-second clip; avoid cuts and scene changes (not stated, inferred from fixed 121 frames).
- Prompt rewriting (official, own words): the Cosmos 1 tools include a Mistral-based upsampler that expands short prompts; in Video2World it can be run with no text prompt and is also separately offloadable or disableable. Our assistant should emulate it: expand to a detailed 80-250 word description, then stop.

## Examples (our own wording)
Text2World:
```
A fixed, low-angle camera in a clean warehouse aisle, overhead fluorescent lights casting even white light. A small yellow forklift drives slowly toward the camera carrying a stack of cardboard boxes on a wooden pallet, its tyres squeaking on polished concrete. In the background, shelves of boxed goods recede into soft focus. Realistic, natural colours, shallow depth of field.
```
Video2World (text paired with the input image):
```
A tabby cat sits on a sunlit wooden table beside a glass of water. Over the next few seconds it stretches one paw toward the glass, taps it, and watches the water ripple, while soft afternoon light stays constant and the camera remains still.
```
Predict2 text-to-image:
```
A wide-angle photograph of a rain-soaked city street at dusk, reflections of neon signs on wet asphalt, a lone cyclist crossing the intersection, natural dim ambient light, realistic materials.
```

## Style file hints
- format: text
- negative: separate (list of defects; optional)
- supports: t2v, i2v (Video2World), t2i (Predict2), no audio, long dense prompts, upsampler-style rewrite

## Open questions
- Official Predict2 text-to-image text encoder statement on the cards: not stated (Diffusers lists t5-11b).
- Exact behaviour and quality effect of negatives on Cosmos 1: not stated beyond the paper remark.
- Whether Video2World text should repeat image content: not stated.
- Safety filter rejection behaviour: not stated.

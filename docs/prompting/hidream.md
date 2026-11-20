# HiDream prompting guide (HiDream-I1, HiDream-O1)

Status: P0 reference. I1 has almost no official prompting guidance (settings and one example only). O1 has a model card, a README and an official prompt-agent. Sources (retrieved 2026-11-19; fetched via a summarizing tool):
- Official: https://github.com/HiDream-ai/HiDream-I1 and https://huggingface.co/HiDream-ai/HiDream-I1-Full
- Official: https://huggingface.co/HiDream-ai/HiDream-O1-Image and https://github.com/HiDream-ai/HiDream-O1-Image
- Official (Comfy): https://docs.comfy.org/tutorials/image/hidream/hidream-o1.md
- Core (community, Swarm maintainer): SwarmUI `docs/Model Support.md` (HiDream O1), `docs/Obscure Model Support.md` (HiDream-i1)
- Third-party listings (comfyui-wiki, wavespeed, etc.) were seen but not relied on.

## Swarm class ids

I1: `hidream-i1` (1024x1024; Full/Dev/Fast share it), `hidream-i1-edit` (768x768, must be set manually in model metadata), `hidream-i1/lora`. O1: `hidream-o1` (2048x2048), `hidream-o1/lora`.

## Text encoders

- I1: four encoders: T5-XXL (`google/t5-v1_1-xxl`) and Llama-3.1-8B-Instruct are named officially; Swarm docs (community) list Long-CLIP L, Long-CLIP G, T5-XXL, Llama-3.1-8B. The model card lists only T5 and Llama; the GitHub diffusers example loads Llama as a fourth encoder. Implication: CLIP parts exist but the LLM/T5 carry long prompts; natural language works well.
- O1: **no separate text encoder**; text, pixels and conditions share one token space (official). A Gemma model belongs to the optional prompt agent, not to generation.

## Prompt format and length

- I1: natural language; the only official example is short and tag-like ("Astronaut in a jungle, cold color palette, muted colors, detailed, 8k"). Max prompt length: not stated.
- O1: long descriptive paragraphs. The official T2I example opens with framing ("medium shot, eye-level, front view") then subject, clothing, setting, lighting. Dev example is one sentence. Max length: not stated. Swarm (community): "designed to use LLM-written prompts", speaks English and Chinese at least.
- Official O1 prompt agent: turns a raw instruction into a self-contained English prompt (JSON `prompt`, `reasoning`, `resolved_knowledge`), resolving layout, subject attributes, physical logic and text before generation. Our rewriter plays this role: make the prompt self-contained, resolve implied knowledge, spell any text.

## What works / what does not

| | Steps | CFG | Notes |
|---|---|---|---|
| I1 Full | 50 | 5.0 (official example) | standard CFG |
| I1 Dev | 28 | off (code comment: 0.0 for Dev and Fast) | distilled |
| I1 Fast | 16 | off | distilled |
| O1 Full | 50 | 5.0, shift 3.0 | guidance only effective on Full |
| O1 Dev | 28 | 0.0 (official card), Comfy docs say 1.0; shift 1.0 | negative not needed (Comfy docs) |

- Negative prompts: I1 not stated. O1 card not mentioned; Comfy docs say none needed for Dev, nothing stated for Full. Treat as unsupported/unnecessary; if the user supplies one for I1 Full, pass it through (Swarm normal CFG applies, community).
- Resolution: I1 official example 1024x1024; other sizes not stated. O1 default and maximum 2048x2048, values "snap to valid resolutions internally"; Swarm (community) says 1024 on O1 looks noticeably worse and above 2048 distorts color. I1 Edit works at 768 (community).
- Text rendering: O1 advertises accurate multi-region multilingual text (LongText-Bench EN 0.979, ZH 0.978 for the full model, vendor numbers). Put exact text in quotes, with font style, color and position (Comfy docs). I1: official example only has a cat holding a "HiDream.ai" sign; no claim stated.
- O1 layout boxes: the repo has `--layout_bboxes` with normalized values (apparently 0-1) but the box order is not stated; Swarm has no `<bbox>` format registered for `hidream-o1`. Do not emit boxes.
- O1 editing/personalization: one reference plus a short instruction ("remove the earphones"); multiple references with a scene description. The prompt does not name references by tag; the README never says how to refer to them. Swarm: up to 10 prompt images but only single-image input is strongly obeyed (community). Full model recommended for editing (May 2026 update).
- Quality keywords: not stated for either.

## Swarm tag compatibility (see `_swarm-syntax.md`)

- I1 uses CLIP-style parts alongside LLM/T5, but weights are most likely a blunt multiplier or unreliable; do not emit, preserve user-written ones. O1: LLM-native, same advice.
- `<break>`: only CLIP chunking; I1's long-CLIP behaviour is not stated, so do not emit. Regions: MM-DiT-like, early steps only (core doc); avoid. `<bbox>`: not registered. Embeddings: not applicable.
- Pass through `<lora:>`, `<wildcard:>`, `<random:>`, `<trigger>`, `<param:>`.

## Examples (our own wording, untested)

O1 (long form):
```
Medium shot, eye-level, front view. A ceramicist in a linen apron sits at a pottery wheel in a sunlit studio, hands shaping a tall grey-blue vase, clay slip on her forearms. Shelves of unfired pots line the back wall, soft north-window light, dust motes visible. A hand-lettered card pinned above reads "Studio Hours 10-4".
```

I1 (compact):
```
A cat holding a hand-painted sign reading "GOOD MORNING", sitting on a sunny kitchen counter, warm light, shallow depth of field, photographic.
```

O1 edit instruction: "Replace the grey sky with a dramatic sunset, keep the buildings unchanged."

## Style file hints

- format: text
- negative: none for O1 (and I1 Dev/Fast); separate optional for I1 Full
- supports: weights=no (do not emit), break=no, regions=no, boxes=no, text-rendering=yes (O1 strong; I1 unverified)

## Open questions

- I1 prompt-length limits and any official prompting advice (not stated).
- Whether the I1 official text-encoder list includes CLIP (model card and GitHub disagree with Swarm docs).
- O1 Full negative prompt behaviour; Dev CFG 0.0 vs 1.0 (card and Comfy docs differ, both mean "off").
- O1 layout bbox order/scale; how prompts should reference multiple images (not stated).
- HiDream-O1 versions beyond the May 2026 release (a newer Dev-2604 build is mentioned on the card; a "1.5" listing exists on third-party sites, unverified).

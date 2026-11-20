# Qwen-Image prompting guide (Qwen-Image, 2512, 2.0/2.1, Edit / Edit Plus)

Status: P0 reference. Official guidance exists but is thin: the READMEs give patterns by example, not a written guide. Sources (retrieved 2026-11-19; the fetches were summarized by a tool, so re-check exact values against the repos before shipping validators):
- Official: https://github.com/QwenLM/Qwen-Image (README: prompt enhancement, sizes, CFG/steps, Edit variants)
- Official: https://raw.githubusercontent.com/QwenLM/Qwen-Image-2.1/main/README.md (2.1: PE-T2I / PE-I2I rewriters, sizes, RGBA format, encoder)
- Core (community-maintained by Swarm, not Qwen): SwarmUI `docs/Model Support.md` (Qwen Image, Edit, 2.1 sections), `src/Text2Image/T2IModelClassSorter.cs`
- The Qwen blog page (qwen.ai/blog?id=qwen-image-2.1) returned no usable text; not used.
- Anything marked (community) below comes from third-party summaries or Swarm docs, not Qwen.

## Swarm class ids

`qwen-image` (default 1328x1328), `qwen-image-edit`, `qwen-image-edit-plus` (these three share compat `qwen-image`), `qwen-image-2.1` (default 2048 in practice; class registers 1024x1024, see resolution), plus `/lora`, `/vae`, `/controlnet` sub-ids that are not prompted directly.

## Text encoder

- Qwen-Image / Edit: README never names it; it requires transformers >=4.51.3 "supporting Qwen2.5-VL". Encoder identity is implied, not stated.
- Qwen-Image-2.1: Qwen3-VL 8B encodes both text and condition images (official, 2.1 README).
- Implication: an LLM/VLM encoder, so full natural-language sentences work; tag soup and CLIP tricks gain nothing.

## Prompt format and length

- Natural language, detailed. Official showcase prompts cover subject, age, clothing, setting, lighting, camera style ("casual iPhone snapshot", "wide-angle lens"), explicit textures, and precise pose/action.
- Languages: Chinese and English both supported; text rendering is highlighted as strong, especially Chinese. 2.1 rewriter outputs English prompts.
- Length: no maximum stated for Qwen-Image/Edit/2.1. A 2.0 announcement line mentions instructions up to about 1k tokens for infographics/slides/posters (that is 2.0, not stated for others).
- Original Qwen-Image README example appends a quality suffix (English: Ultra HD, 4K, cinematic composition). Optional; not stated as required.

## Official prompt enhancement

- T2I (Qwen-Image, 2512): README recommends `rewrite(prompt)` from `tools.prompt_utils`, powered by Qwen-Plus via a DashScope key; 2512 has its own `prompt_utils_2512.py`. Our own rewriter should do the same job: expand a short idea into a long, concrete, descriptive paragraph, keeping any quoted text verbatim.
- Edit: README says "editing results may become unstable if prompt rewriting is not used" (Qwen-VL-Max `polish_edit_prompt`). So rewriting matters more for Edit than for T2I.
- 2.1: two fine-tuned Qwen3.5-VL 9B checkpoints, `Qwen/Qwen-Image-2.1-PE-T2I` and `-PE-I2I`. T2I output is JSON with `rewritten_prompt` (long English) and `wh_ratio` (e.g. "16:9"); edit adds `ratio_follow` (e.g. `<image1>`). Not copied here; we write our own prompts.

## What works / what does not

- Text rendering: put the exact text in quotes and state where it appears (sign, chalkboard, screen), plus style/color/size. For slides, posters, infographics describe the layout section by section.
- Negative prompt: supported through `true_cfg_scale` (official default 4.0). If none is wanted the README passes a single space. The 2512 example uses a Chinese negative list (low resolution, distorted limbs/fingers, oversaturation, waxy look, blurry/garbled text). 2.1 README gives no negative guidance and its Turbo examples use CFG 1 (so no negative effect there).
- Steps (official): Qwen-Image/2512/Edit 50 (cfg 4.0), Edit-2509/2511 40 (cfg 4.0, guidance 1.0); 2.1 default 40, Turbo 8. Swarm docs (community) say CFG 1 for 2.1.
- Resolution (official): 1328x1328 (1:1), 1664x928 (16:9), 928x1664, 1472x1104 (4:3), 1104x1472, 1584x1056 (3:2), 1056x1584. 2.1: native 2048x2048, 2752x1536 (16:9), 1536x2752, 2400x1792, 1792x2400, 2528x1696, 1696x2528. Swarm (community): 2.1 uses a 16x VAE so 2048 equals roughly 1024 on other models.
- Transparency (2.1 only): start with "This is an RGBA image with transparency.", describe the image, end by saying it has an alpha channel and a transparent background. Output must be PNG, not JPG.
- Layout boxes: no box format in the official docs. Do not emit JSON boxes.

## Edit / Edit Plus (short)

- Direct instruction style: "Change the rabbit's color to purple, with a flash light background" (official example). State what changes and what must stay.
- Edit-2509/2511 (`qwen-image-edit-plus`) accept a list of images; no formal "picture 1" tag syntax is documented. The official SGLang example refers to inputs descriptively ("the girl in Figure 1"); treat ordinal references as an informal convention, not a rule. Swarm (community): Plus works well with about 3 images; 2.1 accepts up to 10 references and local edits may be marked by circles or masks.
- Swarm accepts the input images from the prompt box; the rewriter must never invent image numbering the user did not give.

## Swarm tag compatibility (see `_swarm-syntax.md`)

- Weights `<weight[x]:...>` and legacy `(x:1.2)`: LLM-encoder family; at best a blunt multiplier, often nothing. Do not emit; keep if the user wrote them.
- `<break>`: not applicable (CLIP only). Regions: MM-DiT, mostly early steps only; do not emit unless asked.
- `<bbox>`: not registered for these classes. Embeddings: not applicable.
- `<lora:>`, `<wildcard:>`, `<random:>`, `<trigger>`, `<param:>`: pass through unchanged, outside any quoted render-text.

## Examples (our own wording, untested)

Short idea "fox in snow, poster with the title WINTER":
```
A flat-color travel poster of a red fox walking through deep snow among bare birch trees at dusk, soft blue shadows, warm orange rim light on the fur. The title "WINTER" is printed in large white sans-serif capitals across the top third, with a thin border around the poster.
```

Edit instruction: "Make the jacket in the photo dark green and keep her face, pose and the street background unchanged."

2.1 transparent asset:
```
This is an RGBA image with transparency. A glossy red apple with a single green leaf, studio lit, crisp edges. The image has alpha channel and the background is transparent.
```

## Style file hints

- format: text
- negative: separate (T2I, base and 2512; CFG about 4). none for 2.1 Turbo and Edit-2509/2511 (single space).
- supports: weights=no (do not emit), break=no, regions=no, boxes=no, text-rendering=yes (quoted text plus placement)

## Open questions

- Exact text encoder for Qwen-Image/Edit (not stated); max prompt length (not stated).
- Official negative-prompt advice for 2.1 base (not stated).
- Formal multi-image referencing syntax for Edit Plus and 2.1 (not stated).
- Qwen-Image 2.0 weights/prompt guidance (announced; no code or settings in the repo page). Qwen-Image-Layered: no prompt details.

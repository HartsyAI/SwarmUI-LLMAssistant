# HunyuanImage 2.1 (and refiner) prompting guide

Status: draft reference, official README only, thin on prompt rules. Sources (retrieved 2026-11-19; the pages were read through a summarizing fetch tool, so verify exact numbers before shipping a validator):
- Official: https://github.com/Tencent-Hunyuan/HunyuanImage-2.1 (README), https://huggingface.co/tencent/HunyuanImage-2.1 (model card)
- Official (related): https://arxiv.org/abs/2509.04545 is the **PromptEnhancer** paper (rewriter evaluated on HunyuanImage 2.1), not the model's own report. The model's technical report was not located.
- Core: SwarmUI `docs/Model Support.md` (Hunyuan Image 2.1 section), `src/Text2Image/T2IModelClassSorter.cs`, `WorkflowGeneratorModelSupport.cs`.

Swarm class ids: `hunyuan-image-2_1` (base and distilled), `hunyuan-image-2_1-refiner` (refiner, used through Swarm's Refine/Upscale group).

## Text encoders
- Official: a multimodal LLM encoder for scene, action and requirements, plus a **ByT5** encoder for glyph-aware text rendering. The README does not name the MLLM or give token limits ("not stated").
- Core loads `qwen_2.5_vl_7b_fp8_scaled` plus `byt5_small_glyphxl` (so the MLLM is Qwen2.5-VL-7B-class per Swarm's loader; the README does not say).
- Implication: natural language, not tags; literal text to render benefits from ByT5.

## Prompt format and length
- Natural language, Chinese or English ("native support for both"). The README says longer, more detailed prompts "significantly improve" images. No length limit is stated.
- Training captions were structured at short, medium, long and extra-long levels (model card).
- Official rewriter: PromptEnhancer (the `use_reprompt` flag, or the separate PromptEnhancer-32B). Set `use_reprompt=False` if the prompt is already enhanced.

## What works / what does not
- **Weights:** not stated. LLM-encoder model, so Swarm's `<weight>` is only a blunt multiplier at best; do not rely on it.
- **Negative prompt:** not stated by the README. In Swarm it is a normal CFG model (CFG about 3.5), so a separate negative field is wired; the distilled variant can run CFG 1 where it has no effect (Swarm doc).
- **Text rendering:** advertised via ByT5. The README has no explicit quoting rule, but its examples put the target text in quotation marks. Do that.
- **Layout/boxes:** not stated; no JSON box format.
- **Resolution:** official: 2K only, "1K will result in artifacts". Listed: 2048x2048 (1:1), 2560x1536 (16:9), 2304x1792 (4:3), 1792x2304 (3:4), 1536x2560 (9:16); 3:2 and 2:3 are listed without sizes. Swarm doc says lower resolutions can work; the VAE is 32x downscale so 2048 is a small latent. Conflict, prefer official 2K.
- **Settings (official):** base 50 steps, guidance 3.5, shift 5; distilled 8 steps, guidance 3.25, shift 4. Swarm doc suggests about 20 steps for base and says distilled needs testing.
- **Refiner:** second stage for clarity and fewer artifacts. In Swarm it is used via the Refine/Upscale group with `<base>` and `<refiner>` text sections (see Swarm's `Model Support.md` for the recommended parameters; the upstream refiner prompt template is not reproduced here). A short quality-oriented text in the `<refiner>` section is the only refiner-specific prompting.

## Swarm tag compatibility (see `_swarm-syntax.md`)
- `<weight>` and legacy `(x:1.2)`: legacy parsing is off for this class (only SD1/2/XL/3 and Anima enable it in core). Avoid.
- `<break>`: not applicable (not CLIP). Do not emit.
- Regions/`<object>`: MM-DiT style; weak per Swarm docs. `<bbox>`: not registered for this family. Embeddings: n/a.
- `<lora:>`, `<wildcard:>`, `<random:>`, `<base>`/`<refiner>`: fine, processed by Swarm.

## Video notes
Not a video model.

## Examples (our own wording, to be checked against a real model)

"a cat on a windowsill":
```
A tabby cat sits upright on a sunlit wooden windowsill, amber eyes looking toward the camera. Soft morning light from the left, a blurred green garden behind it, shallow depth of field, warm natural color, high detail fur.
```

"bakery sign saying FRESH BREAD":
```
A hand-painted wooden storefront sign above a small bakery door. The sign reads "FRESH BREAD" in bold cream capital letters on a deep green background, with a small wheat illustration to the left. Late afternoon sun, slightly weathered paint, street-level view.
```

## Style file hints
- format: text
- negative: separate (ignored when CFG is 1 on the distilled model)
- supports: weights=no (unreliable), break=no, regions=weak, boxes=no, text-rendering=yes (quote the text)

## Open questions
- Token limits for either encoder; whether negative prompts are intended (not stated).
- Best settings for the distilled model in Swarm (Swarm doc says its documented settings work poorly).
- Whether the official technical report states a prompt style beyond "longer is better".

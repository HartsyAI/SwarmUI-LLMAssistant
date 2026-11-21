# PixelDiT and PiD prompting guide

Status: draft reference. **PixelDiT and PiD are two distinct models**: PixelDiT is a text-to-image model (prompted); PiD is a latent-to-pixel decoder/upsampler (not prompted). Official pages give settings, no prompting guide. Sources (retrieved 2026-11-19; fetched through a summarizing tool):
- Official (PixelDiT): https://github.com/NVlabs/PixelDiT , https://huggingface.co/nvidia/PixelDiT-1300M-1024px (model card), paper https://arxiv.org/abs/2511.20645
- Official (PiD): https://github.com/nv-tlabs/PiD , paper https://arxiv.org/abs/2605.23902 (linked from the PiD repo; not read)
- Integration: https://docs.comfy.org/tutorials/image/pixeldit/pixeldit.md ; SwarmUI `docs/Obscure Model Support.md` (PixelDiT), `T2IModelClassSorter.cs`.

Swarm class ids: `pixeldit` (the 1.3B 1024px text-to-image model, NVIDIA, direct pixel-space, no VAE) and `pid` (compat class for PiD models; used as an upscale/pixel-decode choice in Swarm's refine group, not as a main generator).

## Text encoder
- PixelDiT: **Gemma-2-2B-IT**, text embedding dim 2304, **max length 300 tokens** (model card). Core loads the same Gemma 2 2B encoder.
- PiD: per the repo it takes latents from a backbone's VAE (FLUX, FLUX.2, SD3, SDXL, Z-Image, Qwen-Image, Boogu, DINOv2/SigLIP RAEs, etc.). It is not described as consuming a text prompt; what its `--prompt` flag does is **not stated**.

## Prompt format and length
- PixelDiT: natural language, within 300 tokens. No prompt guide, no template, no rewriter documented. English assumed; other languages not stated.
- PiD: prompt not applicable. Nothing for the rewriter to do; the prompt belongs to the base model that produced the latent.

## What works / what does not (PixelDiT)
- **Weights:** not stated; Gemma-encoder model, treat as unsupported.
- **Negative prompt:** supported by the official script (optional, default empty). The card shows an example list: `low quality, worst quality, over-saturated, blurry, deformed, watermark`. ComfyUI docs expose a negative field. Use a short quality-style list in the separate negative field.
- **Text rendering, layout/boxes:** not stated.
- **Quality caveat:** Swarm doc rates its relative quality as poor at launch; keep prompts simple and concrete.
- **Licence:** NSCLv1, non-commercial research and evaluation only (card). The PiD codebase is Apache-2.0; weights licence not stated.

## What PiD does (for context only)
A conditional pixel-space diffusion decoder replacing VAE decoders and unifying decoding with upsampling. In Swarm it appears in the Refine/Upscale area; the LLM should leave the prompt untouched for PiD stages.

## Swarm tag compatibility (see `_swarm-syntax.md`)
- `<weight>`, legacy `(x:1.2)`: legacy parser off. Avoid.
- `<break>`: do not emit. Regions: weak. `<bbox>`: no format registered. Embeddings: n/a.
- `<lora:>`, `<wildcard:>`, `<random:>`: fine for PixelDiT. LoRA existence for it is not stated.

## Video notes
Neither is a video model.

## Examples (our own wording, to be checked against a real model)

"lighthouse":
```
A white lighthouse on a rocky cliff at sunset, waves breaking on the rocks below, warm orange sky with thin clouds, a small boat far out to sea. Realistic photograph, sharp focus, rich colors.
```

Negative field:
```
low quality, blurry, deformed, watermark, over-saturated
```

## Style file hints
- format: text (short, concrete; under 300 tokens)
- negative: separate
- supports: weights=no, break=no, regions=weak, boxes=no, text-rendering=unknown (not stated)

## Open questions
- Whether the Gemma encoder performs better with long captions (not stated); text-rendering ability.
- Exact role of PiD's `--prompt` flag; PiD paper not read.

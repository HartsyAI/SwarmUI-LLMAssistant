# Microsoft Lens prompting guide

Status: draft reference. Paper and GitHub README are informative; the Hugging Face model card was not retrievable (HTTP 401 on `microsoft/Lens`). Sources (retrieved 2026-11-19; fetched through a summarizing tool):
- Official: https://github.com/microsoft/Lens (README), paper https://arxiv.org/abs/2605.21573 ("Lens: Rethinking Training Efficiency for Foundational Text-to-Image Models"; read only partly), HF repos `microsoft/Lens`, `Lens-Turbo`, `Lens-Base` (cards not read)
- Integration: https://docs.comfy.org/tutorials/image/lens/lens.md; core `WorkflowGeneratorModelSupport.cs`, SwarmUI `docs/Obscure Model Support.md` (Lens).

Swarm class id: `lens` (3.8B dual-stream MMDiT, MIT licence, released for research only).

## Text encoder
- **GPT-OSS-20B** (frozen MoE, 3B active) with features from layers 4, 12, 18, 24 concatenated and projected (paper); **FLUX.2 VAE** (ComfyUI docs, Swarm core).
- Trained on English captions only, yet evaluated in Chinese, French, Japanese and Spanish, attributed to the strong encoder (paper). Swarm gallery sample uses French.
- Implication: a reasoning-LLM encoder; long descriptive English prompts are the native style.

## Prompt format and length
- Natural language. Training used 800M images with **dense, long-form GPT-4.1 captions averaging about 109 words**; dense-only training beat brief or mixed captions (paper). Aim for a detailed 80-150 word description. Token limit: not stated.
- Official prompt rewriting: a "reasoner" LLM rewrites requests into detailed prompts in the training caption style (paper default GPT-5.5; the CLI offers `--reasoner` using the loaded GPT-OSS encoder, or an OpenAI-compatible API). Paper reports results with and without it.
- Text appearing in an image stays in its original language inside the caption.

## What works / what does not
- **Text rendering:** pretraining includes text-heavy and synthetic text images; evaluated on LongText (EN), CVTG, OneIG (EN). README gallery prompts use quoted text; no official rule. Quote it.
- **Weights:** not stated. LLM encoder; treat as unsupported.
- **Negative prompt:** not stated; no flag in the CLI. Turbo uses no CFG. Treat as unsupported.
- **Layout/boxes:** not stated.
- **Resolution:** base resolution 1024 or 1440 (CLI default 1440); ratios 1:2, 9:16, 2:3, 3:4, 1:1, 4:3, 3:2, 16:9, 2:1. Max stated 1440x1440; Swarm doc says coherent down to about 512 and up to about 2048.
- **Settings (GitHub README):** `Lens` (RL-tuned) 20 steps, CFG 5.0; `Lens-Turbo` 4 steps, CFG 1.0; `Lens-Base` (supervised only) 50 steps, CFG 5.0. ComfyUI docs say about 50 and about 8 steps; treat that as the outlier. Swarm doc: Turbo 4, 8 also works well.

## Swarm tag compatibility (see `_swarm-syntax.md`)
- `<weight>`, legacy `(x:1.2)`: legacy parser off. Avoid.
- `<break>`: do not emit. Regions: weak. `<bbox>`: no format registered. Embeddings: n/a.
- `<lora:>`, `<wildcard:>`, `<random:>`: fine.

## Video notes
Not a video model.

## Examples (our own wording, to be checked against a real model)

"portrait":
```
A close portrait of an elderly fisherman with deep weathered wrinkles and a grey beard, wearing a faded navy wool cap. He looks slightly off-camera with a calm, tired smile. Overcast daylight, muted blue-grey harbor blurred behind him, 85mm lens look, fine skin detail, natural color grading.
```

"jar label":
```
A product photo of a glass honey jar on a rustic wooden table. The paper label reads "Mountain Wildflower Honey" in dark brown serif type with a small bee illustration. Warm side light, golden honey glowing, soft background bokeh, a wooden dipper beside the jar.
```

## Style file hints
- format: text (long, detailed, English-first)
- negative: none
- supports: weights=no, break=no, regions=weak, boxes=no, text-rendering=yes (quote the text)

## Open questions
- Model card contents (not retrievable); token limit; the caption-style system prompt (paper Appendix E.1 not read).
- Whether the HF `Lens-Turbo` repo still exists (search results say the original was removed).

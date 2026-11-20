# Ovis-Image prompting guide

Status: draft reference. Official sources give run settings and one example, no prompting guide. Sources (retrieved 2026-11-19; fetched through a summarizing tool):
- Official: https://huggingface.co/AIDC-AI/Ovis-Image-7B (model card), https://github.com/AIDC-AI/Ovis-Image (README), paper arXiv 2511.22982 (listed by search results; not read)
- Official-adjacent: Diffusers `OvisImagePipeline` docs, https://huggingface.co/docs/diffusers/en/api/pipelines/ovis_image (not read)
- Core: SwarmUI `docs/Obscure Model Support.md` (Ovis), `WorkflowGeneratorModelSupport.cs`.

Swarm class id: `ovis`.

## Text encoder
- Official: the PyTorch CLI loads **Ovis2.5-2B** via `--ovis_path` next to a separate VAE; the card does not call it a text encoder explicitly. Model is described as built on Ovis-U1.
- Core loads `ovis_2.5.safetensors` as the text encoder and the Flux.1 VAE.
- Implication: an LLM-style encoder, so plain descriptive natural language; no CLIP syntax.

## Prompt format and length
- Natural language. Official example is one descriptive paragraph naming the literal text, material, colors, lighting, background and finish. Length limit: not stated. Prompt rewriting: not stated. Swarm doc: "supports general prompting in any format".
- English and Chinese (benchmarks report both; a full language list is not stated).

## What works / what does not
- **Focus:** text-heavy images (posters, banners, logos, UI mockups, infographics): "tight alignment between linguistic content and rendered typography".
- **Text rendering:** the official example wraps the literal text in double quotes. No formal rule, but follow it; one quoted string per text element.
- **Weights:** not stated. Treat as unsupported.
- **Negative prompt:** the Diffusers example passes `""`; the PyTorch script has no negative option. Treat as unsupported or weak; if used, Swarm's separate field applies at CFG above 1.
- **Layout/boxes:** not stated.
- **Resolution:** 1024 (CLI `--image_size 1024`). Swarm doc: quickly breaks above 1024. Aspect ratios: not stated.
- **Settings (official):** 50 steps, CFG 5.0. Swarm doc: Euler, Beta scheduler may be better, 20 steps acceptable.

## Swarm tag compatibility (see `_swarm-syntax.md`)
- `<weight>`, legacy `(x:1.2)`: legacy parser off for this class. Avoid.
- `<break>`: do not emit. Regions: weak. `<bbox>`: no format registered. Embeddings: n/a.
- `<lora:>`, `<wildcard:>`, `<random:>`: fine.

## Video notes
Not a video model.

## Examples (our own wording, to be checked against a real model)

"concert poster":
```
A vertical concert poster in a bold screen-print style. The headline "NIGHT SKY LIVE" in large cream block letters across the top, a purple and orange gradient sky below with a silhouette of a crowd, and the small line "Saturday, 9 PM" at the bottom in thin white type. Grainy paper texture, strong contrast.
```

"app login mockup":
```
A clean mobile app login screen on a white background. A rounded button labeled "Sign in" in blue, a text field with the placeholder "Email", a small gray link reading "Forgot password?", soft drop shadows, flat modern UI design.
```

## Style file hints
- format: text
- negative: none (or separate, weak)
- supports: weights=no, break=no, regions=weak, boxes=no, text-rendering=yes (quote the text)

## Open questions
- Negative prompt support; token limit; whether the 2B Ovis2.5 encoder handles very long prompts (not stated).
- Official prompting advice for Chinese text (not stated).

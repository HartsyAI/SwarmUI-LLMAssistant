# Kandinsky 5.0 Image Lite prompting guide

Status: draft reference. Official sources give examples and defaults but no prompting guide. Sources (retrieved 2026-11-19; fetched through a summarizing tool, so check field names and schemas against the source before a validator relies on them):
- Official: https://github.com/kandinskylab/kandinsky-5 (README), https://huggingface.co/kandinskylab/Kandinsky-5.0-T2I-Lite-sft-Diffusers (model card), paper https://arxiv.org/abs/2511.14993
- Official-adjacent: https://huggingface.co/docs/diffusers/en/api/pipelines/kandinsky5_image (Diffusers docs)
- Core: SwarmUI `docs/Model Support.md` (Kandinsky 5), `WorkflowGeneratorModelSupport.cs`.

Swarm class id: `kandinsky5-imglite` (model class `kandinsky5-image-lite`). The video models (`kandinsky5-vidlite`, `kandinsky5-vidpro`) are a different family, not covered here.

## Text encoders
- Qwen2.5-VL (7B) as the main encoder, plus CLIP ViT-L/14 as a single pooled global embedding (paper, Diffusers docs).
- Limits: paper says 256 tokens for Qwen and 77 for CLIP; the Diffusers pipeline defaults `max_sequence_length` to 512 (must be under 1024). Conflict: keep prompts well under about 250 tokens to be safe.
- Implication: natural-language descriptions; CLIP token weighting does not carry over.

## Prompt format and length
- Natural language. Training captions: mostly English synthetic captions, plus Russian captions for images above 512 px; SFT captions came in five lengths (very long to very short), in both languages (paper). So English and Russian are both supported by design; Russian concepts are advertised in the README.
- Prompt rewriting at inference: not stated (the paper uses Qwen3-32B only to rewrite SFT training captions).
- No official length recommendation; the sample prompts in the docs are one to three plain sentences.

## What works / what does not
- **Weights:** not stated.
- **Negative prompt:** a `negative_prompt` argument exists and the examples pass `""`; it is ignored when guidance is 1 or below. No official advice on content. In Swarm use the separate negative field.
- **Text rendering:** README advertises "strong text-writing"; the sample prompt puts the word in quotes ("a label 'HELLO'"). Quote literal text.
- **Layout/boxes:** not stated.

## Swarm tag compatibility (see `_swarm-syntax.md`)
- `<weight>`, legacy `(x:1.2)`: legacy parser off for this class; weighting is at most a blunt multiplier. Avoid.
- `<break>`: do not emit. Regions: weak per Swarm docs. `<bbox>`: no format registered. Embeddings: n/a.
- `<lora:>`, `<wildcard:>`, `<random:>`: fine.

## Video notes
Not applicable to the image-lite class.

## Examples (our own wording, to be checked against a real model)

"fox in snow":
```
A red fox standing in fresh snow at dawn, breath visible in the cold air, pale pink and blue sky behind it, soft diffused light, photographic detail on the fur, slightly low camera angle.
```

"tea shop menu board":
```
A chalkboard menu outside a small tea shop. At the top, in neat white chalk lettering: "TEA HOUSE". Below it three lines: "Jasmine", "Oolong", "Matcha". Wooden frame, warm lantern light, shallow depth of field.
```

## Style file hints
- format: text
- negative: separate
- supports: weights=no, break=no, regions=weak, boxes=no, text-rendering=yes (quote the text)

## Open questions
- Reconciling the 256 vs 512 token limit.
- Official guidance on negative prompts and on whether Russian prompts are as strong as English.

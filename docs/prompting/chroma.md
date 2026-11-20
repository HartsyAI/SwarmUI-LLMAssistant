# Chroma prompting guide (Chroma1-HD, Chroma Radiance, Zeta Chroma)

Status: sources retrieved 2026-11-19. Official guidance is minimal: the Chroma1-HD model card gives one example prompt and sampler settings and says nothing about caption style. Chroma Radiance and Zeta Chroma cards contain no prompting guidance at all. Most style advice is community-sourced and marked as such. Sources:
- Official: https://huggingface.co/lodestones/Chroma1-HD (model card)
- Official: https://huggingface.co/lodestones/Chroma1-Radiance (card, work in progress)
- Official: https://huggingface.co/lodestones/Zeta-Chroma (card, "WIP", finetune of Tongyi-MAI/Z-Image)
- Official-adjacent: https://huggingface.co/docs/diffusers/api/pipelines/chroma (T5 encoder, 512 token default, negative prompt example)
- Community (marked): https://github.com/maybleMyers/chromaforge/blob/main/levzzz_chroma_guide.md (unsigned, undated guide hosted in a fork repo)
- Swarm docs: `docs/Model Support.md` (Chroma, Chroma Radiance), `docs/Obscure Model Support.md` (Zeta Chroma); Swarm source `WorkflowGeneratorModelSupport.cs` (Zeta text encoder)

## Swarm class ids

`chroma` (Chroma / Chroma1-HD), `chroma-radiance`, `zeta-chroma`.

## Text encoders and what that implies

- Chroma and Radiance: T5-XXL only (no CLIP), with T5 padding tokens masked in attention except one (card, diffusers docs). Natural-language grammar is parsed by T5.
- Zeta Chroma: Z-Image derivative; Swarm loads a Qwen3 4B encoder through the `lumina2` loader (Swarm source). The Zeta card states no encoder. See `z-image.md` for the parent model's prompting notes; whether they carry over to the finetune is not stated.
- Token limit: diffusers default and max_sequence_length 512 for Chroma (diffusers docs); the community guide also says 512 tokens. Zeta: not stated.

## Prompt format and length

- Chroma1-HD card example: a detailed natural-language paragraph (subject, lighting, colour palette, background, overall quality). No statement about tags vs prose or training caption style.
- Swarm docs: generally works better with longer prompts; "prompt fluff" at the end can help clean output. Treated as a Swarm maintainer observation, not official.
- Community guide: natural-language sentences first, then optional period-separated tags at the end; commas between tags push toward cartoon/anime, periods suit realism; tags work best with Flash variants. No dedicated "photo" tag; "A casual snapshot of ..." gives an amateur phone-photo look. Optional `aesthetic 0-10` tags (11 may bleed). Quality tags ("masterpiece", "best quality") may or may not help.
- Recommended rewrite: one or two full prose paragraphs (roughly 80-200 words), concrete visual detail, style/medium named early.

## What works and what does not

- Negative prompts: supported and actively used. The official card and diffusers example pass a separate negative prompt (low quality, ugly, unfinished, out of focus, deformed, disfigured, blurry, smudged, restricted palette, flat colors). Swarm: standard CFG, not distilled to 1. Community: a very short negative (<70 tokens) is discouraged; either none or a fuller list. Write it as a comma list of unwanted qualities in the separate negative field.
- Weights: not documented. Swarm docs describe T5 models as weighting poorly; do not rely on them.
- Text rendering, layout/boxes: not stated anywhere official. Do not promise either.
- Zeta: still an early pretrain with poor results per Swarm docs.
- Radiance is pixel-space (no VAE per Swarm docs); the card says it is work in progress, "expect some squiggles on the details".
- Content: the card states no safety alignment; NSFW is not blocked by the model.

## Swarm tag compatibility

- Weights `<weight[..]:..>`: blunt/unreliable on T5; avoid. Legacy `(x:1.2)`: none of the three compat classes set the legacy parser flag, so it is not parsed; do not emit.
- `<break>`: CLIP-only; do not emit. Regions: MM-DiT, early-step only; avoid.
- `<bbox>`: no format registered (generic JSON would be out of distribution); do not emit.
- Embeddings: not applicable. `<lora:>`, `<random:>`, `<wildcard:>`, `<trigger>`: pass through. Swarm notes Z-Image LoRAs do not work on Zeta Chroma.

## Examples (our own wording, unverified on a live model)

Photographic: "A candid photograph of an elderly sailor repairing a rope on a wooden dock at sunset. Warm side light, long soft shadows, calm harbor with moored boats behind him, muted teal and amber palette, shallow depth of field, natural skin texture."
Negative (separate field): "low quality, blurry, out of focus, deformed, disfigured, smudged, flat colors, restricted palette"

Illustrative with trailing tags (community style): "A watercolor illustration of a fox reading under a mushroom, soft washes of green and ochre, loose brushwork, cozy forest evening light. watercolor. storybook. soft edges."

## Style file hints

```
format: text   # prose, optional trailing period-separated tags (community)
negative: separate
supports: weights=no, break=no, regions=limited, boxes=no, text-rendering=unknown
```

## Open questions

- Training caption style (prose vs tags vs mixed) for HD, Radiance and Zeta: not stated.
- Whether Zeta Chroma should be prompted like Z-Image (prose, supports negatives): not stated.
- Radiance and Zeta prompting recommendations: not stated by the authors.
- Text-rendering ability and any official token limit beyond the diffusers default.
- The community guide's authorship and date are unknown; treat its tag advice as unverified.

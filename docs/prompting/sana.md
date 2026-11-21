# NVIDIA Sana prompting guide

Status: reference draft, retrieved 2026-11-19. **No dedicated official prompting guide** was found. The GitHub README, Hugging Face card, paper abstract and Diffusers docs give encoder, limitations and example settings. The Sana docs site (https://nvlabs.github.io/Sana/docs/) home page had no prompting guidance (sub-pages not read).

Sources:
- Official: https://github.com/NVlabs/Sana (README; variants, resolutions, example settings)
- Official: https://huggingface.co/Efficient-Large-Model/Sana_1600M_1024px_diffusers (model card: encoder, limitations, prompt-length note)
- Paper abstract: https://arxiv.org/abs/2410.10629
- Integration docs: https://huggingface.co/docs/diffusers/main/en/api/pipelines/sana
- Core: SwarmUI `src/Text2Image/T2IModelClassSorter.cs`, `docs/Features/Prompt Syntax.md`

## Swarm class ids

`nvidia-sana-1600` (and `nvidia-sana-1600/vae`). Compat class has no legacy prompt parser flag in core. Sana-Video and the other newer Sana variants in the README have no Swarm class id in core as read, so they are out of scope here.

## Text encoder

Gemma-2-2B-IT (decoder-only LLM). Diffusers `max_sequence_length` default is 300.
- Implication: instruction-following LLM encoder; plain natural language works. The paper says "complex human instruction" (CHI) with in-context learning is used to improve image-text alignment. In Diffusers the pipeline prepends such an instruction itself by default (it frames the prompt as "enhance this description"). Whether Swarm's backend does the same: not stated, so do not write that wrapper yourself.

## Prompt format and length

Natural language. The card states quality improves by "increasing the complexity and length of prompts". Token cap 300 (Diffusers default). Recommended exact length: not stated.

## What works / what does not

- Limitations (card): cannot render complex legible text; weak on complex scenes and human hands; not perfectly photoreal; lossy autoencoding. Diffusers' own example prompts short quoted text on a neon sign ("Sana"), so short words may work; reliability not stated.
- Negative prompts: not mentioned on the card or README. Diffusers exposes `negative_prompt` (default empty, ignored when guidance <= 1) and says the negative embedding "should be" that of the empty string. Effectiveness of non-empty negatives: not stated.
- Layout/boxes: not stated.
- Weights: not stated for Sana; LLM-encoder note in `_swarm-syntax.md` applies (blunt multiplier, many do nothing).
- Style/quality keywords: not stated.
- Multilingual: the README changelog mentions multilingual SFT models (emoji, Chinese, English); a separate "MultiLing" 1024px checkpoint is listed in Diffusers. Whether the base model handles non-English: not stated.

## Swarm compatibility

- Weights `<weight[x]:...>`: unreliable on LLM-encoder models; do not emit by default.
- Legacy `(x:1.2)`: not parsed for this class; do not emit.
- `<break>`: CLIP-only; do not emit.
- Regions: not stated for Sana; treat as weak.
- Bbox: no JSON box format registered; do not emit.
- Embeddings: not applicable.

## Examples (our own wording, untested)

1. `a golden retriever puppy asleep on a knitted blue blanket, soft afternoon light from a window, cozy living room, shallow depth of field`
2. `an enamel pin design: a smiling cloud holding an umbrella, flat colors, thick dark outline, pastel background`
3. `a sprawling night market in the rain, strings of warm lanterns overhead, vendors under striped awnings, steam from food stalls, reflections on wet pavement, wide cinematic shot`

## Style file hints

- format: text
- negative: separate (mechanically supported; official guidance absent)
- supports: weights no, break no, regions no, boxes no, text-rendering limited (card says not complex legible text)

## Open questions

- Does the Swarm/Comfy path apply the complex human instruction, and does adding one manually help or hurt?
- Official negative prompt and prompt-style advice (none found).
- Sana docs sub-pages not read (image generation page may add tips).
- Quality of short quoted text and non-English prompts on the base checkpoint.

# OmniGen2 prompting guide

Status: P0 reference. Official README has a real tips section (editing and in-context generation); nothing official on plain T2I style beyond "longer and more detailed works better, English is best". Sources (retrieved 2026-11-19; fetched via a summarizing tool):
- Official: https://github.com/VectorSpaceLab/OmniGen2 (README: usage tips, guidance scales, limitations)
- Official: https://huggingface.co/OmniGen2/OmniGen2 (model card: tips, negative prompt, limitations)
- Official paper: https://arxiv.org/abs/2506.18871 (abstract only; no prompt, encoder or language detail on that page)
- Core (community, Swarm maintainer): SwarmUI `docs/Obscure Model Support.md` (OmniGen 2 section)

## Swarm class ids

`omnigen-2`. Swarm supports only the direct image features, not the LLM text-generation features.

## Text encoder

- Qwen2.5-VL 3B, per Swarm docs (community). The official pages say only that visual understanding is inherited from a Qwen-VL-2.5 foundation and do not describe prompt encoding.
- Implication: MLLM encoder; plain sentences and explicit instructions work. The model "currently performs best with English prompts" (official).

## Prompt format and length

- Natural-language instruction style. For editing and in-context generation the official template is: name the first image, state the change, name what to take from the second image, then describe the target image in as much detail as possible.
- Reference convention (official): ordinal "first image" / "second image"; model card also writes "image 1" / "image 2" ("Add the bird from image 1 onto the desk in image 2"). Use whichever form the user typed; do not mix.
- Longer, more detailed prompts "generally work better" (official). Token limit: not stated.
- Single-image edit consistency (official): add "maintaining her/his facial features, hairstyle, and other attributes".
- Text rendering: not mentioned in official pages. Do not promise it; quoted text is a best effort.

## What works / what does not

- Negative prompt: supported (official). Describe what should not appear, example "blurry, low quality, text, watermark"; a default negative exists and the README suggests using it if unsure. Note the example negative includes "text", so drop that term if the user wants lettering.
- Layout boxes, weights: not stated. Quality/style keywords: not stated.
- Expect multiple attempts; official text says the model does not always follow instructions.

## Swarm tag compatibility (see `_swarm-syntax.md`)

- Weights and `(x:1.2)`: MLLM encoder, blunt multiplier at best. Do not emit; preserve if user-written.
- `<break>`: not applicable. Regions: not stated; avoid. `<bbox>`: not registered for `omnigen-2`. Embeddings: not applicable.
- Reference images come from the Swarm prompt box (or Init Image if none). The rewriter must keep the user's image ordinals and never add references that do not exist.
- Pass through `<lora:>`, `<wildcard:>`, `<random:>`, `<trigger>`, `<param:>`.

## Examples (our own wording, untested)

Text-to-image:
```
A cozy attic reading nook with a round window, a worn green armchair, a stack of hardcover books on a small wooden stool, warm afternoon light and drifting dust, painterly style with soft edges.
```

Single-image edit:
```
Change the first image so the woman wears a red raincoat and holds a yellow umbrella, maintaining her facial features, hairstyle and the street background.
```

In-context (two images):
```
Edit the first image: place the corgi from the second image on the sofa. The living room is bright and tidy, the dog looks toward the camera, natural window light.
```
Negative (separate): `blurry, low quality, watermark`.

## Style file hints

- format: text
- negative: separate
- supports: weights=no (do not emit), break=no, regions=no, boxes=no, text-rendering=no (not stated)

## Open questions

- Maximum prompt length (not stated). Languages other than English (not stated).
- Text rendering ability (not stated by official sources).
- Official text encoder name in the repo docs (only Swarm docs state Qwen2.5-VL 3B).
- Whether "image N" or "Nth image" is preferred when more than two references are used (not stated; both forms appear).

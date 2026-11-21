# ERNIE-Image prompting guide

Status: draft reference. Official card has no prompting rules beyond "long, detailed prompts"; it documents a Prompt Enhancer. Sources (retrieved 2026-11-19; fetched through a summarizing tool):
- Official: https://huggingface.co/baidu/ERNIE-Image (model card), https://github.com/baidu/ernie-image (README); technical report listed at alphaxiv 2605.25347 (not read)
- Official-adjacent: https://docs.comfy.org/tutorials/image/ernie-image/ernie-image.md (ComfyUI docs, encoder names)
- Core: SwarmUI `docs/Obscure Model Support.md` (ERNIE), `T2IModelClassSorter.cs`.

Swarm class id: `ernie-image` (8B single-stream DiT; SFT "base" and "Turbo" variants; Apache-2.0 per search results).

## Text encoder
- Official card and README: not stated. ComfyUI docs and Swarm core: **Ministral 3 3B** (same as Flux.2). Label that as community/integration-sourced.
- A separate lightweight **Prompt Enhancer** (a 3B model per ComfyUI docs) is official, ships in the `pe` folder.
- Implication: LLM encoder, natural language; no CLIP syntax.

## Prompt format and length
- Natural language, long single-paragraph descriptions (camera angle, composition, subject placement, lighting, palette, mood). No JSON or tag syntax for the model. Token limit: not stated.
- The Prompt Enhancer expands short inputs into richer structured descriptions (`use_pe=True`). Its SGLang input JSON (`prompt`, `width`, `height`) is the enhancer's request format, **not** the image prompt format.
- English and Chinese covered by benchmarks; Swarm doc says it handles both deeply.
- Card hint: GenEval with PE helped position and counting but lowered colors and attribute binding for the base model, so enhancement is not free.

## What works / what does not
- **Text rendering:** strong on dense, long-form, layout-sensitive text (posters, infographics, UI, comics, multi-panel). No formal procedure; showcase prompts put exact text in quotes. Do the same.
- **Layout:** described in prose ("title at the top, three panels below"); no box syntax.
- **Weights:** not stated. Treat as unsupported.
- **Negative prompt:** not stated; no negative parameter in any official example. Turbo runs at CFG 1.

## Swarm tag compatibility (see `_swarm-syntax.md`)
- `<weight>`, legacy `(x:1.2)`: legacy parser off. Avoid.
- `<break>`: do not emit. Regions: weak. `<bbox>`: no format registered. Embeddings: n/a.
- `<lora:>`, `<wildcard:>`, `<random:>`: fine. Swarm does not run the official Prompt Enhancer; the LLM rewrite takes that role.

## Video notes
Not a video model.

## Examples (our own wording, to be checked against a real model)

"product shot":
```
A top-down photograph of a ceramic mug of black coffee on a light oak table, a folded linen napkin to the left and two coffee beans near the handle. Soft window light from the upper right casting a gentle shadow, muted warm palette, calm minimal mood.
```

"infographic":
```
A clean vertical infographic titled "HOW A SEED GROWS". Four numbered steps stacked top to bottom with small flat icons: "1 Seed", "2 Sprout", "3 Leaves", "4 Flower". Pastel green background, rounded cards, consistent sans-serif type.
```

## Style file hints
- format: text
- negative: none (Turbo) / separate (base, effect unverified)
- supports: weights=no, break=no, regions=weak, boxes=no, text-rendering=yes (quote the text)

## Open questions
- Official text encoder name, token limit, negative-prompt behavior (not stated).
- Whether the rewrite should imitate the Prompt Enhancer's output style (its training format is not published in what we read).

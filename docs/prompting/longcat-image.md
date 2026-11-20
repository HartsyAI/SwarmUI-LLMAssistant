# LongCat-Image prompting guide

Status: draft reference. The model card has one hard rule (quote rendered text) and run settings. Sources (retrieved 2026-11-19; fetched through a summarizing tool, so verify before shipping a validator):
- Official: https://huggingface.co/meituan-longcat/LongCat-Image (model card), technical report https://arxiv.org/abs/2512.07584
- Official-adjacent: Diffusers `LongCatImagePipeline` docs (listed in search, not read)
- Core: SwarmUI `docs/Obscure Model Support.md` (LongCat-Image), `WorkflowGeneratorModelSupport.cs`.

Swarm class id: `longcat-image` (6B, by Meituan).

## Text encoder
- Technical report: **Qwen2.5-VL-7B**, used as one unified encoder for generation and editing. The model card itself does not name it.
- Quoted text gets a **character-level tokenizer**: "a character-level tokenizer specifically for content demarcated by quotation marks" (report).
- Implication: natural language; quoting is functional, not cosmetic.

## Prompt format and length
- Natural language, English or Chinese. The report's training captions had four granularities (entity, phrase, composition, photographic) weighted toward detailed photographic captions, so descriptive prompts match training. Token limit: not stated.
- Official prompt rewrite: `enable_prompt_rewrite=True` reuses the text encoder as a rewriter; the card also says a stronger external LLM can improve results.

## What works / what does not
- **Text rendering (headline rule):** the card says you must put the text to be rendered inside single or double quotation marks (English or Chinese quote styles); without them text rendering is "severely" compromised. Everything to be drawn as lettering goes in quotes, spelled exactly.
- **Weights:** not stated. Treat as unsupported.
- **Negative prompt:** not stated. Normal CFG model, so Swarm's separate field is wired; effect unverified.
- **Layout/boxes:** not stated.

## Swarm tag compatibility (see `_swarm-syntax.md`)
- `<weight>`, legacy `(x:1.2)`: legacy parser off. Avoid.
- `<break>`: do not emit. Regions: weak. `<bbox>`: no format registered. Embeddings: n/a.
- Preserve quotation marks around text when rewriting; never strip them.
- `<lora:>`, `<wildcard:>`, `<random:>`: fine.

## Video notes
Not a video model (an edit variant, LongCat-Image-Edit, is a separate model).

## Examples (our own wording, to be checked against a real model)

"street shop sign":
```
A rainy evening street in a narrow alley, a glowing red neon sign above a noodle shop reading "NOODLES 24H", wet pavement reflecting the light, a person with an umbrella walking away, cinematic wide shot.
```

Chinese:
```
一张复古海报，中央是一只红色的纸鹤，顶部用黑色粗体写着“春日市集”，底部小字为“四月十二日”，米色纸张纹理，扁平插画风格。
```

## Style file hints
- format: text
- negative: separate
- supports: weights=no, break=no, regions=weak, boxes=no, text-rendering=yes (quotes mandatory)

## Open questions
- Token limit; negative prompt behavior; best rewrite template (report details are not stated in what we read).

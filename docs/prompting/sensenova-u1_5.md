# SenseNova-U1.5 prompting guide

Status: draft reference. U1.5 is a **Preview** release (the card says a refined production version is coming), so settings may change. Sources (retrieved 2026-11-19; fetched through a summarizing tool; check field names and schemas against the source before a validator relies on them):
- Official: https://huggingface.co/sensenova/SenseNova-U1.5-8B-MoT-Preview (model card), https://github.com/OpenSenseNova/SenseNova-U1 (README), `docs/u1.5_best_practices.md` in that repo (prompt-enhancement tools only)
- Announcement: https://sensetime.com/en/news/51170629 (U1 family, April 2026; not read in detail)
- Core: SwarmUI `docs/Obscure Model Support.md` (SenseNova), `T2IModelClassSorter.cs`.

Swarm class id: `sensenova-u1.5` (8B MoT; no VAE, uses NEO-Unify patch encode/decode layers; Apache-2.0 per search results). Swarm doc says it is a derivative of HiDream-O1; that is a Swarm claim, not on the official pages we read.

## Text encoder
- Native, integrated into the unified model (no separate encoder file; no VAE). Swarm doc: "text encoding is similarly native-integrated".
- Implication: an LLM-native model; weights are not applicable; understands instructions well, details less so (Swarm doc: "very intelligent but bad details").

## Prompt format and length
- Natural language works for clear subjects with few constraints. The card's showcases for posters and infographics use **structured JSON prompts** (fields such as `canvas`, `layout`, `style`, `composition_rules`, `negative_prompt`) in Chinese or English; whether JSON is required or parsed specially is **not stated**, and no schema is documented. Do not invent field names; for ordinary requests write plain prose.
- Exact on-image copy goes in quotation marks; the best-practices page adds "no other text" style exclusions (short or underspecified prompts can produce unintended text).
- Official prompt enhancement is an external agent skill (the Image PE skill turns a brief into compact Render JSON) and a Caption-to-Prompt script for reference images; inference does not depend on them. Token limit: not stated.
- English and Chinese documented.

## What works / what does not
- **Text rendering:** substantially improved Chinese and English text and dense layouts (infographics, posters). Known weaknesses: dense or lengthy text, small characters, mixed Chinese-English, exact counts, alignment and hierarchy in complex layouts, small faces and hands.
- **Layout/boxes:** region-controllable editing via masks, boxes and visual markers exists on the editing side; the input format is not stated. Layout for generation is described in prose or in the JSON showcase.
- **Weights:** not applicable; avoid.
- **Negative prompt:** only as a `negative_prompt` array **inside JSON** in showcases; no separate negative parameter in the documented commands. CFG exists, so a separate field may work in Swarm but is unverified.

## Swarm tag compatibility (see `_swarm-syntax.md`)
- `<weight>`, legacy `(x:1.2)`: legacy parser off. Avoid.
- `<break>`: do not emit. Regions/`<bbox>`: no registered box format; do not emit. Embeddings: n/a.
- `<lora:>`, `<wildcard:>`, `<random:>`: fine. JSON prompts containing Swarm tags need escaping care (see `_swarm-syntax.md` open questions).

## Video notes
Not a video model.

## Examples (our own wording, to be checked against a real model)

"vintage ad, plain text":
```
A 1950s-style print advertisement for lemon soda. A smiling woman in a yellow dress holds a glass bottle, bright turquoise background with sunburst rays. The headline reads "SUNNY LEMON SODA" at the top and the small line "Ice cold, always." at the bottom. No other text.
```

"poster, Chinese":
```
一张竖版旅行海报：清晨的西湖，远处有薄雾中的山，近处一只小木船。顶部标题“西湖晨光”，底部小字“杭州”。水彩风格，柔和配色，除此之外不要出现其他文字。
```

## Style file hints
- format: text (JSON is seen in official showcases but not documented; prefer text)
- negative: none (inline exclusions as prose like "no other text")
- supports: weights=no, break=no, regions=no, boxes=no, text-rendering=yes (quote the text, state no other text)

## Open questions
- JSON prompt schema and whether it is parsed specially; mask/box input format; official guidance on a negative field.
- Whether the Preview and the SFT checkpoint differ in prompt style (Swarm doc: guidance unclear).

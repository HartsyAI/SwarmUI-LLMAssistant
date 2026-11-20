# Lumina Image 2.0 prompting guide

Status: reference draft, retrieved 2026-11-19. **No official prompting guide** in the GitHub README or model card (no tips section; the README mentions neither system prompts nor negative prompts). The paper gives some hints about caption styles and the text encoder.

Sources:
- Official: https://github.com/Alpha-VLLM/Lumina-Image-2.0 (README: Gemma-2-2B in the model zoo, example settings)
- Official: https://huggingface.co/Alpha-VLLM/Lumina-Image-2.0 (model card; Apache-2.0)
- Paper: https://arxiv.org/abs/2503.21758 and https://arxiv.org/html/2503.21758 (via summary; verify details against the paper before relying on them)
- Integration docs: https://huggingface.co/docs/diffusers/main/en/api/pipelines/lumina2
- Core: SwarmUI `src/Text2Image/T2IModelClassSorter.cs`, `docs/Features/Prompt Syntax.md`

## Swarm class ids

`lumina-2` (default 1024x1024). Compat class has no legacy prompt parser flag in core.

## Text encoder

Gemma2-2B, a decoder-only LLM. Diffusers `max_sequence_length` default is 256. Diffusers also exposes an optional `system_prompt`; the paper says training prepends one of a small set of system prompts (short instructions to generate images matching the user prompt). The exact inference system prompt is not stated in the README or card, so the rewriter must not invent one.
- Implication: plain natural language, no CLIP-style limit; the LLM encoder follows full sentences.

## Prompt format and length

Natural language. The paper's UniCap captioner produces English and Chinese captions in several styles: detailed, medium, short, and tag-based descriptions, plus style, main-object, all-objects, attribute and spatial-relation captions. It says longer, more detailed captions speed up training convergence and generally generate better. Inference length recommendation: not stated; Diffusers cap is 256 tokens. The README examples are one short style-keyword prompt and one long descriptive paragraph (lighting, palette, composition).

## What works / what does not

- Languages: trained on English and Chinese captions; zero-shot ability reported in German, Japanese and Russian (paper). Do not translate user prompts away from Chinese.
- Text rendering: the paper says the model "still needs substantial improvements in accurately rendering long and complex text". Short words may work; not stated.
- Negative prompts: not mentioned in the README or paper. Diffusers exposes `negative_prompt` with CFG, and says for Lumina "this should be" the empty string. Do not rely on negative lists.
- Layout/boxes: not stated (no JSON box format).
- Weights: not stated; LLM-encoder note in `_swarm-syntax.md` applies.
- Guidance: README example CFG 4.0, 50 steps, `cfg_trunc_ratio` 0.25, `cfg_normalization` True, bf16, 1024x1024. Paper: CFG-Trunc applies full guidance only from a threshold timestep and CFG-Renorm rescales to limit oversaturation at high scales. Diffusers defaults are 30 steps, CFG 4.0.
- Resolution: 1024x1024 in all examples; other ranges not stated.
- Style/quality keywords: example "Astronaut in a jungle, cold color palette, muted colors, detailed, 8k"; effect not stated.

## Swarm compatibility

- Weights `<weight[x]:...>`: unreliable; avoid.
- Legacy `(x:1.2)`: not parsed for this class; do not emit.
- `<break>`: CLIP-only; do not emit.
- Regions: not stated for Lumina 2; treat as weak.
- Bbox: no registered JSON box format; do not emit.
- Embeddings: not applicable.

## Examples (our own wording, untested)

1. `Upper body portrait of an elderly fisherman with a weathered face and a grey wool cap, grey sea behind him, soft overcast light, film photograph`
2. `a flat illustration of a lighthouse on a cliff at sunset, stylized waves in teal and coral, simple shapes, warm gradient sky`
3. `一只橘猫坐在窗台上看着下雨的街道，暖色室内灯光，电影感` (Chinese is a trained language per the paper; quality untested)

## Style file hints

- format: text
- negative: none (not documented; Diffusers says empty negative)
- supports: weights no, break no, regions no, boxes no, text-rendering limited (paper says long or complex text is weak)

## Open questions

- Exact system prompt expected at inference, and whether Swarm/Comfy applies one.
- Negative prompt behaviour and recommended token length.
- Resolutions other than 1024x1024; whether tag-style prompts (a UniCap caption style) work better than sentences.
- Paper details above came from a summary of the HTML page, not read in full.

# Boogu-Image prompting guide

Status: draft reference. The project is young and community-run; the GitHub README has settings and limitations but no prompt-writing guide. Sources (retrieved 2026-11-19; fetched through a summarizing tool):
- Official: https://github.com/boogu-project/Boogu-Image (README), https://huggingface.co/Boogu/Boogu-Image-0.1-Base (model card), project page https://boogu.org/
- Paper: "Boogu-Image-0.1: Boosting Open Agentic Multimodal Generation via Understanding under a Minimal Budget", https://huggingface.co/papers/2607.13125 (abstract level only)
- Integration: https://docs.comfy.org/tutorials/image/boogu/boogu-image-0.1.md; core `WorkflowGeneratorModelSupport.cs`; SwarmUI `docs/Model Support.md` (Boogu).

Identification: the official org is `Boogu` on Hugging Face (`Boogu/Boogu-Image-0.1-*`, GitHub `boogu-project`); Comfy-Org repackages files. Authors are not named on the pages we read.

Swarm class id: `boogu` (Base, Turbo, Edit, Edit-Turbo; 10B, Apache-2.0).

## Text encoder
- Official: the checkpoint has an `mllm` component, not named on the card or abstract. ComfyUI docs and Swarm core: **Qwen3-VL 8B**,. "Derived from OmniGen" is a Swarm-doc claim; official pages do not state a base model.
- Implication: multimodal-LLM encoder, natural language (Chinese or English).

## Prompt format and length
- Natural language. No template or syntax is given; length limit not stated. Card example prompts are descriptive scenes.
- Official optional "instruction reasoner" (rewriter): `utils/t2i_external_prompt_rewriter.py` (example uses Qwen3-VL-32B-Instruct, `--lang en`) or the pipeline-integrated reasoning demo scripts. The paper credits "agentic prompt rewriting" but gives no template.

## What works / what does not
- **Text rendering:** strongest in Chinese and English, including bilingual layouts. For dense or ultra-dense text use **Base at 2K**; Turbo is good for simple text and weaker on dense text. Long text, small fonts and complex layouts can still cause typos or missing characters. Other languages degrade (all official).
- **Weights:** not stated. Treat as unsupported.
- **Negative prompt:** not stated. Base runs normal CFG so Swarm's separate field is wired; Turbo is CFG 1 (no effect).
- **Layout/boxes:** not stated.
- **Limitations (official):** weak knowledge of real brands, people and landmarks (describe, do not name); hands/limbs break in multi-person or occluded scenes; small faces, eyes and text may artifact. Research-only release.

## Swarm tag compatibility (see `_swarm-syntax.md`)
- `<weight>`, legacy `(x:1.2)`: legacy parser off. Avoid.
- `<break>`: do not emit. Regions: weak. `<bbox>`: no format registered. Embeddings: n/a.
- `<lora:>` (a Turbo rank-128 LoRA exists officially), `<wildcard:>`, `<random:>`: fine. Edit variants take a reference image, not covered here.

## Video notes
Not a video model.

## Examples (our own wording, to be checked against a real model)

"mountain village":
```
A small stone village on a steep green mountainside at sunrise, low mist in the valley, smoke rising from two chimneys, a winding path with a person carrying a basket. Soft golden light, painterly realism, wide establishing shot.
```

"bilingual cafe poster":
```
A warm cafe poster with a large latte illustration in the center. Title in bold brown letters: "MORNING BREW", and below it the Chinese line "清晨咖啡". A small footer line: "Open 7:00 - 18:00". Cream background, hand-drawn style.
```

## Style file hints
- format: text
- negative: separate (Base) / none (Turbo)
- supports: weights=no, break=no, regions=weak, boxes=no, text-rendering=yes (Base at 2K for dense text)

## Open questions
- Exact text encoder and base lineage (not stated officially); token limit; negative prompt behavior.
- Rewriter template (README points to INFERENCE_GUIDE.md, not read).

# Swarm prompt syntax: what a prompt writer (human or LLM) may use

Status: reference for the prompt-styles work (P0). Source: SwarmUI core `docs/Features/Prompt Syntax.md` (read 2026-11-19). Nothing here is invented; where the source is silent the entry says "not stated".

Rules for the rewriting LLM, derived from this document:
- Use only the tags listed here. Never invent a tag. Keep tag syntax balanced (`<` ... `>`), and never leave a stray `:` after a wildcard name.
- Tags are processed by Swarm **after** the rewrite, so they must survive it intact. A prompt containing tags is still a normal prompt.
- Whether a tag does anything depends on the target model (last section).

## Tags

| Tag | Form | Notes |
|---|---|---|
| Weight | `<weight[1.1]:text>` | >1 stronger, <1 weaker. Legacy `(text:1.2)` is parsed for legacy-era models if "Parse Alternative Prompt Syntaxes" is on (default). |
| Alternate | `<alternate:a, b>` / `<alt:a,b>` | Switch every step. `\|` or `\|\|` may separate. LoRAs inside are slow. |
| From-to | `<fromto[0.5]:a, b>` | Swap after a step (integer) or fraction. Exactly two entries. |
| Random | `<random:a, b, c>` | Seeded by the main seed (or `Wildcard Seed`). Separators `,` `\|` `\|\|` (most unique wins). Numeric ranges `1-5`, `0.8-1.2`. Multiple: `<random[1-3]:a,b,c>`, `random[1-3,]` adds commas. No repeats unless count exceeds options. |
| Wildcard | `<wildcard:name>` / `<wc:name>` | Text file in `Data/Wildcards`, one option per line. Same count syntax: `<wildcard[1-3]:animals>`. Exclude: `<wildcard:animals,not=cat,dog>`. Subfolders: `my/wildcard/name`. A trailing `:` is a UI autocomplete trick only; never submit it. |
| Variable | `<setvar[name]:data>`, `<setvar[name,false]:data>` (no emit), `<var:name>` | Evaluated when set. For repeating a random/wildcard result consistently. |
| Macro | `<setmacro[name]:data>`, `<macro:name>` | Evaluated when used (each use re-rolls). |
| Trigger | `<trigger>` | Fills the model/LoRA trigger phrase from metadata; empty if none. |
| Repeat | `<repeat[3]:text>`, `<repeat[1-3]:...>` | |
| Embedding | `<embed:filename>` | Textual inversion; mostly SD1-era. |
| LoRA | `<lora:file>`, `<lora:file:weight>`, `<lora:file:backbone:textenc>` | Position normally irrelevant; inside `<segment>`/`<object>`/`<fromto>`/`<alternate>`/regions it is scoped. Weights above 2 usually wreck quality. |
| Preset | `<preset:name>` / `<p:name>` | |
| Param | `<param[CFG Scale]:1>`, read with `<param:name>` | Case-insensitive, spaces ignored. Some params sectionalize (CFG, steps, sampler, scheduler, seeds). |
| Segment | `<segment:text,creativity,threshold>` | CLIPSeg refine; `yolo-model[-n]` for YOLO; `a\|b` groups areas. Defaults 0.6 / 0.5. |
| Clear | `<clear:text>` | Transparent areas; needs PNG. |
| Break | `<break>` | Manual CLIP 75-token chunk split. CLIP-only concept. |
| Bounding box | `<bbox[x1,y1,x2,y2]:prompt>` | Compiles to the model's JSON box format for models that support it. |
| Region | `<region:x,y,w,h,strength> text`, `<region:background>` | Fractions of the image. Small regions likely ignored. |
| Object | `<object:x,y,w,h,strength,strength2> text` | Region plus automatic inpaint. |
| Sections | `<base>`, `<refiner>`, `<video>`, `<videoswap>`, `<extend:frames>` | Route text to one stage only. |
| Comment | `<comment:text>` | Discarded. |

## Per-model behaviour (only what the source states)

- **Weighting:** CLIP models (Stable Diffusion) work well. T5-based models do not work very well. Newer LLM-text-encoder models treat it as a blunt multiplier on the conditioning (highly variable; many do nothing). Some models (for example Krea 2) have dedicated hacks. Not stated per other families: check the family guide.
- **`<break>`:** only meaningful for CLIP's 75-token chunking.
- **Regions:** SDXL-like models respond strongly; MM-DiT models (SD3, Flux) mostly use regions in early steps then ignore them. Small regions are weak everywhere.
- **`<bbox>`:** only for models with a registered JSON box format (core registers ideogram-4, flux-3, fibo-2, ming-image, moss-image, plus a generic one).
- **Embeddings:** common in the SD1 era, rare for newer models.

## Wildcards and randomness for the rewriter

- Listing: core's `WildcardsHelper.ListFiles` (names); the files are plain text in `Data/Wildcards`.
- Seeding: choices follow the main seed unless `Wildcard Seed` is set, so a rewrite that keeps `<random>`/`<wildcard>` tags keeps batch variety; a rewrite that resolves them removes it.
- Policy for the LLM (from the plan): use a wildcard or `<random>` only when the user asked for variety or it is clearly what they meant; never turn a specific request into a random one.

## Open questions

- Per-family weighting/regions support beyond the statements above: answered in each family guide.
- Exact escaping rules for tags inside JSON string values (Ideogram-style prompts): to test with core's tag processing.

# Prompt styles and automatic image-prompt rewriting: design record

Status (2026-11-24): **P0, P1, P2, P3a and P3b are built and merged; P4 is not built.** This file was the plan (written 2026-11-19) and is now the record of why the feature looks the way it does. The README section "Prompt styles" describes what the code does today; where this file and the README disagree, the README is right. It replaced S1-b and S1-c in `NEXT-PHASES.md`.

| Phase | Built? | PR |
|---|---|---|
| P0 Prompting guides (docs only) | Yes, in `docs/prompting/` | #74 |
| P1 Style registry and resolvers, both axes, Settings tab | Yes | #75 |
| P2 Chat-model addendum in turn setup | Yes (the mechanism; no addendum text is built in) | #79 |
| P3a Rewrite service: tag masking, formatters, retry and fallback, cache | Yes | #76 |
| P3b Automatic rewrite of every generation: hook, parameters, metadata, tool result, Enhance button | Yes | #77 |
| P4 More families, real-model evaluation, polish | **Not built** | none |

**What differs from the plan below (as built):**
- Only two styles are built in: `generic` and `ideogram-4`. `flux-3`, `fibo-2`, `moss-image`, `ming-image`, natural-language families and a video style (section 4) are not written; the researched guides exist (P0) but no style is derived from them yet.
- **Automatic rewriting is off by default** (`promptRewrite.auto`), not on. The chat-model addendum master switch is on by default, but no addendum exists, so nothing changes until a user writes one.
- From the switch list (3.4), built later: per-chat and per-assistant overrides, a composer control with a preview, and a switch each for chats and for the Generate tab and API. Still not built: a failure-policy setting (a failed rewrite always falls back to the user's wording, wrapped in valid JSON for a JSON-only model). What the first version built: the master setting, the per-generation parameters **LLM Auto-Enhance Prompt** and **LLM Prompt Style**, the `generate_image` `rewrite: "off"` argument, per-style on/off for chat-model styles, and per-model rules.
- Not built from 3.6: the writing model is not shown wildcard names or samples and does not choose wildcards; a `<wildcard:name>` is masked like any Swarm tag and expanded by core afterwards.
- Edit-before-generate in the Ask-mode approval card was not built (decision 2: show-after only).
- **Not verified on real hardware:** no style has been tried with a real image model and a real chat model; see section 7 and `docs/prompting/README.md` for the open real-model checks.
- The test sections below describe the plan; the as-built tests are `Tests/PromptStyleTests.cs`, `PromptRewriteTests.cs`, `PromptAutoEnhanceTests.cs`, `LlmAddendumTests.cs`, `PromptAssetTests.cs`, `RewritePolicyTests.cs` and the e2e specs `prompt-styles`, `prompt-rewrite`, `prompt-auto-enhance`, `rewrite-controls`, `llm-addendum`.
Standing rules apply: generic and modular, no core hacking, backend-agnostic (the extension only calls Swarm's generic LLM endpoints and never cares what engine is behind them), reuse core CSS/components, theme variables only, one PR per phase, browser-tested, no AI attribution lines.

> **Revision (user, 2026-11-19): two axes, both in scope.** We prompt properly for the specific **LLM** being used (chat model family) **and** properly for the specific **image model** a prompt is written for. They are independent and they compose. Section 3.0 is the new core of the design; the rest was adjusted to match.

> **Round 2 decisions (user, 2026-11-19), these override anything below that disagrees:**
> 1. One general toggle, "auto-enhance my prompts anywhere I write them": typing straight into the Generate tab, the assistant's image tool, and the Enhance button. (Section 3.2b.)
> 2. The rewritten prompt is shown **after** generation; no edit-before-generate for now.
> 3. If a rewrite fails: fall back to the **original wording**, but families that need JSON still get the original wording **wrapped in valid JSON**.
> 4. "SDXL" is just SDXL. Finetune-specific prompting (Pony, Illustrious, ...) is added **by the user** as a custom style and attached to a specific model, used whenever that model is.
> 5. Every model family gets researched from its official guide and written up as a reference doc (section 3.7). So there is no "which families first" decision; it is all of them, reviewed in the order you use them.
> 6. The LLM understands intent from what the user wrote ("put the logo top right") and produces the bbox layout itself; there is no special syntax the user has to learn.
> 7. Negative prompts are rewritten too, with model-specific negative prompting, for models that use one.
> 8. Video models are prompted properly like all the others.
> 9. The LLM must use Swarm's prompt syntax correctly, and use wildcards when they would help, being smart about it (section 3.6).

## 1. What the user wants

Two things, which compose:
1. **LLM axis:** whatever chat/utility LLM the user has selected, its system prompts are tuned to that model's family (and per-model overrides), for every task the assistant does.
2. **Image axis:** when a prompt is written for an image model, it follows that model's format (below).

When the user says "make me an image of a cat" and prompt enhancement is on, the extension looks at which image model is selected and has the LLM rewrite the prompt in the style that gives the best image for **that** model, in the format that model needs. It works with no thought from the user and is pre-built into the extension, but every part is overridable (styles, per-model overrides, the model doing the rewrite) and everything can be switched on and off. The existing image tool (`generate_image`) must use it.

Ideogram 4 is the model that shows why: it only does well with structured JSON (and supports bounding-box layout inside that JSON); plain text gives poor results.

## 2. What I verified (code and docs, today)

**Core already identifies the model family, officially.**
- Every Swarm image model has `T2IModel.ModelClass` (id such as `ideogram-4`, display name "Ideogram 4") and `ModelClass.CompatClass.ID` (groups a base model with its LoRA class etc.). Core detects them from the model file's header keys (`T2IModelClassSorter`). The route `ListT2IParams` already returns every model with its class id plus `model_classes` and `model_compat_classes`, so the browser knows it too.
- Server side the extension can read it from core's model set (`Program.T2IModelSets` is already used in `CoreBridge`); the exact lookup call is still to be confirmed when building.

**Core already knows bounding-box JSON formats per family.** `T2IBoundingBoxFormat` registers `ideogram-4`, `flux-3`, `fibo-2`, `ming-image`, `moss-image` and a generic one, attached to the compat class. Core compiles `<bbox[x1,y1,x2,y2]:description>` tags in a prompt into the model's element JSON (`T2IPromptHandling.ProcessPromptForModel`), and its Generate-tab region tool has an `ideogram` type. We reuse this as read-only interop and never modify it.

**Upstream publishes rewriter material** (useful source for bundled styles; licences to check before copying anything): the Ideogram 4 prompting guide (`github.com/ideogram-oss/ideogram4`, `docs/prompting.md`), FLUX.3 `system_prompts.json`, Ming-Image `t2i_rewriter_system_prompt.txt`, the MOSS Image `moss-image-prompt-enhancer` skill.

**Ideogram 4 format, from its prompting guide:**
- Trained only on structured JSON captions. Plain text "will not work and will likely trigger a safety warning" (false positives are common for non-JSON prompts).
- Top-level keys, in this order: `high_level_description` (string, optional, recommended), `style_description` (object, optional), `compositional_deconstruction` (object, required).
- `style_description`: exactly one of `photo` or `art_style`; strict key order. Photo: `aesthetics`, `lighting`, `photo`, `medium`, `color_palette`. Non-photo: `aesthetics`, `lighting`, `medium`, `art_style`, `color_palette`. `aesthetics`, `lighting`, `medium` are required when the object is present; `medium` is one of photograph, illustration, 3d_render, painting, graphic_design; `color_palette` optional, last, up to 16 uppercase `#RRGGBB`.
- `compositional_deconstruction`: `background` (string, required, first) then `elements` (list, required). Element `obj`: `type`, `bbox`, `desc`, `color_palette`. Element `text`: `type`, `bbox`, `text`, `desc`, `color_palette`. `bbox` and element palettes (max 5) optional.
- `bbox` is `[y_min, x_min, y_max, x_max]`, integers on a 0-1000 grid, origin top-left (matches core's Ideogram 4 format).
- Text to render goes in `text` elements with `\n` for line breaks. Compact serialization (`separators=(",",":")`, no ASCII escaping). No documented length limit.
- Not documented: whether an empty `elements` list is valid (matters for the fallback below); to be tested on a real model.

**What the extension does today.**
- The Generate-tab "Enhance Prompt" button sends `instructionId: 'prompt'` and never says which image model is selected, so it is model-blind. The `<llmprompt>` tag path is also keyed only on the chat LLM.
- `generate_image` (`ToolRisk.Write`) passes `prompt` through unchanged. The target model is known right after presets and params are merged, before the call into core: that is the hook.
- **LLM axis, what exists:** an assistant's instructions can carry several variants per instruction id, each with an `LLMModelMatcher` (kinds ranked by specificity: exact 100, family 75, provider 60, tag 40, glob 20, default; `PickBest` chooses). `LLMModelInfo.Family` (for example `qwen2`) is filled by the provider and can be null, for example for hosted models, so name globs (`claude-*`, `gpt-*`) are the fallback. Variants live inside each assistant, there are only demo variants bundled, nothing is organised by family, and they are used only for the instruction ids, not as a general layer on every task.
- **Image axis, what is missing:** nothing is keyed on the image model.
- **Swarm prompt syntax** (core doc `docs/Features/Prompt Syntax.md`): weighting `<weight[1.1]:text>` (and legacy `(text:1.2)` for SD1/SDXL-era models), `<alternate:a,b>`, `<fromto[0.5]:a,b>`, `<random:a,b,c>` (with `1-5` ranges and `random[1-3]` counts), `<wildcard:name>` / `<wc:name>` (multiples `wildcard[1-3]:`, exclusions `not=`), variables `<setvar[x]:...>` / `<var:x>`, macros, trigger phrase, `<repeat>`, embeddings, `<lora:name:weight>`, `<preset:>`, `<param[..]:>`, `<segment:>` refine, `<clear:>`, `<break>`, `<bbox[x1,y1,x2,y2]:desc>`, `<region:x,y,w,h,strength>`, object regions, base/refiner/video sections, comments. Important caveats from that doc: weighting works well only on CLIP-based models (T5 models do poorly, LLM-text-encoder models treat it as a blunt multiplier, some ignore it); `<break>` only matters for CLIP's 75-token chunks; regions behave very differently per model family (SDXL strong, MM-DiT weak). Random and wildcard choices are seeded by the main seed (or a Wildcard Seed).
- **Wildcards:** core lists them (`WildcardsHelper.ListFiles`, already returned by `ListT2IParams`): plain text files, one option per line, in `Data/Wildcards`.
- **Scale:** core registers about 70 model compat classes; roughly 45-50 are prompt-relevant image/video families (the rest are upscalers, segmentation, audio). Examples: SD1/2/3/3.5, SDXL, Flux 1/2/3, Chroma, Qwen-Image, HiDream, Z-Image, Ideogram 4, Krea 2, Fibo 2, Ming, MOSS, Lumina, PixArt, Sana, Kandinsky 5, Hunyuan Image/Video, Wan 2.1/2.2, LTX Video 1/2, Mochi, Cosmos, MiniMax image/video.
- Reusable pieces: `PromptAssets` (frontmatter markdown loader with bundled/shared/personal scopes, 64 KB cap, cache), `ToolConfigService` (per tool, per user, per assistant config), the Phase 2 hooks (a pre-hook can already rewrite tool arguments), the settings layers, the Settings dialog patterns.

## 3. Design

### 3.0 Two axes that compose

| | **LLM axis** (how to prompt the model doing the writing) | **Image axis** (what to write for the image model) |
|---|---|---|
| Keyed on | the LLM used for this turn: exact id, family (`LLMModelInfo.Family`), provider, tag, name glob (existing `LLMModelMatcher`) | the image model: exact name, core class id, compat class id |
| Applies to | every task the LLM does: chat, tool use, vision, caption, prompt rewrite, companion, summaries | only prompts written for an image (later video) model |
| Style kind | `kind: llm` | `kind: image` |
| Content | a model-specific **addendum**: how this family likes to be instructed, tool-use habits, output discipline ("output only the prompt"), format conventions, brevity for small models | the target model's prompt language: format, schema, layout rules, examples |

A rewrite request's system prompt is built from separate layers, each overridable on its own: **task instruction** (the assistant's existing text, persona included) + **LLM addendum** for the model doing the rewriting + **image style** for the target model. A user can therefore tune how their Qwen is instructed without touching the Ideogram schema, and the reverse.

Design choices (recommended, see questions 10-12):
- LLM styles are **addenda** layered onto the task instruction, not whole replacements. They are small, composable, and bundled updates cannot clobber a user's persona. Existing assistant variants keep working and still win when they are more specific (nothing to migrate).
- Chosen at turn time from the model actually used for that turn. Compare mode gives each lane its own addendum. The text is deterministic per model (no timestamps) so the local prefix cache stays stable.
- The same resolver rules as the image axis: per-model override, then family, then provider/glob, then default; personal over shared over bundled; "Customize" copies a bundled file to personal, "Reset" removes the copy.
- Unknown or new models fall back to the default, so nothing breaks.
- What LLM addenda actually contain (tool-call nudges, formatting habits of each family) has to be established by testing against real models; the bundled set starts small and grows only with evidence.
- Addendum tokens count toward the context estimate and are shown in the dry-run.

### 3.1 A "prompt style" is one file

Markdown with front matter, stored through the existing prompt-asset machinery as a new kind `style` (same scopes, cache, validation, editing plumbing; styles are never listed in the model-visible skill index).

```
---
name: Ideogram 4
description: Structured JSON captions with bounding-box layout.
kind: image                      # image | video | llm
matches: class:ideogram-4, compat:ideogram-4   # for kind: llm: family:qwen2, glob:claude-*, provider:..., exact:...
# kind: llm styles also say  tasks: chat, tools, vision, caption, rewrite   (default: all)
format: json                     # text | tags | json
formatter: ideogram-4            # optional built-in normalizer/validator
negative: separate               # none | separate | inline (the body then has a "Negative prompt" section: how this family wants it written)
supports: boxes, regions         # what the model honours: weights, break, regions, boxes, text-rendering (the LLM is told, and told what NOT to use)
guide: docs/prompting/ideogram-4.md   # the researched reference this style was built from
---
(the instructions for the rewriting LLM: the schema, rules, how to think about layout,
 examples, "output only the final prompt")
```

Resolution, first match wins:
1. a per-model override (exact model name -> a style, or "off");
2. the style matching the model's class id, then its compat class id;
3. optional family hint (see question 4);
4. the generic natural-language style (today's `prompt` instruction becomes this).

Within one id, personal beats shared beats bundled. "Customize" on a bundled style copies it to the personal scope; "Reset" deletes the copy. Bundled files are never edited.

### 3.2 One service, several entry points

`PromptRewriteService.RewriteAsync(request)` is the single implementation; every entry point calls it.

Stages:
1. **Decide:** switches (section 3.4), skip rules (prompt already valid for the target style, so it is idempotent; style set to off; no usable model).
2. **Resolve style** from the target model.
3. **Protect Swarm syntax:** `<lora:..>`, `<embed:..>`, wildcards, `<segment:..>` and similar tags are masked before the LLM sees the prompt and restored after. User-placed `<bbox[..]:..>` tags are layout instructions: for styles with boxes they go to the LLM as fixed elements (question 6).
4. **Build the request:** task instruction, then the LLM addendum for the rewriting model (axis A), then the image style (axis B), the original prompt as quoted data, target width/height (so the layout fits the aspect), negative prompt when the style uses one, optional persona tone and optional `style` memories.
5. **Call the LLM** through the generic dispatcher: the user's chosen rewrite model (default: follow the chat model), low temperature, token cap, timeout, no tools, no conversation id. No reliance on any backend's JSON mode.
6. **Normalize and validate** in the extension: strip fences and chatter; for `json` styles the family formatter parses, reorders keys to the model's order, drops unknown keys, clamps bboxes to the 0-1000 grid (y_min < y_max), checks enums, uppercases hex colours, caps palettes, serialises compactly. A validator then checks schema and length.
7. **Retry once** with the validation error appended; then the failure policy: use the original (with a warning), fail the call, or deterministic family fallback (a formatter may provide a minimal valid wrapper around the original text, so a JSON-only model never receives bare text; to be verified on a real model).
8. **Return** `{original, rewritten, styleId, scope, model, warnings, retried, usage}`.

Caching: a short in-memory cache on (prompt, style, size, rewrite model) so regenerate does not pay twice; one rewrite per tool call even for a batch of images.

Entry points: see 3.2b. The first plan listed four (the tool, the Enhance button, the `<llmprompt>` tag, chat); the built design replaced them with one hook before core parses the prompt, plus the Enhance button. Chat itself is never rewritten (it is the assistant's own writing).

### 3.2b Where it runs (revised after round 2)

**One hook covers every generation.** The extension already runs a function before core parses a prompt (`CoreBridge.RunBeforeCorePromptParsing`, used by `<llmprompt>`). Every generation passes through core's prompt handling: the Generate tab's Generate button, the assistant's `generate_image` tool (it calls core's generation route), and API clients. So "auto-enhance anywhere" is **one hook** in that spot (to verify: that the tool and API paths do hit it), not three separate integrations. Because it runs *before* core expands `<random:>` and `<wildcard:>`, the rewritten prompt keeps those tags and each image in a batch still varies.

**Control through core's own parameter system, not new chips.** The extension registers T2I parameters in its existing LLM parameter group (as it already does for the assistant id): **"Auto-enhance prompt"** (default / on / off) and **"Prompt style"** (auto / any style). They show in the Generate tab's parameter panel with core's own UI, save into presets (so a preset can pin model + style together, which is how a user attaches finetune prompting to a model), work in the tool's `params`, and in API calls. The global default lives in Settings.

**The Enhance Prompt button stays** as an explicit one-off that puts the result in the prompt box (now model-aware). To prevent enhancing twice, the service remembers the text it produced (short-lived, per user) and skips a prompt equal to a previous output; JSON families also skip text that is already valid for the style.

**Show-after.** The tool result carries original, rewritten, style and warnings. Image metadata stores both (through core's extra-metadata mechanism, to verify), so "Reuse parameters" and the image info panel show what was sent. The Generate tab's prompt box keeps what the user typed.

**Fallback (decision 3).** On validation failure after one retry: use the user's original wording. For `json` families the wording is deterministically wrapped in the family's minimal valid JSON (for Ideogram 4 roughly `high_level_description` and `background` set to the wording plus one `obj` element carrying it; exact minimum to confirm on a real model), so a JSON-only model never receives bare text. A warning is attached to the result.

**Costs to state in Settings:** one extra LLM call per generation request (cached for identical prompt/style/size so batches and regenerates are free), added latency before the image starts, and, with a local LLM and a local image model on one GPU, model swapping. The setup check warns about that combination.

**Finetunes (decision 4).** A user duplicates any style (or starts blank), edits it, and attaches it to a model: by exact model name or a name pattern (for example `*pony*`), from Settings or by pinning the "Prompt style" param in a preset. Attachments rank above class-based matching.

### 3.3 Prompt injection and safety
The original prompt is data inside the rewrite request, quoted and labelled; the rewriter has no tools; its output only ever becomes a prompt and is checked by the validator. Bundled styles cite their sources in the front matter.

### 3.4 Switches (as built; the plan listed more)
- **Image axis:** `promptRewrite.auto` (Settings > Prompt styles, **off** by default) and `promptRewrite.entryPoints`; each chat's and assistant's `autoEnhance`; the per-generation parameters **LLM Auto-Enhance Prompt** (`default` / `on` / `off`) and **LLM Prompt Style**; `generate_image`'s `rewrite: "off"`; per-model rules, which can also say "no style".
- **LLM axis:** **Tune prompts for the chat model** (`promptStyles.llmAddenda`, on by default), an on/off switch per style (`promptStyles.disabled`), and each assistant's `promptAddendum: false`.
- Precedence for a generation (`RewritePolicy`): the per-generation parameter, then the chat, the assistant, a model rule of no style, the entry point's switch, the master setting. The chat and assistant switches apply to the image tool only.

### 3.5 Settings: a "Prompt styles" tab
Two sections, "By chat model" (LLM axis) and "By image model" (image axis), each with its own list, overrides and resolve preview. Built from core components (Bootstrap list/segmented/switch, `.basic-button`, core form helpers, theme variables); new `llma-` CSS only where core has no equivalent, stated in the PR.
- Master and per-entry-point switches; rewrite model picker ("follow chat model" or any model the user's backends list); failure policy.
- Style list grouped by kind, with a scope badge, enable switch, Customize / Edit / Reset / Export / Import.
- Per-model overrides: pick a model from Swarm's list, choose a style or "off".
- **Resolve preview:** choose a model, see which style wins and why.
- **Test box:** type a prompt, pick a model, run the real rewrite, see the output, the validation result, and for JSON a formatted view.

### 3.6 Swarm syntax, wildcards and being smart

The rewriting LLM is given (as part of the image-axis layer, condensed from the core syntax doc) the Swarm syntax it may use and how each part behaves, plus per-style `supports:` flags so it only uses what the target model honours (for example: no `<weight>` on T5/LLM-encoder models, `<break>` only for CLIP models, regions only where they work, `<bbox>` only for families with a box format). It is told to be smart, in these rules:
- **Preserve what the user wrote:** `<lora:>`, `<embed:>`, `<preset:>`, `<param[..]:>`, `<segment:>`, `<region:>`, `<setvar>`/`<var>`, existing `<random:>`/`<wildcard:>` tags and weights are kept, with their content improved only where that is safe. Tag syntax must stay valid (the validator checks balanced tags and that JSON stays valid with tags inside string values).
- **Use wildcards when they help, not by default.** The request lists the user's available wildcard names (capped, with a few sample lines each when allowed). Using one is right when the user asked for variety or randomness ("any", "random", "surprise me", a batch where variety is the point) or when a wildcard clearly is the thing they meant. It must never turn a specific request ("a black cat") into a random one. Inline `<random:a,b>` is the same, for small choices. The aggressiveness is a setting (off / only when asked / smart).
- **Use syntax the model understands**, for example `<bbox[..]:..>` or boxes inside the family's JSON when layout was described, never invent tags, never use tags the Swarm doc does not list.
- Every rewrite is validated against these rules; a prompt that breaks tag syntax counts as a failed rewrite.

Privacy: wildcard names and sample lines are the user's files and go to the rewriting model, which may be hosted. A setting controls it (names only / names and samples / none).

### 3.7 Model prompting guides (research docs, one per family)

Each model family has a reference doc in the extension repo at `docs/prompting/<family>.md`, built from the model's **official** sources (its GitHub README/prompting guide, Hugging Face card, paper, official examples), plus `docs/prompting/_swarm-syntax.md` (the syntax above, per-family compatibility). Each doc has: sources with URLs and retrieval date; what the text encoder is and what that implies; the prompt format and schema; length limits; what works and what does not (weights, negative prompts, text rendering, layout/boxes, resolution notes, video-specific motion/camera language); two or three verbatim-safe example prompts of our own writing; open questions for anything the sources do not say. Style files cite their guide; users can open the guide from Settings. Where an official guide does not exist the doc says so and marks conclusions as community-sourced.

Process: read-only research, one agent per family group (families that share a guide are grouped, for example SD1/SD2, SDXL, SD3/3.5, Flux 1 / 2 / 3, Wan 2.1 / 2.2, LTX 1 / 2, Qwen-Image), results reviewed by you in the order you use the models, then the styles are derived from the docs. Nothing is copied wholesale from upstream system prompts without checking its licence.

## 4. Bundled styles

*Status: only `generic` and `ideogram-4` are built in (`ideogram-4` has golden unit cases for its formatter). Everything after them is not built.*

- v1 ships `ideogram-4` (JSON with layout) and `generic` (natural language, the current `prompt` behaviour). Both come with golden unit tests.
- Next, in order of evidence: `flux-3`, `fibo-2` (core already defines their bbox formats; I have not read their prompting docs), `moss-image`, `ming-image`, then natural-language families and a video style (the existing `video-prompt` instruction).
- Tag-style families (SDXL finetunes such as Pony or Illustrious) share one core class with other SDXL models, so class detection cannot tell them apart: see question 4.
- Each style is only declared good after a real run on a real model; the mock proves wiring, validation and fallback only.

Bundled **LLM** addenda: a `default` (empty) plus a small first set for the model families you actually use (question 11), each added only after testing on that model.

## 5. Phases (one PR each)

**P0: Prompting guides (built, #74; docs only, no code).** `docs/prompting/_swarm-syntax.md` and one guide per model family group (section 3.7), reviewed by you. Independent of everything else and read-only, so it can start first and run while P1 is built; it is what P1's bundled styles are derived from.

**P1: Style registry and resolvers, both axes (built, #75)** (no behaviour change yet). Add kind `style` to the prompt-asset loader with `kind: llm | image`; the LLM-family resolver (reusing `LLMModelMatcher`, with name-glob fallback when `Family` is unknown) and the image resolver (core model class); bundled `generic` and `ideogram-4`; the Settings tab with both sections: list, customize/reset, per-model overrides, resolve previews. Testable alone.

**P2: LLM axis wiring (built, #79; no addenda shipped).** Apply the LLM addendum in turn setup (`TurnSetupService`, so chat, headless and sessions all get it), for the model used in each turn (compare lanes separately); switches; dry-run shows the addendum and its token cost; deterministic addendum text. No addenda are bundled: one is added for a model family only after it has been tried on that model (none were, so none ship); the mechanism, an empty default and a docs example are what P2 delivers.

**P3: Image rewrite service and wiring (built as P3a #76 and P3b #77; the switches, the failure-policy setting and entry points B to D differ from this list, see the status at the top).** `PromptRewriteService`, the formatter/validator framework with the Ideogram 4 formatter, entry points A-D, the switches, rewrite-model and failure-policy settings, the test box, transparency (tool result, image metadata). Uses both axes.

**P4: More families, evaluation, polish (not built).** Additional image styles and LLM addenda, a golden prompt set run against real models (side-by-side with and without, judged by the user), the approval-card preview/edit if wanted, video styles.

## 6. Tests

The test plan was written before the code. What exists is listed in the status at the top (unit tests per area, and e2e specs that drive the mock LLM with header-only model files). The mock proves wiring, validation, fallback and the switches only. The live evaluation (a golden prompt set judged by the user against a real chat model and a real Ideogram 4) was never run and is not in CI.

## 7. Risks (all still open; none has been exercised with real models)
- Finetunes that need a different style than their class (question 4).
- LLM JSON errors on a JSON-only model: handled by the formatter, a retry, and the family fallback.
- Latency and cost of one extra LLM call per image; cache and one-rewrite-per-call limit it; the setup check warns when the rewrite model is local and shares GPU memory with image generation.
- New core model classes appear over time: unknown classes fall to the generic style, so nothing breaks.
- Copying upstream system prompts: check licences, cite sources, prefer writing our own.

## 8. Open questions

The round 3 and earlier questions are superseded by what was built (the decisions at the top and the README describe the result) (for example: the auto-enhance control is a core-style parameter and not a chip; wildcard policy became "hide wildcards from the writing model"; finetunes attach to a model by exact name or pattern through a rule). The record of the questions was removed because the answers live in the README. What is still open is only P4: which model families to write styles for next, and how to judge them on real models.

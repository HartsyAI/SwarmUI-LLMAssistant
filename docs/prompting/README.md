# Model prompting guides (P0)

Reference docs for the prompt-styles work, one per model family, written 2026-11-19 from public sources (official where they exist, marked community otherwise). Bundled styles are derived from these guides (see the status table below); none of them has been tested on a real model yet, and CI cannot do that, so treat a style as a starting point until someone has judged its output. Start with `_swarm-syntax.md`.

These guides cover only what shapes the prompt text: the text encoder, prompt format and schema, length limits, negative prompts, text rendering, layout boxes, and motion or camera language. They deliberately do not list generation settings (steps, CFG, sampler, resolution, fps, frame counts): the engine and Swarm's model classes already hold the per-model defaults, and a style reads facts such as the model class and whether it is a video model from there, not from these documents.

Caveat: most pages were read through a summarising fetch tool, so a schema's field names and key order must be checked against the source before a validator relies on them. Each guide lists its own open questions.

## Index (Swarm class ids in parentheses)

**Structured JSON / box families** (highest value for the rewriter)
- `ideogram-4.md` (ideogram-4): JSON only, `[y,x,y,x]` 0-1000 boxes.
- `flux-3.md` (flux-3-image-klein): caption plus JSON array of `{id,bbox,desc}`; core's `<bbox>` output does not match BFL's form.
- `fibo-2.md` (fibo-2): structured JSON; core's emitted keys and range unconfirmed against the official schema.
- `ming-image.md` (ming-image), `moss-image.md` (moss-image): official rewriters exist; core's compile covers only part of each schema.
- `krea-2.md` (krea-2), `minimax-h3-image.md` (minimax-h3-image).

**Stable Diffusion lineage**: `sd1-sd2.md`, `sdxl.md`, `stable-cascade.md`, `sd3.md`, `auraflow.md`, `pixart.md`, `sana.md`, `lumina-2.md`.

**Flux / Chroma**: `flux-1.md`, `flux-2.md`, `chroma.md`.

**LLM-encoder and newer image models**: `qwen-image.md`, `z-image.md`, `hidream.md`, `omnigen-2.md`, `hunyuan-image.md`, `kandinsky5-image.md`, `ovis.md`, `longcat-image.md`, `ernie-image.md`, `boogu.md`, `lens.md`, `anima.md`, `sensenova-u1_5.md`, `pixeldit.md` (also PiD, a decoder that is not prompted), `mage-flow.md`.

**Video**: `wan.md`, `hunyuan-video.md`, `hunyuan-video-1_5.md`, `ltx-video.md`, `mochi.md`, `cosmos.md`, `kandinsky5-video.md`, `minimax-h3.md`, `svd.md` (image-to-video, no text prompt).

Not covered (not prompted with image/video text): audio models (Ace Step, MiniMax Music, YuE2), SAM3, SeedVR2, ControlNets.

## Bundled style status

Style pack 1 added these styles under `Assets/prompt-assets/styles/`. A family not in the table has no bundled style yet and keeps the `generic` style (`ideogram-4` was added earlier). "Formatter" means the answer is checked and normalized in code; every style is **unverified on a real model**.

| Guide | Bundled style | Matches (core ids) | Formatter |
|---|---|---|---|
| `ideogram-4.md` | `ideogram-4` | class `ideogram-4` | `ideogram-4` |
| `flux-3.md` | `flux-3` | class `flux-3-image-klein` | `flux-3` (caption plus layout array) |
| `fibo-2.md` | `fibo-2` | class `fibo-2` | `fibo-2` |
| `ming-image.md` | `ming-image` | class `ming-image` | `ming-image` |
| `moss-image.md` | `moss-image` | class `moss-image` | `moss-image` (compiles the pixel draft) |
| `qwen-image.md` | `qwen-image` | classes `qwen-image`, `qwen-image-edit`, `qwen-image-edit-plus`, `qwen-image-2.1` | text |
| `flux-1.md` | `flux-1` | classes `Flux.1-dev`, `Flux.1-schnell` (not Kontext, Fill, depth, canny) | text |
| `flux-2.md` | `flux-2` | classes `flux.2-dev`, `flux.2-klein-4b`, `flux.2-klein-9b` | text |
| `z-image.md` | `z-image` | class `z-image` | text |
| `hidream.md` | `hidream` | classes `hidream-i1`, `hidream-o1` (not the edit class) | text |
| `sd3.md` | `sd3` | classes `stable-diffusion-v3-medium`, `-v3.5-large`, `-v3.5-medium` | text |
| `sdxl.md` | `sdxl` | classes SDXL base, 0.9 base, refiner, turbo (not the edit class) | tags |
| `sd1-sd2.md` | `sd1-sd2` | classes `stable-diffusion-v1`, `-v2-512`, `-v2-768-v`, `-v2-turbo` (not inpaint; not Alt-Diffusion) | tags |
| `chroma.md` | `chroma` | classes `chroma`, `chroma-radiance` (not Zeta Chroma) | text |
| `wan.md`, `hunyuan-video.md`, `hunyuan-video-1_5.md` | `video-prose` | compat `wan-21`, `wan-21-1_3b`, `wan-21-14b`, `wan-22-5b`, `hunyuan-video`, `hunyuan-video-1_5` | text |

Left out on purpose, and why: **SSD-1B and Alt-Diffusion** (the legacy `(word:1.2)` parser is off for them in core, and the rewriter cannot write `<weight>` tags, which it must treat as Swarm tags), **Zeta Chroma** (its guide says prompting is not stated), **Flux.1 Kontext, Fill, depth and canny and the SDXL and SD1 edit or inpaint classes** (they are edits, not text-to-image expansion), **LTX Video, Mochi, Cosmos, Kandinsky 5 Video and MiniMax H3** (their guides differ from Wan's on audio, length limits or negatives, so one shared video style would mislead). Every other family in the index above is not done yet. Thin guides (SD3, SDXL, SD1/2, Chroma, Z-Image, HiDream I1) were still written up, because they agree on the basics, but they rest on model cards and community notes and each guide says so.

## Findings that change the design

1. **Core's `<bbox>` compile is not complete or correct for several families.** Flux.3 (BFL wants a caption plus JSON array with `<id>` cites), Fibo 2 (official keys are `location`/`relative_size`/`color_scheme`; core emits `bounding_box` in 0-100), MOSS and Ming (core omits wrappers and text items). So the JSON-family styles should emit each model's full native form themselves rather than rely on `<bbox>` tags.
2. **Official prompting guidance is thin or absent** for most families (SD1/2/XL/Cascade, Mochi, Kandinsky, HunyuanImage, Chroma Radiance, Zeta Chroma, MiniMax H3 Image, Z-Image, HiDream-I1). Those styles will rest on model cards and community sources, flagged as such.
3. **Official prompt rewriters exist** for Qwen-Image (T2I and edit), Wan, HunyuanVideo 1.5, HiDream-O1, Flux.3 (upsampler card), Ming, MOSS, Fibo: the format they output is documented in each guide in our own words (no upstream text copied).
4. **Negative prompts and token limits are mostly "not stated"**; decide per family by test.
5. **Dead or blocked sources:** BFL's Klein prompting guide (404), Stability's SD3.5 guide (404), Wan 2.2 prompt guide (image-only page), several LTX pages (403). Re-check later.

## Before any style ships (real-model checks)

Ideogram empty-`elements` fallback; Flux.3 inline `<bbox>` vs BFL form; Fibo 2 numeric boxes; escaping Swarm tags inside JSON string values.

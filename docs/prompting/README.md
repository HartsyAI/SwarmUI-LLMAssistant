# Model prompting guides (P0)

Reference docs for the prompt-styles work, one per model family, written 2026-11-19 from public sources (official where they exist, marked community otherwise). Nothing here is a bundled style yet: styles are derived from these guides in P1/P4 and only after a real-model test. Start with `_swarm-syntax.md`.

Caveat for every guide: most pages were read through a summarising fetch tool. Field names, numbers (steps, CFG, sizes, token limits) must be spot-checked against the source repo before a validator or style relies on them. Each guide lists its own open questions.

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

## Findings that change the design

1. **Core's `<bbox>` compile is not complete or correct for several families.** Flux.3 (BFL wants a caption plus JSON array with `<id>` cites), Fibo 2 (official keys are `location`/`relative_size`/`color_scheme`; core emits `bounding_box` in 0-100), MOSS and Ming (core omits wrappers and text items). So the JSON-family styles should emit each model's full native form themselves rather than rely on `<bbox>` tags.
2. **Official prompting guidance is thin or absent** for most families (SD1/2/XL/Cascade, Mochi, Kandinsky, HunyuanImage, Chroma Radiance, Zeta Chroma, MiniMax H3 Image, Z-Image, HiDream-I1). Those styles will rest on model cards and community sources, flagged as such.
3. **Official prompt rewriters exist** for Qwen-Image (T2I and edit), Wan, HunyuanVideo 1.5, HiDream-O1, Flux.3 (upsampler card), Ming, MOSS, Fibo: the format they output is documented in each guide in our own words (no upstream text copied).
4. **Negative prompts and token limits are mostly "not stated"**; decide per family by test.
5. **Dead or blocked sources:** BFL's Klein prompting guide (404), Stability's SD3.5 guide (404), Wan 2.2 prompt guide (image-only page), several LTX pages (403). Re-check later.

## Before any style ships (real-model checks)

Ideogram empty-`elements` fallback; Flux.3 inline `<bbox>` vs BFL form; Fibo 2 numeric boxes; Wan 2.2 T2V 14B class id; klein steps (4 vs 8); escaping Swarm tags inside JSON string values.

# Ideogram 4 prompting guide

Status: P0 reference, first family. Sources (retrieved 2026-11-19; the second was obtained as a summary, so verify field details against the repo before shipping a validator):
- Official: https://github.com/ideogram-oss/ideogram4 , `docs/prompting.md`
- Core: SwarmUI `src/Text2Image/T2IBoundingBoxFormat.cs` (the `ideogram-4` box format), `docs/Features/Prompt Syntax.md`

## What matters

- Trained **only on structured JSON captions**, passed as a string. Plain text "will not work and will likely trigger a safety warning"; false positives are common for non-JSON prompts. Anything not JSON must be converted first.
- NSFW prompts are blocked (gray "Image blocked by safety filter" image).
- Serialize compactly: `json.dumps(..., separators=(",",":"), ensure_ascii=False)`; non-ASCII characters stay literal (escaped `\uXXXX` triggers a verifier warning).
- Length limits and negative prompts: **not documented**. Treat negative prompting as unsupported until a real test says otherwise.

## Schema (key order matters)

Top level, in this order:
1. `high_level_description`: string, optional, strongly recommended; one or two sentences.
2. `style_description`: object, optional.
3. `compositional_deconstruction`: object, **required**.

`style_description`: exactly one of `photo` (photographs) or `art_style` (everything else). `aesthetics`, `lighting`, `medium` are required when the object is present. `color_palette` optional and last.
- Photo order: `aesthetics`, `lighting`, `photo`, `medium`, `color_palette`.
- Non-photo order: `aesthetics`, `lighting`, `medium`, `art_style`, `color_palette`.
- `medium` examples: `photograph`, `illustration`, `3d_render`, `painting`, `graphic_design`.
- `color_palette`: up to 16 entries, uppercase `#RRGGBB` only (no `#fff`, no lowercase). Include background colours for a dark/light backdrop; include highlight and shadow colours to steer lighting.

`compositional_deconstruction`: `background` (string, required, first) then `elements` (list, required).
- Object element order: `type` (`"obj"`), `bbox`, `desc`, `color_palette`.
- Text element order: `type` (`"text"`), `bbox`, `text`, `desc`, `color_palette`.
- `bbox`: optional, `[y_min, x_min, y_max, x_max]`, integers on a 0-1000 grid, origin top-left (note y first; Swarm's `<bbox[x1,y1,x2,y2]:...>` tag is compiled to this by core).
- `desc`: detailed description of the element. `text`: the literal string to render; line breaks as `\n`. Element `color_palette`: optional, up to 5 entries.

## How the rewriter should think

- Always output a single JSON object, nothing else (no prose, no code fence in the final value).
- Decide `photo` vs `art_style` first; it fixes the key order of `style_description`.
- Put anything that must be drawn as lettering in `text` elements, spelled exactly as the user wrote it.
- Layout: only emit `bbox` when the user described placement ("logo top right", "text across the bottom") or the composition needs it; derive the box from the intent (top right is roughly `[0,650,250,1000]`). The business-card example in the official guide uses no boxes at all.
- Keep Swarm tags the user wrote (`<lora:>`, `<wildcard:>`, `<random:>`) inside string values only if they stay valid after JSON escaping; otherwise surface a warning. See `_swarm-syntax.md`.
- Weights, `<break>`, regions: not applicable (not a CLIP model); do not emit.

## Examples (our own wording, to be checked against a real model)

"a photo of a cat":
```json
{"high_level_description":"A close-up photograph of a tabby cat sitting on a sunlit windowsill.","style_description":{"aesthetics":"Warm, intimate, shallow depth of field.","lighting":"Soft natural window light from the left with gentle shadows.","photo":"85mm lens, f/1.8, eye-level, sharp focus on the eyes.","medium":"photograph","color_palette":["#C98B4B","#F2E6D0","#3A2E26"]},"compositional_deconstruction":{"background":"Blurred interior with warm wood tones and a soft white curtain.","elements":[{"type":"obj","bbox":[150,200,900,800],"desc":"A tabby cat with amber eyes sitting upright, whiskers backlit by the window."}]}}
```

"coffee shop poster, the words OPEN DAILY at the bottom":
```json
{"high_level_description":"A flat graphic poster for a coffee shop with a steaming cup and bold lettering at the bottom.","style_description":{"aesthetics":"Clean, retro, high contrast.","lighting":"Flat, even.","medium":"graphic_design","art_style":"Vintage screen-print poster.","color_palette":["#2B1B14","#E8C9A0","#D2492A"]},"compositional_deconstruction":{"background":"Solid cream background with a subtle paper grain.","elements":[{"type":"obj","bbox":[120,250,640,750],"desc":"A steaming coffee cup in dark brown with an orange accent stripe."},{"type":"text","bbox":[760,150,900,850],"text":"OPEN DAILY","desc":"Bold condensed sans-serif capitals, dark brown, centered."}]}}
```

## Fallback when a rewrite fails

For JSON-only models the original wording must still be wrapped, never sent as bare text. Proposed minimal wrapper: `high_level_description` and `background` set to the wording, plus one `obj` element carrying it. **Unverified:** whether an empty `elements` list is valid and whether this wrapper generates acceptably; confirm on a real model before the validator ships.

## Open questions

- Negative prompts and any length limit (not documented).
- Whether the shipped "magic prompt" templates in the repo are reusable (licence unchecked; we write our own style instead of copying).
- Exact behaviour of boxes that overlap or go out of range (we clamp to 0-1000 and enforce `y_min < y_max`, `x_min < x_max` in code).

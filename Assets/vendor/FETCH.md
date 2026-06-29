# Vendored front-end libraries

These libraries are committed into the extension so it has **zero runtime external dependencies** —
SwarmUI never needs internet access to load them. They are served locally from
`/ExtensionFile/LLMAssistantExtension/Assets/vendor/...` (registered via `OtherAssets` in
`LLMAssistantExtension.OnInit`) and lazy-loaded by `Assets/utils.js`.

Do **not** add `<script src="https://...">` references anywhere — that is the exact thing this
folder exists to avoid.

## Provenance (exact versions — bump deliberately)

| Library | Version | License | Source |
|---|---|---|---|
| marked | 15.0.11 | MIT | `https://cdnjs.cloudflare.com/ajax/libs/marked/15.0.11/marked.min.js` |
| highlight.js | 11.11.1 | BSD-3-Clause | `https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.11.1/highlight.min.js` + `styles/github-dark.min.css` |
| DOMPurify | 3.2.4 | Apache-2.0 OR MPL-2.0 | `https://cdnjs.cloudflare.com/ajax/libs/dompurify/3.2.4/purify.min.js` |
| KaTeX | 0.16.21 | MIT | `https://cdn.jsdelivr.net/npm/katex@0.16.21/dist/` (js, css, contrib/auto-render, fonts/*.woff2) |
| Mermaid | 11.6.0 | MIT | `https://cdnjs.cloudflare.com/ajax/libs/mermaid/11.6.0/mermaid.min.js` |

Note: KaTeX 0.16.21 is **not** published on cdnjs at `/KaTeX/0.16.21/` (404) — fetched from jsdelivr npm.
The previous code pointed at the dead cdnjs URL, so math rendering silently failed before this change.

## KaTeX fonts
Only the **20 `.woff2`** files referenced by `katex.min.css` are shipped (under `katex/fonts/`). The
`@font-face` rules list `woff2` first, so browsers never request the `.woff`/`.ttf` fallbacks — those
formats are intentionally omitted. If you bump KaTeX, re-extract the font list:
`grep -oE "KaTeX_[A-Za-z0-9_-]+\.woff2" katex/katex.min.css | sort -u`.

## License texts
Full upstream license texts are in `LICENSES/`.

## Updating a library
1. Download the exact pinned version from the source above into the matching folder.
2. For KaTeX, refresh `katex/fonts/` from the font list above.
3. Update the version in this file and verify the global is still exposed
   (`window.marked` / `window.hljs` / `window.DOMPurify` / `window.katex` / `window.mermaid`).
4. The `OtherAssets` registration auto-enumerates this folder, so no code change is needed to add/remove files.

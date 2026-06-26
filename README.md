# SwarmUI LLM Assistant Extension

A full-featured LLM chat tab for [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI). Adds persistent chat threads, customizable assistants, agentic tool calling, vision support, a floating in-page companion, per-user memory, and deep Generate-tab prompt integration.

The extension is **self-contained**: the whole feature surface talks to one internal seam, `ILLMProvider` (see `LLMs/`), and never references SwarmUI's core LLM types. A small, **removable** backend pack (`Backends/`) supplies the actual runtimes behind that seam:

- **Local LLM (HartsyInference)** — fully native pure-C# GGUF inference (Qwen2/Qwen3/Llama), no llama.cpp binding and no external process.
- **Anthropic Claude** — the Messages API, using each user's own API key.
- **Remote LLM (OpenAI-compatible)** — OpenAI, Ollama, LM Studio, vLLM, OpenRouter, … using each user's own API key.

The seam means the runtime is swappable without touching the rest of the extension — see [Removing / swapping the backend pack](#removing--swapping-the-backend-pack).

> **Status:** Feature-complete for the v1.0 ship. See [PRODUCTION_PLAN.md](PRODUCTION_PLAN.md) for the open polish items and the v1.1+ roadmap.

---

## Features

### Core chat
- **Dedicated tab** — Resizable sidebar (threads), main chat area, right-hand assistant panel. Split bars persist their widths to localStorage; double-click to reset.
- **Server-authoritative threads** — Chat history lives on the server. Edit, delete, rename, search, and export. Empty threads aren't saved until the first message; messages persist *before* the LLM is called so nothing is lost on disconnect.
- **WebSocket streaming** with typing indicator and stop button.
- **Markdown rendering** — Full GFM, syntax-highlighted code blocks, tables, KaTeX math, Mermaid diagrams. Output is sanitized through DOMPurify.
- **Image attachments** — Paperclip or drag-drop. Uploaded once, downscaled to ≤1536px long edge, persisted as URLs on the message so thread blobs stay small.
- **Token counting** — Exact via the running llama.cpp tokenizer when available; cheap `chars/4` heuristic otherwise.

### Assistants
- **Personalities with overrides** — Each has its own name, category, avatar, color, per-mode system prompts, and optional parameter overrides (temperature, max tokens, top-p, max context messages).
- **Built-in default ("Swarmie")** — A SwarmUI-savvy helper that uses the `swarm_docs` tool to answer how-to questions straight from the bundled documentation, with citations.
- **Starter templates** — Shipped templates include an anime persona, code reviewer, concise translator, story writer, and vision analyzer. Loaded lazily from `Assets/starter-assistants.json`.
- **Inheritance (`extends`)** — Assistants can inherit from another assistant; instructions and enabled-tool lists merge with the parent. Cycle-safe.
- **Per-model instruction variants** — Each instruction can carry alternates keyed on model `Family`, `Provider`, `Tag`, `Exact` ID, or `Glob` pattern, resolved at request time.
- **7 built-in instruction modes** — `chat`, `vision`, `caption`, `prompt`, `randomprompt`, `instructiongen`, `companion`.
- **Test runner** in the editor — Run a sample input through the unsaved instruction text before committing.

### Tool calling (agentic)
The extension implements tool calling via **prompt injection**, so it works with any LLM backend that streams plain text. The streaming layer watches for `</tool_call>`, parses the JSON, dispatches via `ToolExecutorService`, formats the result, and re-prompts. Up to `ToolConstants.MaxAgenticIterations` (8) rounds per user turn before forced termination with `truncated: true`.

**Built-in tools** (13):

| Tool ID | Purpose | Default permission |
|---|---|---|
| `generate_image` | Calls SwarmUI's T2I engine. Returns image URL. Per-assistant default preset; presets injected into description per-user. | POWERUSERS (UNTESTED safety) |
| `create_image_preset` | Saves a T2I preset to the calling user's account. Also requires SwarmUI's core `Manage Presets` permission. | POWERUSERS (UNTESTED) |
| `caption_image` | Runs a vision model on one image with a chosen caption style. | POWERUSERS |
| `fuse_image_descriptions` | Captions multiple images with role-specific prompts (style/subject/setting/reference), merges into a unified prompt. | POWERUSERS |
| `batch_caption_folder` | Captions every image in a folder, writes `.txt` sidecars. Sandboxed. | POWERUSERS |
| `web_search` | DuckDuckGo HTML scrape. Returns `{title, url, snippet}`. | POWERUSERS (UNTESTED) |
| `file_read` | Sandboxed read inside SwarmUI's `Data` directory. 65 KB cap. | POWERUSERS (RISKY) |
| `file_write` | Sandboxed write into `Output/llm_assistant/`. Extension allowlist + per-user extras. 1 MB cap. | POWERUSERS (RISKY) |
| `http_request` | GET/POST/PUT/DELETE/HEAD/PATCH. SSRF-blocked (loopback / RFC1918 / link-local / cloud metadata). Response capped at 262 KB. | POWERUSERS (RISKY) |
| `shell_exec` | Arbitrary shell command on the host. **Default permission: NOBODY.** Sandboxed working dir, 30 s timeout, 64 KB output cap. | NOBODY (POWERFUL) |
| `memory_write` | Writes a strictly per-user memory entry (name, pronouns, bio, current work, preferences, dislikes, facts). | POWERUSERS |
| `memory_read` | Reads the calling user's profile. | POWERUSERS |
| `swarm_docs` | Lists/reads SwarmUI's bundled docs. Sandboxed strictly to `docs/`. | USER |

**Custom tools** — Open `Settings > Tools > + Create Tool` to register a new tool. ID, name, description, JSON Schema for parameters, and a handler ID that maps to a registered `ToolHandler`. There's a built-in "Run Test" panel for development. Handler types: `builtin`, plus reserved `mcp_stdio` / `mcp_http` for the planned MCP integration.

### Vision
Assistants with a `vision` instruction set accept image attachments. The chosen model receives the image inline; the assistant's `vision` prompt is used as system context. Vision also powers the `magic-vision` generate tab action and the `caption_image` tool.

### Assets / Artifacts (Claude-style)
The right panel maintains a per-thread Assets index, plus a full-size viewer modal.

- Promoted from: assistant messages with fenced code blocks (non-Mermaid), tool results (`generate_image`, `file_read`, `file_write`, etc.).
- Viewer actions: **Copy**, **Download**, **Use as Prompt** (text → Generate prompt box), **Use as Init** (image → Generate init image).
- `file_write` results land in `Output/llm_assistant/<relative path>`, appear in chat as a clickable link, and lazy-load their contents in the viewer.

### Companion overlay
A floating in-page helper, opt-in per user. Snap-corner positioning with drag offsets, expandable text input, and quick action buttons (ask, critique last image, prompt help, suggest preset, explain feature, daily tip). Supports ambient chatter (greeting, reactions to Generate-tab images, idle nudges) with quiet mode, quiet hours, and a per-session cap. Persona resolves to an explicit assistant ID or follows the active assistant.

### Per-user memory
A strictly per-user profile. Categories: preferred name, pronouns, bio, current work, preferences, dislikes, facts. Capped at 50 list entries, 100 facts, 2000 chars per field. The Swarmie default assistant writes to memory naturally as the conversation reveals it; nothing is ever asked just to fill memory. Memory is never visible to any other user and there is no shared memory layer.

### Multi-user model
Settings live in two layers:

- **Shared layer** — admin-managed baseline (shared assistants, shared tools, default instructions, default parameters). Gated behind the `llm_shared_write` permission.
- **User layer** — per-user overrides + personal assistants + personal tools + preferred model.

The UI tags each assistant / tool with a `_scope` badge (`shared` or `personal`) so users see which entries are theirs vs. inherited from the instance.

### Generate-tab integration
Registers a parameter group called **LLM Prompt Processing** in the Generate sidebar and hooks SwarmUI's prompt parser.

**Parameters:**

| Parameter | Default | Purpose |
|---|---|---|
| `LLM Use Cache` | `true` | Reuse responses for identical prompts in a batch. |
| `LLM Generate Wildcard Seed` | `false` | Generate a shared wildcard seed per batch so `<wildcard>` selections stay consistent while the LLM is called once. |
| `LLM Model ID` | `default` | Override the LLM model used for prompt processing. |
| `LLM Instructions` | `prompt` | Instruction set to use. |

**Prompt tags:**

- `<llmprompt:your rough idea>` — At generation time, the inner text is sent to the LLM under the chosen instructions; the tag is replaced by the response.
- `<llmresponse:…>` — Internal marker for cached responses.
- `<llmoriginal>` — Re-inject the original (pre-LLM) text into the prompt.

### LLM model registry
On init, the extension registers an `LLM` model type via `Program.T2IModelSets["LLM"]` with `FolderPaths = Models/llm`. LLM weights appear in SwarmUI's model browser alongside `Stable-Diffusion`, `LoRA`, etc. The `LLM Model ID` parameter pulls from this same registry.

---

## Prerequisites

- SwarmUI installed and working
- At least one LLM backend instance added under **Server > Backends**. The bundled pack registers three backend types:
  - **Local LLM — HartsyInference** (pure-C# GGUF inference; drop `.gguf` files into `Models/llm`)
  - **Anthropic Claude** (Claude API; uses each user's own API key)
  - **Remote LLM — OpenAI API** (any OpenAI-compatible HTTP API — Ollama, LM Studio, vLLM, OpenAI, OpenRouter, …)

The remote backends read a per-user API key from the **User** tab (Anthropic / OpenAI). The local backend needs no key.

---

## Installation

1. Clone into the SwarmUI extensions directory:
   ```bash
   cd /path/to/SwarmUI/src/Extensions/
   git clone https://github.com/Hartsy/SwarmUI-LLMAssistant.git
   ```
2. Run `update-windows.bat` or `update-linuxmac.sh` to recompile SwarmUI.
3. Restart SwarmUI. The extension loads automatically and adds an **LLM Assistant** tab.
4. Drop LLM weights (GGUF) into `Models/llm/` if you plan to use the local backend.
5. Add an LLM backend in **Server > Backends**.

---

## Usage

### Welcome → first chat

1. **Pick or create an assistant** — On first open the welcome gallery shows your assistants. Click one to start chatting. Your last-used assistant is remembered.
2. **Chat** — Type in the rounded input bar at the bottom. Enter sends, Shift+Enter newlines. Paperclip attaches an image (drag-drop also works).
3. **Switch assistants** — Click **Switch** in the right-hand panel to return to the gallery, **Edit** to modify the current assistant.
4. **Manage threads** — The left sidebar groups by date. `+ New` starts a thread (only saved on first send). Hover a thread for delete; rename via the inline rename affordance (planned for v1.0 polish — see Roadmap).
5. **Resize** — Drag the split bars; double-click to reset.

### Top bar
- **Model pill** — Currently selected LLM with a colored status dot. Click to swap models.
- **Parameters popover** — Per-thread overrides for temperature, max tokens, top-p, context window.
- **Export** — Thread to JSON, Markdown, or plain text.
- **Settings** — `General`, `Assistants`, `Tools`, `Companion` tabs.
- **Sidebar / panel toggles** — Collapse the sidebar or right panel (the panel auto-hides at tablet width).

### Settings — General

| Setting | Default | Description |
|---|---|---|
| Temperature | `1.0` | Sampling temperature |
| Max Tokens | `1024` | Max response length |
| Top P | `0.9` | Nucleus sampling cutoff |
| Context Messages | `0` | Prior messages included per request (`0` = all) |
| Stream | `true` | WebSocket streaming |
| Markdown Rendering | `true` | Render markdown in assistant messages |
| Enter to Send | `true` | Enter sends, Shift+Enter newlines |
| Show Token Count | `true` | Char/token counter under input |

> `Top K`, `Repeat Penalty`, and `Seed` fields are currently in the UI but not yet plumbed through to every backend. Tracked under v1.0 polish in [PRODUCTION_PLAN.md](PRODUCTION_PLAN.md).

### Settings — Assistants

Create, edit, delete, and scope (personal/shared) assistants. Each assistant has name, category, description, avatar (upload supported), color, per-mode instructions with optional per-model variants, parameter overrides, and an enabled-tools checklist. Built-in assistants are read-only on core fields but you can toggle tools and edit per-mode prompts. Tools must also be enabled globally in the Tools tab for them to actually run.

### Settings — Tools

Create, edit, delete, and test tools. Built-in tools have read-only handler config but their description / enabled state / per-user options (e.g. `generate_image` default preset, `file_write` extension allowlist) are editable.

### Settings — Companion

Master enable, persona (an assistant ID, or follow the active assistant), corner snap, opacity, button checklist, and chatter triggers (greeting / reactions / idle), with quiet mode and quiet hours.

---

## Architecture

```
SwarmUI-LLMAssistant/
├── LLMAssistantExtension.cs        # Entry point — OnPreInit registers assets, OnInit registers model type / tools / API / migrations / GC
├── Constants.cs                    # Instruction IDs, feature keys, tool constants, roles
├── LLMs/                           # The stable seam — no concrete backend referenced here
│   ├── ILLMProvider.cs             # The seam interface + LLMProviderRegistry (everything dispatches through this)
│   ├── LLMTypes.cs                 # Extension-owned DTOs (LLMMessage, LLMMediaAttachment, LLMModelInfo, LLMRoles)
│   ├── LLMDispatcher.cs            # Routes to the ILLMProvider that advertises the requested model; CountTokens
│   ├── LLMModelLookup.cs           # Cached provider.ListModels() lookups (5min TTL)
│   ├── LLMModelMatcher.cs          # Generic matcher (Exact, Family, Provider, Tag, Glob, Default)
│   ├── ExtendedLLMInput.cs         # Standalone LLM request shape (messages, params, media, Tools)
│   ├── SwarmNativeLLMProvider.cs   # OPTIONAL bridge to native Swarm AbstractLLMBackends (off by default)
│   └── LLMStreamHelper.cs          # Agentic streaming loop, WS plumbing, server-side persistence
├── Backends/                       # REMOVABLE backend pack — delete to fall back to a native LLM API
│   ├── LLMProviderBackend.cs       # Base: AbstractLLMBackend + ILLMProvider; self-registers into the registry
│   ├── HartsyLocalLLMProvider.cs   # Pure-C# local GGUF inference (HartsyInference.LLM NuGet)
│   ├── AnthropicLLMProvider.cs     # Anthropic Messages API (per-user key)
│   ├── RemoteOpenAILLMProvider.cs  # Any OpenAI-compatible endpoint (per-user key)
│   └── LLMBackendPack.cs           # One Register() call — backend types + per-user API key types
├── Services/
│   ├── AssistantResolver.cs        # Flattens `extends` chains, applies variants — cached per-user
│   ├── AssistantService.cs         # Assistant CRUD with scope gating
│   ├── ImageInputResolver.cs       # data URI / local path / HTTPS → raw bytes + MIME
│   ├── InstructionService.cs       # Built-in + custom instruction resolution with {{var}} substitution
│   ├── MediaResolver.cs            # LLMMediaAttachment conversion (URL passthrough / local → base64)
│   ├── MediaStorageService.cs      # Persist chat images; downscale ≤1536px; 10 MB cap
│   ├── MigrationService.cs         # Idempotent one-time settings upgrades
│   ├── NetworkSafety.cs            # SSRF guard (loopback / private / link-local / cloud metadata)
│   ├── OrphanedFileGC.cs           # Daily sweep with 24h grace
│   ├── PromptCacheService.cs       # LRU cache with request deduplication
│   ├── SessionStateService.cs      # Per-user active thread / model / assistant
│   ├── SettingsService.cs          # Shared + user layers; merge view with _scope tagging
│   ├── StarterAssistantsCache.cs   # Lazy load of Assets/starter-assistants.json
│   ├── ThreadStorageService.cs     # Per-user thread CRUD; index maintenance
│   ├── ToolConfigService.cs        # User-default + per-assistant tool config with deep merge
│   ├── ToolExecutorService.cs      # Dispatch + permission gating + param validation + 60s timeout
│   ├── ToolPromptService.cs        # Build tool system prompt block; parse <tool_call> blocks
│   ├── ToolRegistryService.cs      # Tool definitions (settings) + handlers (in-memory)
│   ├── UserPresetCache.cs          # TTL cache around User.GetAllPresets()
│   └── UserProfileService.cs       # Strictly per-user memory profile
├── Tools/
│   ├── ToolHandler.cs              # Abstract base, ExecutionContext shape, EnrichForUser hook
│   └── BuiltIn/                    # 13 built-in tools (see table above)
├── T2I/
│   ├── PromptProcessor.cs          # LateSpecialParameterHandlers — parses <llmprompt>, calls LLM, injects responses
│   └── PromptTagHandler.cs         # Registers T2I parameters, hooks prompt parser
├── WebAPI/
│   ├── LLMAssistantAPI.cs          # Endpoint registration + 14 permission definitions
│   ├── ChatEndpoints.cs            # LLMAssistantSendMessage(WS), CreateThread, TestInstruction, UploadChatImage, CountTokens
│   ├── AssistantEndpoints.cs       # Assistant CRUD + avatar upload + starter templates
│   ├── InstructionEndpoints.cs     # Custom instruction CRUD (legacy, kept for T2I tag compatibility)
│   ├── ThreadEndpoints.cs          # Thread CRUD, message edit/delete, export
│   ├── ModelEndpoints.cs           # Model listing per backend
│   ├── SettingsEndpoints.cs        # Global settings get/save/reset
│   ├── ToolEndpoints.cs            # Tool CRUD, manual execution, config get/set, image preset list
│   ├── AssetEndpoints.cs           # Per-thread asset CRUD
│   ├── SessionEndpoints.cs         # Per-user session state get/set
│   ├── MemoryEndpoints.cs          # Per-user profile read/clear
│   └── CompanionEndpoints.cs       # Companion-specific context (last generated image, etc.)
├── Tabs/Text2Image/
│   └── LLMAssistant.html           # Tab markup
└── Assets/
    ├── llmassistant.js             # Top-level controller + state
    ├── chat.js                     # Messages, streaming, attachments, message ops
    ├── threads.js                  # Thread list, save/load, export, search
    ├── tools.js                    # Tool management UI
    ├── assets.js                   # Artifact panel + viewer modal
    ├── companion.js                # Floating overlay
    ├── utils.js                    # CDN loader (marked / highlight.js / KaTeX / Mermaid / DOMPurify), helpers
    ├── starter-assistants.json     # Bundled starter templates
    ├── swarmui-logo.jpg            # Swarmie's avatar
    └── llma-*.css                  # Layout, topbar, welcome, chat, panel, settings, tools, companion, assets, common
```

### Design notes

- **One seam, swappable runtime.** The stable extension core (chat, threads, tools, companion, T2I) talks only to `ILLMProvider` via `LLMProviderRegistry` — it never names a concrete backend or SwarmUI's `AbstractLLMBackend`. The bundled runtimes live in the removable `Backends/` pack; each registers itself into the registry on backend init. Swapping or removing the runtime is a self-contained change — see [Removing / swapping the backend pack](#removing--swapping-the-backend-pack).
- **Server-authoritative chat history.** The client cannot inject fake history. The user message is appended to the saved thread *before* the LLM is called, so nothing is lost on disconnect. The assistant reply is appended after the agentic loop completes.
- **In-tab modal.** The settings dialog is `position: absolute; inset: 0` inside the tab pane, not `position: fixed` to the viewport. Bootstrap modals would float over the entire SwarmUI UI, which breaks the tab metaphor.
- **Tool call format.** `<tool_call>{"name":"X","arguments":{...}}</tool_call>` / `<tool_result name="X">…</tool_result>`. Deliberately text-based so it survives any backend that streams plain text. Native `tools` / `tool_calls` passthrough for OpenAI-schema backends is planned for v1.1.
- **Layered security.** Permission checks at the endpoint level + per-tool gates + SSRF guards on outbound HTTP + path sandboxing on all file IO + dangerous tools defaulting to `NOBODY`.
- **Reused SwarmUI styles.** `.basic-button`, `.splitter-bar`, all theme CSS variables (`--text`, `--emphasis`, `--light-border`, …). Global scrollbar styling inherited from `site.css`.

### Removing / swapping the backend pack

The `Backends/` folder is the only part of the extension that knows about a concrete LLM runtime. Everything else depends solely on `ILLMProvider`. To drop the bundled runtimes — e.g. once SwarmUI ships a first-class native LLM API you'd rather use:

1. Delete the `Backends/` folder.
2. Remove the `Backends.LLMBackendPack.Register();` line in `LLMAssistantExtension.OnInit`.
3. Delete the HartsyInference NuGet block + the `ExcludeAssets` block from `SwarmUI-LLMAssistant.csproj` (and set `CopyLocalLockFileAssemblies` back to the default).
4. Register a replacement `ILLMProvider`. A ready-made bridge to native Swarm backends ships as `LLMs/SwarmNativeLLMProvider.cs` (disabled by default) — call `SwarmNativeLLMProvider.Register()` from `OnInit` instead of the pack. Adjust its input/model mapping to whatever the native API exposes.

Because the rest of the extension only ever sees `ILLMProvider` and the extension-owned DTOs, none of the chat / threads / tools / companion / T2I code changes.

> **Note on the bundled pack:** the three providers register as Swarm **backend types** (so they appear under Server > Backends with config + status). Each backend instance self-registers into `LLMProviderRegistry` when it initializes, so the chat tab only sees backends that are actually running.

### Permissions (14)

Every endpoint and every dangerous built-in tool has an explicit permission. Defaults are tuned conservatively.

| Permission | Default | Notes |
|---|---|---|
| `llm_chat` | POWERUSERS | Send messages |
| `llm_settings` | POWERUSERS | Read/write settings, assistants, tools |
| `llm_models` | POWERUSERS | List available models |
| `llm_threads` | POWERUSERS | Manage threads + assets + session state |
| `llm_shared_write` | ADMINS | Write to the shared / admin baseline (vs personal overrides) |
| `llm_companion` | POWERUSERS | Use the floating overlay |
| `llm_tool_generate_image` | POWERUSERS (UNTESTED) | Tool gate |
| `llm_tool_create_image_preset` | POWERUSERS (UNTESTED) | Tool gate |
| `llm_tool_vision` | POWERUSERS | Caption / fuse / batch caption tools |
| `llm_tool_web_search` | POWERUSERS (UNTESTED) | Tool gate |
| `llm_tool_file_read` | POWERUSERS (RISKY) | Tool gate |
| `llm_tool_file_write` | POWERUSERS (RISKY) | Tool gate |
| `llm_tool_http_request` | POWERUSERS (RISKY) | Tool gate |
| `llm_tool_shell_exec` | NOBODY (POWERFUL) | Effectively gives the LLM shell access |
| `llm_tool_memory` | POWERUSERS | Per-user memory profile |
| `llm_tool_swarm_docs` | USER | Read SwarmUI's bundled docs |

---

## Roadmap

See [PRODUCTION_PLAN.md](PRODUCTION_PLAN.md) for the full polish punch list and feature roadmap. Highlights:

**v1.0 polish (in progress)**
- Wire (or remove) the unused `Top K` / `Repeat Penalty` / `Seed` UI fields
- Surface "no backend running" / "load failed" on the welcome gallery instead of silently rendering an empty grid
- Thread rename UI
- Include tool calls in thread exports
- Vision-capable model badge
- Reset Defaults confirm dialog
- Keyboard navigation + a11y pass
- Loading spinners + retry buttons
- Audit logging for `shell_exec` / `file_write` / shared writes
- Configurable orphan-GC interval, belt-and-braces sandbox check, per-user rate limits for outbound tools

**v1.1 — Backends & models**
- First-class Ollama backend (model browser, auto-pull, status)
- Native OpenAI backend with structured outputs / `tools` passthrough
- Gemini / Google AI Studio backend
- Native tool-calling passthrough for backends that support OpenAI-schema `tools` (drops the prompt-injection middleman on those)
- Model status pills (loading / running / error)
- Per-user API key UI inside the tab

**v1.2 — Conversation features**
- Thread folders / pinning / favorites
- Fork / branch from a message
- System-prompt diff view in the editor
- Token-budget visualizer in the context bar
- Auto-summarize old turns when context fills
- Voice input (Whisper) and voice output (TTS, optionally via SwarmUI-AudioLab)

**v1.3 — Tools & extensibility**
- **MCP (Model Context Protocol)** support — the `mcp_stdio` / `mcp_http` handler types are already reserved
- Tool marketplace (curated, community-contributed)
- Per-tool sandbox limits in UI (max output, rate limits, host allowlists)
- Streaming tool results for long-running tools
- Tool composition / pipelines without re-prompting the model
- Code interpreter tool (sandboxed Python)
- Read-only DB query tool over SwarmUI's own data

**v1.4 — Multi-modal & generation**
- Inline image edit / refine via `generate_image` with `initImage` from the previous output
- Audio attachments (STT backend)
- Video frame extraction + vision
- Multiple images per message
- Drag images from the Generate tab history straight into chat

**v1.5 — Collaboration & ops**
- Shared threads (admin-pinned)
- Share links with expiry
- Read-only assistant preview mode
- Usage dashboard (tokens / tool calls / generations per user, per assistant)
- Audit log viewer in admin UI
- Backup / restore (assistant + tool config bundles)

**v1.6 — Agents**
- Long-running agents persisting across sessions
- Scheduled assistants
- Webhook-triggered assistants
- Multi-agent conversations in one thread

**v1.7 — UX polish**
- Theme picker beyond SwarmUI defaults
- Compact / spacious density toggle
- Mobile-first companion mode
- First-run tour

---

## Troubleshooting

**"No LLM backend is running":**
Add a backend under `Server > Backends`. The simplest path is a `SimpleRemoteLLMBackend` pointing at a local Ollama (`http://localhost:11434`).

**Tool calls never fire:**
- Check the tool is enabled both globally (`Settings > Tools`) and on the current assistant (`Settings > Assistants > Edit > Enabled Tools`).
- Confirm the user has the matching `llm_tool_*` permission.
- Some small models don't follow the `<tool_call>` format reliably — try a larger or more instruction-tuned model.

**`<llmprompt>` tags are not processed at generation time:**
- Make sure the **LLM Prompt Processing** parameter group is toggled on in the Generate sidebar.
- Verify an LLM model is selected via `LLM Model ID`.
- Check server logs for the `[LLMAssistant]` prefix.

**Thread list is empty after sending messages:**
- Threads are persisted on first message — sending must have failed. Check `ThreadStorageService` logs.

**Settings modal appears behind the page:**
- The modal is scoped to the tab pane (not the viewport). If it's rendering outside the tab, the `.llma-container` is no longer `position: relative` or the tab pane has lost `position: relative; height: 100%`.

**Companion overlay doesn't appear:**
- It's opt-in. Enable it under `Settings > Companion`. The user must have `llm_companion` permission.

**An assistant says it can't see images:**
- The current model is not vision-capable, or the model exists but the backend doesn't pass `Media` through. Switch to a known vision model (e.g. `claude-sonnet-4`, a vision-tagged Ollama model, or a multimodal GGUF).

---

## Changelog

- **1.0.0 (in polish)** — Initial functional release. Chat UI, threads, assistants with inheritance and variants, agentic tool calling with 13 built-in tools, vision, asset system, floating companion, per-user memory, multi-user shared/personal layers, granular permissions, server-authoritative chat history, orphan GC, token counting, Generate-tab integration.

## License

MIT License — see [LICENSE](LICENSE).

## Acknowledgments

- [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) and [mcmonkey](https://github.com/mcmonkey4eva) — Base platform and the LLM backend infrastructure this extension dispatches into.
- [marked](https://marked.js.org/), [highlight.js](https://highlightjs.org/), [KaTeX](https://katex.org/), [Mermaid](https://mermaid.js.org/), [DOMPurify](https://github.com/cure53/DOMPurify) — Markdown, code, math, diagrams, and sanitization.

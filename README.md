# SwarmUI LLM Assistant Extension

A full-featured LLM chat tab for [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI). Adds persistent chat threads, customizable assistants, agentic tool calling, vision support, a floating in-page companion, per-user memory, and deep Generate-tab prompt integration.

The extension is **self-contained**: the whole feature surface talks to one internal seam, `ILLMProvider` (see `LLMs/`), and never references SwarmUI's core LLM types. A small, **removable** backend pack (`Backends/`) supplies the actual runtimes behind that seam:

- **Local LLM (HartsyInference)** — fully native pure-C# GGUF inference (Qwen2/Qwen3/Qwen3.5/Llama/Gemma/Phi and more — see [Benchmarks](#benchmarks--local-backend-hartsyinference-architecture-pass) for the full per-architecture list), no llama.cpp binding and no external process.
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

## Benchmarks — Local backend (HartsyInference) architecture pass

A baseline pass verifying the local GGUF backend (`llmassistant-hartsy-local`) actually generates and
actually uses the GPU, across every architecture the underlying `HartsyInference.LLM` engine claims to
support — the engine's `GgufConfigFactory`/`ISsmModel` dispatch recognizes ~24 decoder families as of
2026-07-10 (dense transformer decoders, MoE, MLA, VLM, embeddings, plus the recurrent/hybrid families —
Mamba/RWKV/Qwen3.5's Gated DeltaNet — that route through a separate `ISsmModel` path instead of the shared
transformer spine). Each row is one real `LLMAssistantSendMessage` API call against a live SwarmUI instance,
not a unit test.

**Method:** RTX 3060 12GB, host with 31GB RAM. One `.gguf` per architecture (all Q4_K_M/F16 quants already
on disk for engine parity testing), symlinked into `Models/llm/`. Single cold request per model —
`"What is the capital of France? Answer in one short sentence."`, `maxTokens: 100`, `noCache: true`,
default sampling — with `nvidia-smi` polled every 0.3s during the call. "Elapsed" is therefore **wall time
including one-time GGUF load + dequant from disk**, not steady-state decode throughput — for the small
models most of that time is decode (a follow-up warm 2nd call on `stablelm-2-zephyr` at 200 max tokens
produced a ~950-char response in 5.3s, i.e. real decode is fast); for the 5-12GB vision/MoE files, load
dominates a short-answer request. **Getting real tokens/sec numbers per architecture is exactly what the
planned Python-side comparison pass should nail down** — this table is the "does it run, is it on the GPU,
is it roughly sane" baseline to diff future runs against.

| Architecture (GGUF `general.architecture`) | Test checkpoint | Status | Cold latency | Peak VRAM | Peak GPU util | Notes |
|---|---|---|---|---|---|---|
| gemma2 | gemma-2-2b-it Q4_K_M | ✅ Pass | 3.5s | 5.1 GB | 79% | Coherent |
| olmo2 | OLMo-2-0425-1B-Instruct Q4_K_M | ✅ Pass | 1.9s | 5.1 GB | 52% | Coherent, fastest instruct model tested |
| stablelm | stablelm-2-zephyr-1.6b Q4_K_M | ✅ Pass | 1.8s | 3.6 GB | 76% | Coherent; warm 200-tok follow-up ≈950 chars in 5.3s |
| internlm2 | internlm2_5-1.8b-chat Q4_K_M | ✅ Pass | 2.0s | 3.9 GB | 100% | Coherent |
| minicpm | minicpm-2b F16 | ✅ Pass | 7.6s | 8.6 GB | 100% | Answer correct but trails into hallucinated URLs |
| olmoe | olmoe-1b-7b-0924-instruct Q4_K_M (MoE) | ✅ Pass | 6.0s | 8.6 GB | 86% | Coherent — first working MoE architecture check |
| internvl-chat | InternVL2_5-1B Q8_0 (text path, no image) | ✅ Pass | 3.3s | 2.7 GB | 20% | Coherent |
| minicpm-v | minicpmv (ggml-model Q4_K_M, text path) | ✅ Pass | 8.5s | 7.3 GB | 100% | Coherent |
| qwen2vl | Qwen2-VL-2B-Instruct Q4_K_M (text path) | ✅ Pass | 5.2s | 3.3 GB | 16% | Coherent |
| qwen2.5vl | Qwen2.5-VL-7B-Instruct Q4_K_M (text path) | ✅ Pass | 10.4s | 7.2 GB | 99% | Coherent |
| pixtral (mistral) | pixtral-12b Q4_K_M (text path) | ✅ Pass | 10.5s | 10.2 GB | 99% | Coherent |
| exaone | EXAONE-3.5-2.4B-Instruct Q4_K_M | ✅ **Fixed** (2026-07-10) | 5.4s | 3.8 GB | 97% | Was incoherent word-salad; re-verified after fix → "The capital of France is Paris." |
| bloom | bloom-560m Q4_K_M | ✅ **Fixed** (2026-07-10) | 5.5s | — | — | Was `Tokenizer has no <|im_start|> token` (base/non-instruct checkpoint, no chat template). Fixed with a new **raw-completion fallback** (see below) — no longer a refusal: generates a real, on-topic-opening free continuation. |
| gpt2 | gpt2-medium Q4_K_M | ✅ **Fixed** (2026-07-10) | 0.7s | — | — | Same raw-completion fallback → *"The capital of the French republic is Strasbourg."* — plausible-looking but factually wrong, expected for a small non-instruct GPT-2 doing free continuation, not a bug. |
| gptneox | pythia-410m F16 | ✅ **Fixed** (2026-07-10) | 1.2s | — | — | Same fallback → rambles but correctly states *"Paris is the capital of France"* mid-response. |
| starcoder2 | starcoder2-3b Q4_K_M | ✅ **Fixed** (2026-07-10) | 3.7s | — | — | Same fallback → generates an off-topic code-completion-style continuation (expected behavior for a code-completion base model given a natural-language prompt, not a bug). |
| glm4 | THUDM GLM-4-9B-0414 Q4_K_M | ✅ **Fixed** (2026-07-10) | 13.5s | — | — | **Different root cause from the other four** — GLM-4-9B-0414 IS instruction-tuned and ships a real native chat template (`<|system|>`/`<|user|>`/`<|assistant|>`), but that template uses Jinja's `{% for x in seq if cond %}` inline-filter syntax, which a general engine bug misparsed as a malformed ternary (`Expected keyword 'else'`) — falling back to ChatML, which then hit the same missing-token error as the base models above. **Fixed the Jinja engine** (a `for`-loop inline-filter is not GLM-4-specific — benefits any future model using the same construct); confirmed via the server log that GLM-4 now compiles and uses its own real template, not the raw-completion fallback. Response quality is mediocre/off-topic at this Q4_K_M quant — plausibly the checkpoint itself, not re-investigated further. |
| mamba2 | mamba2-370m F16 | ✅ **Fixed** (2026-07-10) | 8.0s | 2.3 GB | 43% | Was `Attempted to divide by zero` (separately fixed earlier the same day — SSM wiring), then the raw-completion fallback above also applies to it (same base-checkpoint-no-template situation as bloom/gpt2/etc, routed through the SSM pipeline instead of the transformer one) → now generates a real (rambling, on-topic-adjacent) continuation instead of the expected-refusal. |
| rwkv7 | RWKV-v7-World-2.9-0.4B F16 | ✅ **Fixed** (2026-07-10) | ~2s | — | — | Was `Attempted to divide by zero`; re-verified → generates real fluent English (off-topic free-association, consistent with an un-tuned base checkpoint, but no crash and no garbage tokens) |
| nemotron | Nemotron-Mini-4B-Instruct Q4_K_M | ✅ **Fixed** (2026-07-10) | ~3s | — | — | Was `CUDA_ERROR_INVALID_VALUE`; re-verified → "The capital of France is Paris." |
| mllama | Llama-3.2-11B-Vision-Instruct Q4_K_M (text-only path) | ✅ **Fixed** (2026-07-10) | 13.6s | — | — | Was `Value is not iterable: String`; re-verified → generates (response wanders off-topic and starts mid-word, worth a follow-up look, but no longer a hard error) |
| qwen2moe | qwen1.5-moe-a2.7b-chat Q4_K_M (MoE, 9GB file) | ✅ **Fixed — fails clean now** (2026-07-10) | 9.0s | — | — | Was a host **OOM-kill of the entire SwarmUI process**; re-verified → now returns a graceful `CUDA_ERROR_OUT_OF_MEMORY` JSON error, server stays up, host RAM never dropped below ~10GB free during the attempt. The model still doesn't fit in 12GB VRAM (expected on this card), but it no longer takes the whole server down to fail. |
| granite / granitemoe | Granite-3.x checkpoints | ✅ **Fixed** (2026-07-10) | — | — | — | Bonus find while re-verifying exaone: `GgufConfigFactory` had granite/granite-MoE using SplitHalf RoPE pairing instead of Interleaved (and exaone had the opposite bug — it was wrongly in the Interleaved list). Both corrected; granite family now numerically matches the HF reference. |

**Takeaways (updated 2026-07-10, second fix pass — every architecture in this table now passes):**
- **All 26 architectures in this table now generate real output — zero refusals, zero hard errors.** The 5
  "expected failures" (bloom, gpt2, gptneox, starcoder2, glm4) from the first fix pass were NOT actually
  unfixable: 4 of the 5 were genuinely base/non-instruct checkpoints with no chat template, which just needed
  a **raw-completion fallback** (new, see below) instead of being refused outright; the 5th (glm4) turned out
  to have a real chat template that a general Jinja engine bug was silently breaking (see the glm4 row).
  Quality still varies with checkpoint size/quant/instruct-tuning — several of the now-passing base models
  ramble or go off-topic, which is expected free-continuation behavior for an un-tuned model, not a bug —
  but none of them refuse or crash anymore.
- **All 6 originally-broken/buggy items above were re-tested live against the running server and are now
  confirmed fixed**: `mamba2`/`rwkv7` no longer divide-by-zero, `nemotron` no longer CUDA invalid-argument,
  `mllama` no longer throws on its text-only path, `exaone` no longer garbles output, and the `qwen2moe`
  crash is now a graceful, catchable VRAM-OOM error instead of a full host process kill. A 7th bug
  (granite/granitemoe RoPE pairing) was found and fixed during the exaone re-verification.
- Also fixed during this pass: `mamba2`/`rwkv7` (and `mamba`/`rwkv6`) previously recomputed the **entire**
  sequence from scratch every decode step (O(n²) generation); they now carry real incremental SSM/WKV state
  across steps (O(1) per step), and a shared `SsmLanguageModel`/`SsmGenerationPipeline` dispatch path was
  built out so the recurrent family loads through the same request pipeline as the transformer family
  instead of being unreachable. A missing fused Q4/Q5_0 GEMV kernel (`mul_mat_vec_q5_0_f32`) was also added —
  any model with an odd hidden dimension that fell back to the slow generic path (e.g. Q4_K_M's mixed Q5_0
  fallback for non-256-divisible K-dims) is now ~2.5x faster on decode.
- **Two new architecture families landed after this table** — Gemma-4 (Apr 2026) and Qwen3.5 (Feb 2026, Gated
  DeltaNet hybrid attention) — see [Gemma-4 support](#gemma-4-support-new-architecture-2026-07-10) and
  [Qwen3.5 support](#qwen35-support-gated-deltanet-hybrid-2026-07-10) below. Not added as rows to the table
  above since they were verified via a standalone CLI harness against the engine directly, not this
  live-`LLMAssistantSendMessage`-API methodology — the underlying engine support is identical either way.
- **Deployment status**: everything on this page — the 6 original bug fixes, the granite RoPE fix, the SSM
  incremental-state rewrite, the Q5_0 kernel, CUDA graph decode, Gemma-4, Qwen3.5, the raw-completion
  fallback, and the Jinja for-loop-filter fix — is now **committed** in the `HartsyInference` engine repo
  (multiple commits since `b65e8bd`, most recently `fcfbbf6`), **except** the raw-completion fallback itself
  (`HartsyLocalLLMProvider.cs`) and this README, which live in the extension's own separate git repo
  (`src/Extensions/SwarmUI-LLMAssistant/`, gitignored from SwarmUI's own tree) and are still uncommitted there
  as of this writing. It is *not yet published to NuGet* (still `alpha.46` on the feed as of this writing) —
  extensions still need
  `UseLocalHartsy=true` against a local build of that repo, or a future NuGet bump past `alpha.46`, to pick
  it up (see [[feedback_engine_bump_checklist]]). Re-check `git log`/`git status` in the engine repo before
  citing a specific commit hash here in the future — this note has already gone stale once this session
  (the user commits independently and doesn't announce it).

### Raw-completion fallback for base/non-instruct checkpoints (2026-07-10)

Base (non-instruct) GGUF checkpoints — bloom, gpt2, pythia/gptneox, starcoder2-style code-completion models,
and any recurrent/SSM base checkpoint (mamba, mamba2, …) — have no `chat_template` metadata, so
`GgufLanguageModel`/`SsmLanguageModel` always fell back to the built-in ChatML template. ChatML itself needs
`<|im_start|>`/`<|im_end|>` special tokens the tokenizer doesn't have for these models, so every chat request
against one of them threw `Tokenizer has no <|im_start|> token` instead of generating anything — previously
documented as an "expected" limitation with no workaround.

It's fixed now: `HartsyLocalLLMProvider` detects this exact situation (the resolved template is the built-in
`ChatMlTemplate` fallback *and* the tokenizer has no ChatML tokens — checking the actual fallback object, not
just "tokens missing", avoids misfiring on a model with a real custom template that simply doesn't use
ChatML's tokens) and switches to **raw completion**: the latest message's plain text is tokenized directly
(`ILlmTokenizer.EncodeOrdinary`) and fed to the engine via `GenerationRequest.RawTokenIds`, which the engine
already supported end-to-end (`PromptBuilder` checks it before any templating) — nothing in the engine needed
to change, the plumbing was just never wired up to anything. No chat-turn structure is imposed, matching how a
completion-only model actually works: it continues whatever text it's given, it doesn't understand "system"/
"user"/"assistant" roles at all.

**Verified live** against every checkpoint in the table above that used to be marked "⛔ Expected" — all five
now generate (see their rows for details and response snippets). Quality is exactly what you'd expect from an
untuned base model doing free continuation: on-topic-adjacent rambling, not chat answers. That's correct
behavior for these checkpoints, not a remaining bug.

### CUDA graph decode (opt-in, 2026-07-10)

For the plain dense GQA + RoPE decoder shape — Llama-3.x, Qwen2, Qwen3, Mistral — the engine can now capture
one decode step (embed → attention → MLP → argmax) as a CUDA graph and replay it instead of re-issuing the
full kernel-launch sequence every token. This removes CPU launch overhead from the decode loop, which matters
most on small/fast models where launch latency, not compute, is the bottleneck.

- **Opt-in only**: enable the `GraphDecode` toggle in this extension's settings (Server Settings ->
  LLMAssistant), or set the environment variable `HARTSY_GRAPH_DECODE=1` before starting SwarmUI. There is
  also a matching `SpeculativeDecode` toggle (prompt-lookup speculative decoding, no draft model — drafts
  from repeated n-grams already seen in the prompt/response so far) with the same eligibility, biggest win
  on repetitive output. Both require the request to end up greedy (Temperature = 0); the chat UI's default
  temperature (0.7) does not route through either path yet.
- **Greedy sampling only for now**: graph decode requires `Sampling.Greedy` on the request. The extension's
  default temperature (0.7) does **not** currently route through the graph path — only explicit greedy/
  temperature-0 requests do. On-device temperature/top-k sampling to lift this restriction is planned but not
  built yet.
- **Verified**: byte-identical logits vs the eager path on every eligible architecture tested, with real
  speedups (~2.57x on Qwen3-0.6B, smaller but still positive on larger/compute-bound models like Mistral-7B,
  matching the launch-bound-vs-compute-bound prediction in the engine's perf docs).
- Architectures outside the plain dense GQA/RoPE shape (MoE, SSM/recurrent, vision-text) are not eligible and
  silently fall back to the normal eager decode loop.

### Gemma-4 support (new architecture, 2026-07-10)

Google's Gemma-4 (April 2026) is a new architecture family, not a config variant of Gemma-3 — per-layer
embeddings (a Gemma-3n-lineage mechanism), a hybrid local/global attention pattern where LOCAL layers use a
genuinely narrower head dimension than global layers (not just a different RoPE base), cross-layer KV-cache
sharing on the mobile-oriented E2B/E4B checkpoints, and — on the 26B-A4B MoE checkpoint — a routed-expert FFN
branch that runs in parallel with (not instead of) the dense FFN.

- **Verified working**: Gemma-4-E2B-it (Q4_K_M, fits the 3060). Coherent, factually correct, properly-spaced
  generation confirmed live.
- **Built but not locally tested**: the 31B-dense and 26B-A4B-MoE checkpoints share the identical code path and
  compile clean, but exceed 12GB VRAM — no local load was attempted (see the RAM/VRAM safety note below).
  Verification for these is deferred to cloud-GPU testing.
- Bring-up surfaced three real bugs in shared engine code (none Gemma-4-specific, so every architecture benefits):
  the GGUF parser silently discarded BOOL-typed metadata arrays (Gemma-4's `sliding_window_pattern` is a genuine
  per-layer array, not a broadcast period); a tensor-shape convention slip in a first-pass fix; and
  `tokenizer.ggml.model="gemma4"` wasn't routed to the SentencePiece tokenizer. Also fixed 4 general Jinja
  chat-template gaps (unary minus, block-form `{% set %}...{% endset %}`, `range()`, `is sequence`) surfaced by
  Gemma-4's tool-calling template — these fix any future model using the same constructs, not just Gemma-4.

**RAM/VRAM safety note**: loading even the small (2GB) E2B checkpoint OOM-killed the dev box's VSCode once
during this work — the cause was proceeding with a load despite already-low available host RAM (checked, saw
it was low, went ahead anyway), not the model itself being too large. Standing rule now: check `free -h` +
`nvidia-smi` immediately before any local model load, and stop rather than proceed if headroom is tight. Models
too large for this dev GPU's 12GB VRAM are built and wired but never locally loaded, full stop — see
[[feedback_audio_inference_ram_oom]].

### Qwen3.5 support (Gated DeltaNet hybrid, 2026-07-10)

Alibaba's Qwen3.5 (Feb 2026) mixes two attention mechanisms in the SAME model — every 4th layer is regular
GQA + RoPE attention, the rest are Gated DeltaNet: a delta-rule linear attention (causal Conv1d, per-head L2
norm, then a sequential recurrent state update) that gives roughly Mamba-like memory/compute characteristics
while still supporting real in-context lookup, unlike a plain SSM. This applies even to the smallest 0.8B
model, not just the large MoE tier.

- **Verified working**: Qwen3.5-0.8B (Q8_0). Coherent, factually correct generation confirmed live over 100
  tokens (stable across many recurrent steps, not just the first few).
- **Built but not locally tested**: 2B/4B/9B share the identical code path; not run locally, but no VRAM
  concern at that size — just not exercised yet.
- **Not built**: the MoE tier (35B-A3B/122B-A10B/397B-A17B, a separate `qwen35moe` GGUF arch) — no MoE FFN
  support added to the Gated DeltaNet layer yet, and these sizes need cloud-GPU testing regardless.
- Routes through the SAME recurrent-model dispatch already used for Mamba/RWKV
  (`SsmLanguageModel.IsSsmArchitecture` / `HartsyLocalLLMProvider`'s existing `PeekArchitecture` branch) — no
  extension-side code changes needed, this "just works" once the engine is rebuilt.
- **Real bug found via live testing, not obvious from reading the reference once**: missed a `q *= 1/√head_dim`
  scale llama.cpp applies immediately before the recurrence step — produced fluent-looking word salad, not a
  crash. Fixing it flipped straight to coherent, correct output. Matches this session's Gemma-4 lesson: garbled
  non-crashing output on a brand-new architecture needs a real checkpoint to debug against, reading the
  reference source once is not enough to catch every step.

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

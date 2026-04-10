# SwarmUI LLM Assistant Extension

> [!WARNING]
> **WORK IN PROGRESS — NOT YET FUNCTIONAL.**
> This extension depends on SwarmUI's native LLM backend infrastructure (`AbstractLLMBackend`, the `LLM` model type, and per-backend model discovery), which is still being implemented upstream. Until that work lands, this extension will load but cannot actually send messages to a model — the chat UI, threads, assistants, and tool system are all wired up and ready, but there are no LLM backends for the dispatcher to route requests to. Track SwarmUI's LLM backend development before expecting this extension to do anything useful.

A full-featured LLM chat tab for [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) that adds persistent chat threads, customizable assistants, tool/function calling, vision support, and deep Generate-tab prompt integration — all using SwarmUI's native LLM backend registry. The extension itself does not manage backends; it only talks to whatever LLM backends SwarmUI already knows about.

## Features

- **Full Chat Tab** — Dedicated tab with a resizable sidebar (thread list), message area, right-hand assistant panel, and split bars that save their widths to localStorage
- **Persistent Threads** — Chats are saved per-user. Search, rename, and delete from the sidebar. Empty threads are not saved until the first message is sent
- **Assistants** — Multiple customizable personalities, each with its own avatar, color, category, per-mode system prompts, and parameter overrides (temperature, max tokens, top-p)
- **Instruction Modes** — 6 built-in instruction types per assistant: `chat`, `vision`, `caption`, `prompt`, `randomprompt`, `instructiongen`
- **Tool Calling** — Agentic loop with prompt-injection-based tool calls. Ships with 3 built-in tools: `generate_image` (calls SwarmUI's T2I engine), `web_search`, and `file_read` (sandboxed). Up to 8 iterations per user turn. Extensible via a `ToolHandler` base class
- **Vision Support** — Attach images to messages, send them to vision-capable models, generate captions, and feed them back into T2I generation
- **Streaming Responses** — WebSocket-based token streaming with typing indicator and stop button
- **Markdown Rendering** — Full markdown, syntax-highlighted code blocks, tables, KaTeX math, and Mermaid diagrams inside bubbles
- **Generate Tab Integration** — Use `<llmprompt:your prompt>` tags directly in the positive prompt to have an LLM rewrite that segment at generation time
- **LLM Model Type** — Registers a new `LLM` model type in SwarmUI's model registry (alongside `Stable-Diffusion`, `LoRA`, etc.), backed by `Models/llm/`
- **Prompt Caching** — Cache LLM responses for identical prompts during batch generation
- **Wildcard Seed Sync** — Generate a consistent wildcard seed per batch so `<wildcard>` selections match across a batch while the LLM is still called only once

## Prerequisites

- SwarmUI installed and working
- **One or more LLM backends registered in SwarmUI** — this extension does not install or manage LLM runtimes. Once SwarmUI's native LLM backend support ships, you will be able to add backends in `Server > Backends` the same way you add image generation backends today

## Installation

1. Clone into the SwarmUI extensions directory:
   ```bash
   cd /path/to/SwarmUI/src/Extensions/
   git clone https://github.com/Hartsy/SwarmUI-LLMAssistant.git
   ```

2. Run `update-windows.bat` or `update-linuxmac.sh` to recompile SwarmUI.

3. Restart SwarmUI. The extension loads automatically and adds an **LLM Assistant** tab to the Text2Image tab group.

4. Drop LLM model files into `Models/llm/` (the folder is created automatically on first run).

5. Add an LLM backend in `Server > Backends` (once SwarmUI exposes this).

## Usage

### LLM Assistant Tab

1. **Pick or create an assistant** — On first open, the welcome gallery shows your assistants. Click one to start chatting. Your last-used assistant is remembered across sessions.
2. **Chat** — Type in the rounded input bar at the bottom. Enter to send, Shift+Enter for newline. Click the paperclip icon to attach an image.
3. **Switch assistants** — Click **Switch** in the right-hand panel to return to the welcome gallery, or **Edit** to modify the current assistant.
4. **Manage threads** — The left sidebar lists all chats grouped by date. Click `+ New` to start a fresh thread with the current assistant (the thread is only saved once you send the first message). Hover a thread to reveal its delete button.
5. **Resize layout** — Drag the split bars between sidebar / main / panel to resize. Double-click a split bar to reset. Widths are saved to localStorage.

### Top Bar Controls

- **Model pill** — Shows the currently selected LLM model with a colored status dot. Click to open a dropdown and switch models.
- **Parameters popover** — Per-thread overrides for temperature, max tokens, top-p, and context window size.
- **Export** — Export the current thread as JSON, Markdown, or plain text.
- **Settings** — Opens the settings modal with `General`, `Assistants`, and `Tools` tabs.
- **Sidebar toggle** / **Assistant panel toggle** — Collapse sidebar or right panel (panel toggle auto-shows on tablet-width viewports).

### Assistants

Open `Settings > Assistants` to create, edit, or delete assistants. Each assistant has:

- **Name, category, description, avatar, color** — Shown in the welcome gallery and right panel
- **Per-mode system prompts** — Separate instructions for chat, vision, caption, prompt, randomprompt, and instructiongen modes
- **Parameter overrides** — Optional temperature, max tokens, and top-p that override global defaults for this assistant
- **Enabled tools** — Checklist of tools this assistant can call. Global tools must also be enabled in the Tools tab for them to actually run
- **Built-in assistants** are read-only on their core fields but you can toggle enabled tools and per-mode prompts

### Tool Calling

The extension implements tool calling via **prompt injection** (no native OpenAI `tools` / `tool_calls` fields required), so it works with any LLM backend that streams text. Flow:

1. When the user sends a message, the enabled tools for the current assistant are compiled into a system prompt block listing each tool's name, description, and JSON Schema parameters.
2. The model is instructed to emit tool calls as `<tool_call>{"name":"TOOL","arguments":{...}}</tool_call>` blocks in its response.
3. The streaming layer watches for `</tool_call>` and, on match, stops the current generation round, parses all complete calls, and dispatches them to the registered `ToolHandler` via `ToolExecutorService`.
4. Results are formatted as `<tool_result name="TOOL">...</tool_result>` and appended to the chat history.
5. The backend is re-invoked with the extended history; the model reads the tool result and continues.
6. This loops up to `ToolConstants.MaxAgenticIterations` (8) times before the backend forcibly terminates with a `truncated: true` response.

**Built-in tools:**

| Tool ID | Handler | Description |
| --- | --- | --- |
| `generate_image` | `GenerateImageTool` | Generates an image via SwarmUI's T2I engine and returns the image URL. Useful for assistants that suggest visual content mid-conversation |
| `web_search` | `WebSearchTool` | HTTP-based web search, returns `{ title, url, snippet }` results |
| `file_read` | `FileReadTool` | Sandboxed file read from within SwarmUI's data directories. Rejects `..` traversal and absolute paths outside the data root |

**Custom tools:** Open `Settings > Tools > + Create Tool` to register a new tool. You can set an ID, name, description, JSON Schema for parameters, and a handler ID that must map to a registered `ToolHandler`. There's a built-in "Run Test" panel that lets you execute a tool with arbitrary arguments for development.

Custom handler types are exposed via the `handlerType` field (`builtin`, and reserved values `mcp_stdio`, `mcp_http` for a planned MCP integration).

### Vision

Assistants with a `vision` instruction set can accept image attachments. Click the paperclip icon in the input bar to upload an image — it gets encoded and sent alongside the message text to the selected LLM model. Uses the assistant's `vision` instruction as the system prompt instead of `chat`.

Vision is also used by the `magic-vision` generate tab action to caption an existing image.

### Generate Tab Integration

The extension registers a parameter group called **LLM Prompt Processing** in the Generate tab sidebar and hooks into SwarmUI's prompt parsing system:

**Parameters:**

| Parameter | Default | Description |
| --- | --- | --- |
| `LLM Use Cache` | `true` | Cache LLM responses for identical prompts during batch generation |
| `LLM Generate Wildcard Seed` | `false` | Generate a shared wildcard seed per batch so `<wildcard>` selections stay consistent while the LLM is called once |
| `LLM Model ID` | `default` | Override the LLM model used for prompt processing |
| `LLM Instructions` | `prompt` | Which instruction set to use (defaults to the built-in `prompt` mode) |

**Prompt tags:**

- `<llmprompt:your rough idea>` — At generation time, the text inside this tag is sent to the LLM using the chosen instructions, and the tag is replaced with the response
- `<llmresponse:...>` — Marks an LLM-generated segment (used internally for caching)
- `<llmoriginal>` — Re-adds the original unprocessed tag content back into the prompt. Useful if your model also benefits from the raw tag-style prompt

**Backward compatibility:** When the MagicPrompt extension is **not** installed, the same handlers are also registered under `<mpprompt>`, `<mpresponse>`, and `<mporiginal>` aliases so prompts authored for MagicPrompt continue to work.

## Architecture

```
SwarmUI-LLMAssistant/
├── LLMAssistantExtension.cs        # Extension entry point (OnPreInit/OnInit)
├── Constants.cs                    # Instruction IDs, feature keys, tool constants, roles
├── LLMs/
│   ├── LLMDispatcher.cs            # Selects first running AbstractLLMBackend
│   ├── ExtendedLLMInput.cs         # Extends LLMInput with tools, history, assistant ctx
│   └── LLMStreamHelper.cs          # Agentic streaming loop + WS message plumbing
├── Services/
│   ├── SettingsService.cs          # Global settings, default assistant, tool seeding
│   ├── AssistantService.cs         # Assistant CRUD, per-assistant tool enable
│   ├── InstructionService.cs       # Built-in + custom instruction sets
│   ├── ThreadStorageService.cs     # Per-user thread persistence
│   ├── MigrationService.cs         # Version upgrades (tool seeding, enabledToolIds)
│   ├── PromptCacheService.cs       # Response cache keyed on prompt+model+instructions
│   ├── ToolRegistryService.cs      # Tool CRUD + handler lookup
│   ├── ToolExecutorService.cs      # Execute by tool ID, validate args, wrap errors
│   └── ToolPromptService.cs        # Tool system prompt builder + <tool_call> parser
├── Tools/
│   ├── ToolHandler.cs              # Abstract base for tool handlers
│   └── BuiltIn/
│       ├── GenerateImageTool.cs    # Calls SwarmUI T2I engine
│       ├── WebSearchTool.cs        # HTTPS web search
│       └── FileReadTool.cs         # Sandboxed file read
├── T2I/
│   ├── PromptProcessor.cs          # Runs on LateSpecialParameterHandlers
│   └── PromptTagHandler.cs         # Registers params + <llmprompt>/<llmresponse>/<llmoriginal>
├── WebAPI/
│   ├── LLMAssistantAPI.cs          # Endpoint registration under PermSettings
│   ├── ChatEndpoints.cs            # LLMAssistantSendMessageWS (streaming chat)
│   ├── AssistantEndpoints.cs       # Assistant CRUD
│   ├── InstructionEndpoints.cs     # Instruction set CRUD
│   ├── ThreadEndpoints.cs          # Thread CRUD, active-thread persistence
│   ├── ModelEndpoints.cs           # Model listing per backend
│   ├── SettingsEndpoints.cs        # Global settings get/save
│   └── ToolEndpoints.cs            # Tool CRUD + manual test execution
├── Tabs/
│   └── Text2Image/
│       └── LLMAssistant.html       # Tab markup (auto-registered by folder convention)
└── Assets/
    ├── llmassistant.js             # Main tab controller, state, welcome gallery
    ├── chat.js                     # Message rendering, streaming, tool bubbles
    ├── threads.js                  # Thread list, save/load, group-by-date
    ├── tools.js                    # Tool management UI (list, editor, test)
    ├── utils.js                    # CDN loader (marked, KaTeX, Mermaid, highlight.js)
    ├── llma-layout.css             # Grid container, sidebar, split bars
    ├── llma-topbar.css             # Top bar, model pill, popovers, context bar
    ├── llma-welcome.css            # Welcome gallery, assistant cards
    ├── llma-chat.css               # Messages, bubbles, input pill, attachments
    ├── llma-panel.css              # Right assistant panel
    ├── llma-settings.css           # Settings modal, assistant editor
    ├── llma-tools.css              # Tool call/result bubbles, tool list/editor
    └── llma-common.css             # Toasts, responsive breakpoints, shared animations
```

### Design Notes

- **No backend ownership.** The extension never spawns, configures, or talks directly to any external LLM process. It only calls into `Program.Backends.RunningBackendsOfType<AbstractLLMBackend>()` and dispatches via SwarmUI's native streaming API. This means adding support for a new LLM runtime is a SwarmUI-level change, not an extension-level change.
- **Model registry integration.** On init, the extension calls `Program.T2IModelSets["LLM"] = handler` with `FolderPaths = Models/llm`, so LLM models appear in SwarmUI's model browser alongside image models. The `LLM Model ID` T2I parameter pulls from this same registry.
- **In-tab modal.** The settings dialog is `position: absolute; inset: 0` anchored inside the tab pane, not `position: fixed` to the viewport. This is intentional — Bootstrap modals would float over the entire SwarmUI UI, which breaks the tab metaphor.
- **Reused SwarmUI styles.** The HTML uses `.basic-button` for all primary buttons, `.splitter-bar` for the resizable dividers (alongside an extension-specific `.llma-splitbar` that adds layout-only rules), and all theme CSS variables (`--text`, `--emphasis`, `--light-border`, etc.). Global scrollbar styling from `site.css` is inherited directly.
- **CSS `:has()` empty state.** The chat panel uses `:has(.llma-messages:empty)` to vertically center the input with a "How can I help you today?" headline when no messages exist yet, then drops to bottom on first send.
- **Tool call format.** The prompt injection format is `<tool_call>{"name":"X","arguments":{...}}</tool_call>` / `<tool_result name="X">...</tool_result>`. This is deliberately text-based so it survives any backend that streams plain text — we can add native `tools` / `tool_calls` passthrough later for backends that support the OpenAI schema natively.

### Permissions

All endpoints are registered under the `PermSettings` permission so they follow SwarmUI's standard user permission system.

## Configuration

### Settings Modal

Open the settings gear in the top-right of the LLM Assistant tab.

**General tab:**

| Setting | Default | Description |
| --- | --- | --- |
| Temperature | `0.8` | Sampling temperature |
| Max Tokens | `2048` | Max response length |
| Top P | `0.9` | Nucleus sampling cutoff |
| Top K | `40` | Top-K sampling cutoff |
| Repeat Penalty | `1.1` | Penalty for token repetition |
| Seed | `-1` | `-1` = random |
| Context Messages | `0` | How many prior messages to include in each request (`0` = all) |
| Stream | `true` | Enable WebSocket streaming |
| Markdown Rendering | `true` | Render markdown in assistant messages |
| Enter to Send | `true` | Enter sends, Shift+Enter newlines |
| Show Token Count | `true` | Show char/token counter under input |

**Assistants tab:** Create, edit, delete assistants. Each has name, category, description, avatar, color, per-mode instructions, parameter overrides, and enabled tools.

**Tools tab:** Create, edit, delete, and test tools. Built-in tools can be toggled on/off and have their descriptions edited, but core fields (handler type, handler ID) are read-only.

## Troubleshooting

**The tab loads but I can't send messages — "No LLM backend is running":**
- Expected. This extension requires SwarmUI's native LLM backend support, which is not yet shipped. See the warning at the top of this README.

**Tool calls never fire:**
- Verify the tool is enabled both globally (`Settings > Tools`) and on the current assistant (`Settings > Assistants > Edit > Enabled Tools`)
- Check the system prompt preview — if the tool spec isn't in the prompt, the model has no way to discover it
- Some small models don't follow the `<tool_call>` format reliably. Try a larger/instruction-tuned model

**`<llmprompt>` tags are not being processed at generation time:**
- Make sure the **LLM Prompt Processing** parameter group is toggled on in the Generate tab sidebar
- Verify an LLM model is selected via `LLM Model ID` or that your assistant has a default model
- Check the server logs for the `[LLMAssistant]` prefix

**Thread list is empty after sending messages:**
- Threads are only persisted after the first message is sent (empty threads are intentionally not saved)
- Check `ThreadStorageService` logs for write errors

**Settings modal appears behind the page instead of floating in the tab:**
- The modal is scoped to the tab pane (not the viewport). If it's rendering outside the tab, check that the `.llma-container` is still `position: relative` and the tab pane has `position: relative; height: 100%`

## Changelog

- **1.0.0** — Initial structure. Chat UI, threads, assistants, instructions, tool calling, T2I prompt integration. Pending SwarmUI native LLM backend support to become functional.

## License

MIT License — see [LICENSE](LICENSE).

## Acknowledgments

- [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) and [mcmonkey](https://github.com/mcmonkey4eva) — Base platform and the LLM backend infrastructure this extension is waiting on
- [MagicPrompt](https://github.com/HartsyAI/SwarmUI-MagicPromptExtension) — The original Hartsy prompt enhancement extension; `<llmprompt>` tag aliasing to `<mpprompt>` is provided for backward compatibility when MagicPrompt is not installed
- [marked](https://marked.js.org/), [highlight.js](https://highlightjs.org/), [KaTeX](https://katex.org/), [Mermaid](https://mermaid.js.org/) — Markdown, code, math, and diagram rendering inside chat bubbles

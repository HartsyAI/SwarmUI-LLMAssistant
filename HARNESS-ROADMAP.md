# LLM Assistant: harness roadmap

> **Status (2026-11-24): the roadmap in section 4 is built.** Phases 0 to 11 are merged on `main` (#58 to #73), and the README describes every feature below as it works today; this file is now the record of why they were built and in what order. The gap table keeps its original "what we had" text, which describes the code **before** that work. What was not built is in section 6. Newer work that was not in this roadmap (memory, prompt styles and rewriting, the chat library, image workflow tools) is in `NEXT-PHASES.md`.
>
> | Phase | What it delivered | PR |
> |---|---|---|
> | 0, 0b, 0c | Browser e2e harness; core reach-ins isolated in `CoreBridge`; settings forms built from core's components | #58, #59, #60 |
> | 1 | Ask / Auto / Plan approval of tool calls | #61 (landed on `main` together with 0b and 0c through #62) |
> | 2, 3 | Hooks; token usage and context-window compaction | #64 |
> | 4 | Tool sources and the MCP client (stdio and Streamable HTTP; tools only) | #65 |
> | 5 | Workspaces and the `code_*` toolset | #66 |
> | 6a, 6b | Code mode in the interface; Changes tab and live Terminal | #67, #68 |
> | 7 | Skills and custom slash commands | #69 |
> | 8 | Sessions (the assistant's subagents) | #70 |
> | 9 | Knowledge library (`search_knowledge`) | #72 |
> | 10 | Schedules | #71 |
> | 11 | Setup check and headless turns with a dry run | #73 |

Written 2026-11-17 against `main`. Compares this extension with [OpenHarness](https://github.com/HKUDS/OpenHarness) (the reference named for this work), plus LibreChat, OpenCode, Goose and Open WebUI. Peer feature lists for OpenCode, Goose and Open WebUI come from their repo front pages and a few comparison articles, which are thin, so treat those columns as indicative and re-check a peer's docs before copying a design. OpenHarness's list is the detailed one.

Goal: grow from "chat workspace with tools" into a full agent harness, in additive steps that never break the existing chat, and reserve a **Code mode** (Codex / Claude Code style) now so the UI and data model do not need reshaping later.

---

## 1. What we have that peers mostly don't

Frame the roadmap as additive. These stay.

| Strength | Where |
|---|---|
| Message tree with branching (`activeLeafId`), edit and regenerate as siblings | `ThreadStorageService` |
| Side-by-side model compare lanes | `chat-compare.js`, `ToolTurnRunner` lanes |
| In-process pure-C# GGUF engine (CUDA / Vulkan / CPU), prefix-cache reuse, vision + video | `HartsyLocalLLMProvider` |
| One provider seam (`ILLMProvider`) and one tool loop shared by chat and voice | `ToolTurnRunner`, `IToolTurnSink` |
| Permissions keyed on the handler, not the tool id; per-user rate limits; opt-in audit log | `ToolExecutorService`, `ToolRateLimitService`, `AuditLogService` |
| Shared / personal settings layers, assistant inheritance (`extends`), per-model prompt variants | `AssistantResolver` |
| Floating companion, `<llmprompt:>` Generate-tab tags, `generate_image` / presets / captioning tools | `companion.js`, `T2I/` |
| Zero-CDN UI | `Assets/vendor/` |

## 2. Gaps

Ordered by leverage. "Seam" is where it slots in today.

| # | Gap | What peers do | What we have | Seam |
|---|---|---|---|---|
| 1 | **Per-call approval and permission modes** (done in Phase 1: Ask / Auto / Plan, see the README) | OpenHarness: default (ask before writes/exec), auto, plan (read-only). OpenCode: build / plan agents. Cline: approve every action. | Only RBAC per handler: who may ever run a tool. Nothing asks "run *this* write?". `shell_exec` is on or off. | `ToolExecutorService.ExecuteTool`, a new sink event |
| 2 | **MCP client** (done in Phase 4, see the README) | stdio + HTTP, auto-reconnect, tool schemas inferred, tool-only servers (OpenHarness, Goose, LibreChat, Open WebUI) | `mcp_stdio` / `mcp_http` handler types are reserved in `Constants.cs` and the tool editor, not implemented | `ToolHandler`, `ToolRegistryService` |
| 3 | **Hooks** (done in Phase 2, see the README) | PreToolUse / PostToolUse, defined by plugins (OpenHarness) | None | `ToolExecutorService` is the single chokepoint |
| 4 | **Context management** (done in Phase 3, see the README, "Long chats") | Auto-compaction, preserved task state (OpenHarness) | Only a "Context Messages" count, which truncates. No summarising compaction. A long agent run just overflows. | `ExtendedLLMInput` build, `LLMStreamHelper` |
| 5 | **Skills** (done in Phase 7, see the README, "Skills and commands") | On-demand `SKILL.md`, user / project / bundled scopes, anthropics/skills layout (OpenHarness, LibreChat) | Assistant instructions only, always loaded | `AssistantResolver`, a new skill loader plus a `skill` tool |
| 6 | **Subagents** (done in Phase 8 as sessions: real chats the assistant starts and directs, see the README, "Sessions") | Agent tool, SendMessage, teams, background tasks (OpenHarness, Goose, LibreChat) | None. The loop is single-agent. | A nested `ToolTurnRunner.RunAsync` with its own sink |
| 7 | **Plan mode and a task list** (done: Plan mode in Phase 1, the `code_todo` task list and the plan card in Phases 5 and 6a) | EnterPlanMode / ExitPlanMode, TaskCreate/Update (OpenHarness, OpenCode) | None | Mode on the turn context, `todo` tool, a UI card |
| 8 | **Workspace / project context** (done in Phases 5 and 6a, see the README) | CLAUDE.md / AGENTS.md discovery injected into the prompt; worktrees (OpenHarness, OpenCode) | None. Every file tool is hard-wired to `Data/` or `Output/`. | New per-user workspace root (see section 3) |
| 9 | **Code-grade file tools** (done in Phase 5: `code_read`, `code_write`, `code_edit`, `code_glob`, `code_grep`, `code_bash`, `code_todo`; no LSP or notebook tools) | Read, Write, **Edit**, Glob, Grep, LSP, notebook | `file_read` (Data only), `file_write` (Output only, extension allowlist), no edit-in-place, no glob, no grep. `shell_exec` runs `cmd.exe`/`sh` in `Data/`. | New handlers beside `FileReadTool` |
| 10 | **Custom slash commands** (done in Phase 7) | `commands/*.md`, `/` palette of commands + skills | `/` opens a tool picker only | `tool-picker.js` |
| 11 | **RAG / file search** (done in Phase 9 as the knowledge library, see the README, "Knowledge library") | Knowledge bases, file search (Open WebUI, LibreChat) | None. Attachments are images/video. | New service plus a tool |
| 12 | **Scheduling and background tasks** (done in Phase 10 as schedules that start sessions, see the README, "Schedules") | Cron create/list/delete, remote trigger (OpenHarness), recipes (Goose) | None | Hosted service |
| 13 | **Cost and usage tracking** (tokens per reply and per turn done in Phase 3, see the README, "Long chats"; there is no price table) | Token + cost per session (OpenHarness) | Token counter in the context bar only | Provider results |
| 14 | **Run non-interactively / dry run** (done in Phase 11: `LLMAssistantRunTurn`, its `dryRun`, and the setup check, see the README, "Setup check and headless turns") | `-p` with json streams, `--dry-run` readiness report | HTTP API exists; no readiness check | New route |
| 15 | **Artifacts and code interpreter** (not built; `code_bash` in code mode runs commands in a workspace, which is not a sandboxed interpreter) | React/HTML/Mermaid artifacts, sandboxed code exec (LibreChat) | Asset index with HTML/SVG/Mermaid preview; no code execution | Part of Code mode |

Known follow-up (prompt styles, P2, still open): the chat-model style addendum is added by `TurnSetupService` and the compare lanes only. The voice endpoints, the one-shot `LLMAssistantSendMessage` and the image-prompt rewrite build their own system prompt and do not get it.

Not worth copying: chat-channel gateways (OpenHarness's `ohmo` for Telegram/Slack), plugin marketplaces, 54 slash commands. They do not fit a SwarmUI tab.

## 3. Code mode

A mode of the same tab, not a new tab: a thread is either `chat` or `code`. Same sidebar, same provider seam, same tool loop, same message tree. What changes is the tool set, the permission gate, and the right-hand panel.

### What it needs beyond a toggle

1. **Workspace root.** A per-user allowed directory outside `Data/` and `Output/`, chosen in the UI and stored on the thread (`workspace`) with an admin-set list of permitted roots. Every sandboxed tool today hard-codes its root; code tools take the root from the thread. Path checks reuse the existing `StartsWith`-after-`GetFullPath` pattern from `ShellExecTool`, hardened against symlinks and `..`.
2. **Approval gate** (gap 1, the keystone). The loop pauses on a gated call, the sink emits an approval request, the client answers allow once / allow for session / deny, the loop resumes or feeds the denial back as the tool result. Three modes, switched in the composer:
   - **Ask** (default): reads run freely; writes, edits and shell commands need approval.
   - **Auto**: everything allowed inside the workspace (for trusted or disposable setups).
   - **Plan**: read-only tools only; the model must end with a plan the user accepts, which flips the mode to Ask or Auto.
3. **Code toolset** (gap 9): `read_file`, `edit_file` (exact-string replace), `write_file`, `glob`, `grep`, `bash` bound to the workspace, `todo`. `shell_exec` stays as is for chat mode.
4. **Right panel variant.** Assistant panel becomes **Workspace** with tabs: Files (tree + open file), Changes (diff of what the agent touched, with revert), Terminal (live output of running commands). The splitter, collapse and width persistence already exist for the right panel; only its inner content swaps.
5. **Message rendering.** Tool calls render as compact rows (command, file path, diff hunk) with approve / deny buttons inline, reusing `chat-toolcalls.js`.
6. **Project instructions** (gap 8): `AGENTS.md` / `CLAUDE.md` found at the workspace root are appended to the system prompt.
7. **Assistant fit.** The assistant editor already has a `Code` category. Recommended: keep using the category for the picker and add no new instruction mode. Code mode uses the assistant's `chat` instruction plus a built-in code-mode preamble the extension owns, so users don't have to hand-write tool-use discipline.
8. **Safety.** Gated by its own permission (`llm_code_mode`, default NOBODY, like `shell_exec`). Every approved write/exec goes to the audit log. Rate limits apply.

### What was stubbed in Phase 0 (all of it is live now)

These were reservations only when this file was written. Every one is now used: the thread modes and approval modes by Phases 1 and 5, the approval event by Phase 1, and the top-bar switch by Phase 6a.

| Stub | File |
|---|---|
| `ThreadModes` (`chat`, `code`) and `ApprovalModes` (`ask`, `auto`, `plan`) constants | `Constants.cs` |
| `mode` field on new threads (`chat`); old threads read as `chat` | `ThreadStorageService.CreateThread` |
| `IToolTurnSink.ToolApproval` (keeps a no-op default for sinks with nobody to ask; the chat socket sends the `tool_approval` frame) | `ToolTurnRunner.cs` |
| Chat / Code mode switch in the top bar (live since Phase 6a: it opens the Workspace panel; the Changes tab and Terminal followed in Phase 6b) | `Tabs/Text2Image/LLM Assistant.html`, `Assets/llma-topbar.css` |

## 4. Build order (all steps shipped; PR numbers in the status table at the top)

Each step shipped on its own and is useful without the next.

1. **Approval gate + permission modes** (gap 1). Unblocks everything risky. Works in chat mode first: `shell_exec` and `file_write` can finally be on by default behind "ask".
2. **MCP client** (gap 2). Biggest feature gap, lowest cost: it is a `ToolHandler` per server, and the editor already reserves the type. Add reconnect and a per-server permission.
3. **Hooks** (gap 3) at the same chokepoint as the approval gate; ship together with it if convenient.
4. **Context compaction** (gap 4). Needed before any long agent run is trustworthy. Summarise old rounds into a saved system note; keep the tree intact.
5. **Workspace root + code toolset** (gaps 8, 9). Turns on Code mode behind `llm_code_mode`.
6. **Code mode UI** (section 3): mode switch live, Workspace panel, diff view, inline approvals, plan mode, todo card.
7. **Skills** (gap 5) and **custom slash commands** (gap 10), sharing one loader.
8. **Subagents** (gap 6, done in Phase 8 as sessions) once compaction exists, since each subagent needs its own context budget.
9. **RAG, scheduling, cost tracking, dry run** (gaps 11-14): built as Phases 9, 10 and 11 (cost tracking is token counts only, with no price table).

## 5. Risks that were to be settled before step 5 (status as of 2026-11-24)

- **Local small models are unreliable tool callers.** Code mode should show a warning when the selected model has no native tool-call support in its template, and refuse plan/auto there. *Not built, as far as a search of code mode's front end and the setup check found on 2026-11-24: nothing warns about or refuses a model that cannot call tools. The README lists tool calling on small models under Known limitations.*
- **Windows shell quoting.** `ShellExecTool` builds `cmd.exe /c` and `sh -c` strings by hand. A code-grade `bash` tool needs real argument handling and process-tree kill on cancel. *Addressed: the code tools and `shell_exec` both run through `ProcessRunner`, which kills the whole process tree on a timeout or Stop (README, "Code tools and workspaces").*
- **Multi-user.** A shared server with a workspace root means one user's agent can touch another's files unless roots are per-user. Default to a per-user directory under `Output/` and make arbitrary roots an admin-only setting. *Done: each user's workspaces live under their own `Output/<user>/llm_assistant/workspaces/`, and extra folders need `llm_workspace_roots` (ADMINS).*

## 6. What is not built, and what is not verified

Not built (from this roadmap): artifacts and a sandboxed code interpreter (gap 15); LSP and notebook tools; a price table for cost tracking; MCP resources, prompts and sampling (the client is tools-only); per-user MCP servers; skills that restrict tools, and skills from a URL or marketplace; a webhook trigger for schedules; a warning for models that cannot call tools; the chat-model addendum on the voice, one-shot and image-rewrite paths.

Not verified on real hardware or real services: the local GGUF engine is not covered by the e2e suite (it needs a GPU), and CUDA has not been run for MOSS-VL; the knowledge library and memory recall have been exercised against a scripted mock, not a real embedding model; no prompt style (Ideogram 4 included) has been tried with a real image model and a real chat model, only with the mock LLM and header-only model files. See "What is not built / not verified" in `NEXT-PHASES.md`.

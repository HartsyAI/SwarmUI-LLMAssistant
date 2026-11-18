# LLM Assistant: harness roadmap

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
| 7 | **Plan mode and a task list** | EnterPlanMode / ExitPlanMode, TaskCreate/Update (OpenHarness, OpenCode) | None | Mode on the turn context, `todo` tool, a UI card |
| 8 | **Workspace / project context** (back end done in Phase 5, see the README) | CLAUDE.md / AGENTS.md discovery injected into the prompt; worktrees (OpenHarness, OpenCode) | None. Every file tool is hard-wired to `Data/` or `Output/`. | New per-user workspace root (see section 3) |
| 9 | **Code-grade file tools** | Read, Write, **Edit**, Glob, Grep, LSP, notebook | `file_read` (Data only), `file_write` (Output only, extension allowlist), no edit-in-place, no glob, no grep. `shell_exec` runs `cmd.exe`/`sh` in `Data/`. | New handlers beside `FileReadTool` |
| 10 | **Custom slash commands** (done in Phase 7) | `commands/*.md`, `/` palette of commands + skills | `/` opens a tool picker only | `tool-picker.js` |
| 11 | **RAG / file search** | Knowledge bases, file search (Open WebUI, LibreChat) | None. Attachments are images/video. | New service plus a tool |
| 12 | **Scheduling and background tasks** (done in Phase 10 as schedules that start sessions, see the README, "Schedules") | Cron create/list/delete, remote trigger (OpenHarness), recipes (Goose) | None | Hosted service |
| 13 | **Cost and usage tracking** | Token + cost per session (OpenHarness) | Token counter in the context bar only | Provider results |
| 14 | **Run non-interactively / dry run** | `-p` with json streams, `--dry-run` readiness report | HTTP API exists; no readiness check | New route |
| 15 | **Artifacts and code interpreter** | React/HTML/Mermaid artifacts, sandboxed code exec (LibreChat) | Asset index with HTML/SVG/Mermaid preview; no code execution | Part of Code mode |

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

### What is stubbed now

Reservations only; no behaviour changes.

| Stub | File |
|---|---|
| `ThreadModes` (`chat`, `code`) and `ApprovalModes` (`ask`, `auto`, `plan`) constants | `Constants.cs` |
| `mode` field on new threads (`chat`); old threads read as `chat` | `ThreadStorageService.CreateThread` |
| `IToolTurnSink.ToolApproval` with a no-op default, so the approval event has a place on the wire | `ToolTurnRunner.cs` |
| Chat / Code mode switch in the top bar (live since Phase 6a: it opens the Workspace panel; the Changes tab and Terminal followed in Phase 6b) | `Tabs/Text2Image/LLM Assistant.html`, `Assets/llma-topbar.css` |

Not verified in a running SwarmUI.

## 4. Build order

Each step ships on its own and is useful without the next.

1. **Approval gate + permission modes** (gap 1). Unblocks everything risky. Works in chat mode first: `shell_exec` and `file_write` can finally be on by default behind "ask".
2. **MCP client** (gap 2). Biggest feature gap, lowest cost: it is a `ToolHandler` per server, and the editor already reserves the type. Add reconnect and a per-server permission.
3. **Hooks** (gap 3) at the same chokepoint as the approval gate; ship together with it if convenient.
4. **Context compaction** (gap 4). Needed before any long agent run is trustworthy. Summarise old rounds into a saved system note; keep the tree intact.
5. **Workspace root + code toolset** (gaps 8, 9). Turns on Code mode behind `llm_code_mode`.
6. **Code mode UI** (section 3): mode switch live, Workspace panel, diff view, inline approvals, plan mode, todo card.
7. **Skills** (gap 5) and **custom slash commands** (gap 10), sharing one loader.
8. **Subagents** (gap 6, done in Phase 8 as sessions) once compaction exists, since each subagent needs its own context budget.
9. **RAG, scheduling, cost tracking, dry run** (gaps 11-14) as demand appears.

## 5. Risks to settle before step 5

- **Local small models are unreliable tool callers.** Code mode should show a warning when the selected model has no native tool-call support in its template, and refuse plan/auto there.
- **Windows shell quoting.** `ShellExecTool` builds `cmd.exe /c` and `sh -c` strings by hand. A code-grade `bash` tool needs real argument handling and process-tree kill on cancel.
- **Multi-user.** A shared server with a workspace root means one user's agent can touch another's files unless roots are per-user. Default to a per-user directory under `Output/` and make arbitrary roots an admin-only setting.

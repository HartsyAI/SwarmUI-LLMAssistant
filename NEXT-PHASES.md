# LLMAssistant: next phases (12 onward) - DRAFT FOR REVIEW

Status: plan only, nothing built. Phases 0-11 are merged (#58-#73). This file is the hand-off for a fresh session.

**Direction change (user, 2026-11-19):** the long-term goal is a full LLM agent harness and chat UI with all the tools, able to do everything Hermes can do (not yet researched: confirm which Hermes, see the questions below). It will roll out slowly, and the user wants to talk through each feature before it is planned in detail. So:
- **Priority 1 is the "Memory and prompts" slice (next section).** Design questions first, no building until they are answered.
- Everything under "Later work" is parked, not approved, and its order is not agreed. Do not build any of it.
- **Architecture principle (user, 2026-11-19, overrides my earlier wording):** the extension is a generic, modular wrapper around whatever LLM backend the user has set up in Swarm. It calls Swarm's generic backend/API endpoints and must not care what runs behind them. No LLM inference-engine logic gets built in the extension, and nothing in the plan may depend on one specific engine. HartsyInference is the user's personal choice of backend, nothing more; Ollama, llama.cpp, hosted APIs and anything else Swarm can drive are all equal citizens. New features go through the existing provider seam (`LLMDispatcher`, capability interfaces such as `IEmbeddingProvider`) and degrade gracefully when a backend lacks a capability. (Pre-existing provider adapters in `Backends/` are left as they are; this is about new work.)
- Keep a Hermes gap analysis as its own item (see the last section) before planning later slices.

---

# PRIORITY 1: Memory and prompts slice (design stage)

> **Status (2026-11-22):** M1 (the note store, #78) is merged. **M2 (the after-turn extractor) is built** on branch `hartsy/m2-note-extractor-7e4142`: it answers questions 2, 5 (master switch off by default, per-chat and per-assistant opt-out, a never-save list, a notice with Undo), 6 (add/update/delete against the most related notes, a cap with oldest-unused eviction) and 7 (only user messages and assistant replies, never tool results or server-set `kind` messages) as recommended; see the README's "Per-user memory". Still open: **M3**, the review, edit, delete screen (and a pause switch, an admin switch, retention), and anything that reads `style` notes (the image rewrite, S1-c). The extractor runs after each qualifying turn rather than every N turns or on idle (a cheap check and an hourly cap bound the cost); there is no central budget limit yet (suggested addition 1), so it carries its own.

## Goals (from the user)

1. **Memory:** the assistant saves memories by itself, continuously, without being asked.
2. **System prompts by type of task and by model family.** Image generation matters most (this is Swarm): when a tool call generates an image with a given model, the prompt is rewritten for that model automatically. Example: "a photo of a cat" with Ideogram 4 becomes a detailed prompt in the JSON format Ideogram uses.
3. Prompts are organised by model family, with optional per-model overrides.
4. The user can turn rewriting off or change its instructions. Everything modular and fully customizable in Settings. Default: it follows whatever model they are using.

## What exists today (checked in the code)

- **Memory** is one per-user profile blob (`Services/UserProfileService.cs`, now replaced by the note store in `Services/Memory/`, phase M1): fixed scalar fields (preferredName, pronouns, bio, currentWork) plus capped lists (preferences, dislikes, facts with a created date). The model writes with the `memory_write` tool (`category`, `content`) and reads with `memory_read`; the whole profile is rendered into the system prompt through `{{userProfile}}`; Settings can view and clear it. Strictly per user. Two consequences for goal 1: the model only saves when it decides to during a turn, and `memory_write` is `ToolRisk.Write`, so in Ask approval mode every save would prompt for approval.
- **Prompts:** an assistant holds instruction texts per instruction id (`chat`, `vision`, `caption`, `prompt`, `randomprompt`, `video-prompt`, `instructiongen`, `companion`), each with variants chosen by `LLMModelMatcher.PickBest` against the **LLM** being used (`Services/AssistantResolver.cs`). The `<llmprompt>` tag (`T2I/PromptProcessor.cs`) runs one LLM call with the `prompt` instruction, also keyed on the LLM. Nothing is keyed on the **image model** being targeted, which is what goal 2 needs.
- **`generate_image` tool** (`Tools/BuiltIn/GenerateImageTool.cs`, `ToolRisk.Write`): takes `prompt` verbatim, merges presets and `params`, requires a model (from a preset or `params.model`), calls core. No rewrite step. The target model is known right after the preset/params merge and before the core call: that is the natural hook.

> **Update:** S1-b (prompt styles, now covering both the chat-LLM axis and the image-model axis) and S1-c (image rewrite) now have their own detailed plan, split into four PRs: see `PROMPT-STYLES-PLAN.md` (it supersedes the prompt-related questions 8-19 below where they differ). The memory questions 1-7 below still stand.

## Proposed shape (for discussion, not decided)

Three PRs, in order. Each is only planned in detail once its questions are answered.

- **S1-a Memory:** a memory store of small individual notes, an after-turn extractor that saves them automatically, recall into the prompt, and a review/edit/undo UI in Settings.
- **S1-b Prompt styles:** a registry of prompt styles by task type, model family and optional per-model override, plus family detection for LLMs and for image/video models, with Settings UI. Bundled defaults, shared (admin) and personal layers, built on the existing `PromptAssets` loader (frontmatter markdown, as skills use) so they are files that can be edited, exported and shared.
- **S1-c Image prompt rewrite:** one `PromptRewriteService` called from `generate_image` (after preset/params merge), re-pointed from the `<llmprompt>` tag, with validation, fallback, transparency and off switches. The approval card in Ask mode shows the rewritten prompt.

## Design questions

Each has my recommendation, to argue with.

### Memory

1. **What is a memory?** Keep the fixed profile, or atomic notes? *Recommend:* atomic notes (text, kind such as preference/fact/project, source thread, created, last used, pinned flag), with the old profile migrated in as the first notes and `{{userProfile}}` kept working.
2. **Who writes, and when?** (a) the chat model calls `memory_write` mid-turn (today); (b) a separate extractor pass after each turn; (c) both. "Continuously without being asked" points to (b): mid-turn saving depends on the model remembering to do it, and Ask mode would gate it. *Recommend:* (b), with the extractor model user-selectable in its own setting (default: follows the chat model; the user may pick any model their backends offer, for example a small local one), run every N turns or when the chat goes idle rather than every turn, and `memory_write` kept for explicit "remember this". Cost: one extra model call per run; say so in Settings.
3. **Scope.** Per user only, or also per assistant, per workspace/project, per chat mode (chat vs code)? Should image-style preferences ("user likes film grain, hates oversaturation") feed the image prompt rewrite? *Recommend:* per user with an optional assistant/workspace tag; the rewrite may read memories of kind `style`.
4. **Recall.** Inject everything (capped, as today) or retrieve the top matches per turn? *Recommend:* a small always-on core (pinned notes) plus top-k retrieval reusing the Phase 9 knowledge index (BM25 now, vectors when an embedding model is set). Semantic recall needs embeddings; keyword recall works without.
5. **User control and privacy.** Visible, editable, deletable; a toast on save with Undo; a per-user pause switch; a per-chat "don't remember this chat"; an editable list of things never to save (credentials, health, etc.); an admin switch to disable memory entirely; retention limits. Which of these are must-haves?
6. **Keeping it tidy.** Duplicates, updates ("I moved to X"), contradictions, decay. *Recommend:* the extractor sees the most related existing notes and emits add/update/delete operations, never blind appends; a note cap per user with oldest-unused dropped first.
7. **Injection safety.** Memory is persistent, so anything that writes it is an attack path (a web page or document telling the assistant to "remember" something). *Recommend:* the extractor reads only the user's own messages (and the assistant's replies), never tool results, fetched pages or attachments; each note records its source; notes render in the prompt as data, not instructions. Agree?

### Image prompt rewriting

8. **Where does it hook in?** (a) inside `generate_image` after presets/params are merged; (b) the existing `<llmprompt>` tag; (c) a button on the Generate tab. *Recommend:* one shared service used by all three, with (a) first and (b) re-pointed. What does "model selected" mean for you: the model the tool will actually use (preset or `params`), or the model currently chosen on the Generate tab?
9. **Family detection.** How do we know a model is "Ideogram 4" or "Flux"? Options: core's model class/architecture id (reliable, needs checking against core), filename patterns (fragile), a user-set family on the model (override). *Recommend:* core class id first, then patterns, then user override. Is Ideogram 4 a local model in your Swarm, or an API-backed model? Which families should ship first (for example Flux, SDXL-style tag models, Qwen-Image, Ideogram, a video family)?
10. **Output formats and validation.** Families differ: natural language, tag lists, token-limited prompts, structured JSON. The rewrite must be checked: JSON parses and matches the family's schema, length within the model's limit. On failure: fall back to the original prompt with a warning in the tool result, or fail the call? *Recommend:* retry once, then fall back with a warning.
11. **Which LLM rewrites?** *Recommend:* its own setting; default follows the chat model; any model the user's configured backends list, routed by capability. Note that a local LLM and image generation can compete for the same GPU memory, so the setup check should warn when the rewrite model is local.
12. **Transparency.** The tool result shows original and rewritten prompt; the image metadata keeps both; the chat model sees the rewritten one so "make it darker" edits it. In Ask mode the approval card shows the rewrite and lets the user edit it before generating. Want an always-preview option?
13. **Scope of the rewrite.** Prompt only, or also negative prompt and suggested parameters (steps, CFG, size) per family? Skip when the prompt is already in the target format (a pasted JSON prompt), and never rewrite twice. *Recommend:* prompt (and negative prompt where the family uses one) in v1; parameters later.
14. **Default and off switches.** *Recommend:* on by default for tool-initiated generations only; never when the user types straight into the Generate tab unless they use `<llmprompt>`. Switches at: global, per assistant, per chat, per call (tool arg), per family. "Use my exact prompt" phrasing in the request skips it. Right?

### Prompt library

15. **Structure.** Axes: task type (the existing instruction ids plus image/video rewrite) by model family, with per-model override. Resolution order: per-model override, then family, then task default; personal over shared over bundled. *Recommend:* one family registry covering LLM families and image/video families (two kinds), edited in one Settings screen, stored as frontmatter-markdown files. The existing assistant instruction variants (keyed on the LLM) become the LLM-family half of it, migrated without losing user edits. Agree, or keep them separate?
16. **Persona versus style.** An assistant's persona (a character) and a model-family style both want to shape the prompt. Compose (persona first, style after) or does the style win for rewrites? *Recommend:* for rewrites the family style wins, persona only adds tone hints; chat keeps persona first.
17. **Chat-model family prompts.** Should slice 1 also cover system prompts by LLM family for the chat itself (some models need different tool-use nudges), or only image/video rewriting plus the existing variants? *Recommend:* include, since the registry is the same.
18. **Who writes the bundled prompts?** Per-family prompts need real format specs and real tuning. For Ideogram 4 I need its JSON format spec or sample prompts from you. Quality can only be judged with real image models and a GPU, not the mock. How should we evaluate (a fixed prompt set, side-by-side images)?

### Customisation UI

19. **Settings.** A "Prompts" tab: list by family with a model-family detector preview ("this model resolves to: family X, override Y"), edit text, test against a sample prompt using the real rewriter, reset to bundled, import/export, enable/disable per family. Anything else you want here?

## Dependencies and risks

- Semantic memory recall needs an embedding model from whatever backend the user has, through the existing `IEmbeddingProvider` capability. If the backend offers none, recall is keyword-only (BM25); the feature must work fully that way. Nothing engine-specific is built here.
- Features that need structured output (the JSON rewrite) must not rely on a backend's JSON mode or any one provider's feature: the extension validates the text itself and retries, so any backend works.
- Rewrite quality can only be proven with real models; the e2e mock proves wiring, validation and fallback only.
- The extractor makes an unattended model call: it must respect the Phase 8/10 spend limits (budget limits become more relevant; see suggested additions).
- Memory must not become an injection channel (question 7).
- No core hacking: model family detection must use official core model metadata; if core lacks what we need, propose it upstream.

## Also needed before later slices

- **Hermes gap analysis.** My understanding is this means Nous Research's Hermes Agent, but I have not verified it. Confirm which Hermes (link or feature list welcome), then research it and add a gap table to `HARNESS-ROADMAP.md`, as was done for OpenHarness, so later slices are chosen from evidence.

---

# LATER WORK (parked, not approved, order not agreed)

The phases below came from the first draft. Numbers will change when the order is re-agreed after the first slice.

## Start-up for a fresh session

1. Read this file, then `HARNESS-ROADMAP.md`, then the "Rules every phase follows" section of `C:\Users\AI Overlord\.claude\plans\federated-pondering-dawn.md` (they all still apply).
2. Memory index (`MEMORY.md`) holds the standing rules: no AI attribution lines, never force-push or rebase pushed branches, never stop the user's SwarmUI on 7801 (harness uses 17801 swarm / 17802 mock, kill by port owner), models must be user-selectable and capability-routed.
3. Repo: `HartsyAI/SwarmUI-LLMAssistant`, base `main`, branch `harness/phase-N-<name>`. One PR per phase. Gate per PR: unit tests, Playwright suite, UI looked at in the browser (dark/light/phone), code review at high effort with fixes and `ReportFindings` outcomes, CI green, then `gh pr merge N --merge` (no `--delete-branch`).
4. Known baseline: 1,034 unit tests pass, 1 known failure (`VoiceTurnWsDisconnectTests...TheOriginalBug`, same on `main`).

## Order and why (first-draft order, superseded by the priority above)

`12 Robustness -> 13 Real-model smoke (no GPU) -> 14 Documents -> 15 Streaming headless -> 16 GPU / local engine pass -> 17 Local embeddings (blocked upstream)`

- 12 first: small, fixes real defects, and adds the restart and second-user test tooling that later phases reuse. It also fixes CI time before more specs land.
- 13 early (not last): everything so far ran on a scripted mock. Real models can change designs in 14-15 (usage fields, tool-call quirks, embedding limits), so find that out before building more.
- 14 before 15: documents are the larger user-visible gap; streaming is a transport variant.
- 15 depends on 12 (atomic reply registration).
- 16 needs GPU access (user decision). 17 is blocked on HartsyInference.

## Phase 12: Robustness (one PR, small pieces, each independent)

**12a. Atomic reply registration (the two-requests race).** `GenerationCancellationRegistry.Begin` is `AddOrUpdate`: it cancels any earlier reply for the thread. That is right for the chat socket (a newer message replaces the old reply) but the headless route checks `IsActive` and then calls `Begin`, so two simultaneous requests can both pass the check. Add `TryBegin(threadId)` (atomic `TryAdd`, returns null when taken); `TurnScope` gets a `TryBegin` that the headless path uses, the socket path keeps replace semantics. Tests: unit (N parallel `TryBegin`, exactly one wins; `End` still removes only its own entry), e2e (fire two headless requests together, exactly one succeeds, the other gets the "already being written" error).

**12b. Headless caller disconnect.** Not verified: whether core hands a route its request-abort token (the route signature is `(Session, JObject)`). Investigate first. If core exposes it, cancel the turn on abort. If not, no core hacking: add an optional `timeoutSeconds` (default and ceiling from a setting) that cancels the turn, and document that a caller can stop a turn by `threadId` with the existing stop route (so a script should pass its own `threadId`, or the reply should surface it early). The streaming variant (Phase 15) fixes this properly through the socket closing.

**12c. Restart recovery test.** `SessionManager.Reconcile` exists and runs on the list endpoints, with no test that survives a real restart. Add a `restartSwarm()` helper to `E2E/harness` (stop by port owner, start again on the same data dir, wait for ready). Specs: a session running a slow mock turn when the server restarts shows `interrupted` afterwards (not `running`); a session waiting for approval at restart becomes `interrupted` and its pending approval is gone; a schedule with a run due during downtime is handled by the catch-up window. Check first which of these `SessionTests` already cover as unit tests, and only add the gaps.
Also fix the skipped Phase 10 finding here (a due run lost when models are not yet listed right after a restart) since the restart harness makes it testable.

**12d. Non-admin coverage.** The harness has one admin. Find how to seed a second, non-admin user in the throwaway instance (core user/role data in the temp data dir; verify, do not assume). Specs as that user: readiness report hides MCP error text and the data folder path; schedules, knowledge, MCP management, code mode, session tools are refused without their permission; a normal user cannot see another user's threads/sessions/schedules. If seeding proves impractical, fall back to unit tests that build a non-admin `Session` and call the services and endpoints directly, and say so in the PR.

**12e. CI time.** E2E takes 35-45 min against a 60 min timeout and every later phase adds specs. Shard Playwright across matrix jobs (`--shard=i/n`), each job on its own ports and data dir, with the build cached. Include the harness port settings needed for parallel jobs.

**12f. The one known failing unit test.** Fix `VoiceTurnWsDisconnectTests...TheOriginalBug` or quarantine it with a reason and an issue link, so a clean baseline means something.

Exit: all specs pass on all shards; restart and non-admin specs committed.

## Phase 13: Real-model smoke (opt-in live suite)

Goal: learn what the mock hides. Mostly a test suite plus a findings report; real fixes found become small follow-up PRs.

- `E2E/live/` Playwright project, excluded from CI and from the default run. Driven by env vars (`LLMA_LIVE_BASE_URL`, `LLMA_LIVE_KEY`, `LLMA_LIVE_MODEL`, `LLMA_LIVE_EMBED_MODEL`), never keys in the repo or in logs. A cost cap (max requests / max tokens per run) and cheap-model defaults.
- Assertions are loose on purpose (the tool was called, args parsed as valid JSON, the turn ended, usage was reported, no crash), never on exact text.
- Matrix, as access allows: whichever backends the user has configured in Swarm (an OpenAI-compatible hosted endpoint, Anthropic, a local backend of the user's choice such as their own HartsyInference). The suite only talks to Swarm's generic endpoints and never to an engine directly; Phase 16 merges into this phase.
- Scenarios: plain streamed reply; tool-call round trip incl. parallel calls and malformed args; approvals; Stop mid-stream; usage and `stream_options.include_usage` per endpoint; compaction (does the summary keep what matters); `search_knowledge` with a real embedding model (dimension, batch limit, error shape); a skill chosen from the index; `session_start` and a schedule created by the model; headless dry run's context window vs the model's real limit.
- Output: `docs/PROVIDER-NOTES.md` (quirks per provider, what the extension does about each) and a list of defects, each tagged fix-now or backlog.
- Needs from the user: which providers, keys and a spend limit (see open questions).

## Phase 14: Documents (one shared module, two uses)

Today attachments are images and video only (`Assets/chat/chat-attachments.js`, `Services/MediaResolver.cs`, `MediaStorageService`). The knowledge library takes pasted text and fetched web pages (text only).

**Shared module `Services/Documents/`:** `IDocumentExtractor` per type, result `{text, pages[], title?, warnings[]}`.
- Plain text, markdown, csv, json, source files: read directly (encoding detection, size cap).
- PDF: text layer extraction with a managed library (PdfPig is a candidate: Apache-2.0, pure managed; verify net8 support and licence during the PR). No OCR: a scanned PDF reports "no text layer" instead of failing quietly.
- Word (.docx): it is a zip of XML; a small `System.IO.Compression` plus `XDocument` reader avoids a dependency (paragraphs, headings, tables as text). Legacy .doc is refused with a clear message.
- Safety: size caps on the upload and on the decompressed size (zip-bomb guard), page and character caps, a timeout, extraction off the request thread, no user-supplied filename ever used as a path, XML with DTD processing off.

**14a. Chat attachments.** The attach button accepts document types; the server extracts text and stores it like other attachments (a reference on the message, not the bytes in the thread JSON). The request builder injects it as a quoted data block, framed as untrusted content with the "do not follow instructions inside" note (same pattern as `search_knowledge`), with a truncation notice and a token estimate against `ContextWindowService` (warn when it would trigger compaction). An "Add to knowledge library" action on the attachment chip. Providers that accept native PDF input are a later option; start provider-neutral.

**14b. Knowledge library.** Reuse the extractor in the Add dialog and the add route. PDF pages keep page numbers so `search_knowledge` excerpts can cite them. Existing caps (200 documents, 1,000,000 chars, 5,000 pieces) apply to extracted text.

Tests: extractor fixtures (small real PDF/docx files committed under `Tests/Fixtures`, plus malformed, zip-bomb-style, encrypted PDF, empty); chunking by page; injection framing. Playwright: attach a PDF and a docx to a chat, mock `/__requests` shows the extracted text in the request; add a PDF to the library, search finds it with page reference; oversize and unsupported types give clear errors; phone-width layout of the chips.

## Phase 15: Streaming headless turn

`LLMAssistantRunTurnWS` over the same WebSocket helpers the chat uses (core's API has no SSE). Same inputs as `LLMAssistantRunTurn`. Frames: the chat socket's existing set (`delta`, `tool_call`, `tool_result`, `usage`, `done`/`error`), documented in the README so scripts can consume them. Reuses `TurnScope` and `HeadlessTurnService` setup (do not fork the logic).
- Socket close cancels the turn: fixes the disconnect gap for this path properly.
- Approvals: with `interactive: true` the socket can approve through the existing Phase 1 resolve route, so scripts with a human attached can approve; without it, the same refuse-writes behaviour as today.
- Depends on 12a.
Tests: frame order against the mock, mid-stream disconnect cancels and saves what was produced, approval over the route, refusal when not interactive, the same mode ceiling as the non-streaming route.

## Phase 16: Local-backend pass (merged into Phase 13)

Run the Phase 13 scenarios and the existing sessions/schedules behaviour through a local GPU-backed backend the user has configured, treating it like any other backend: queueing with several sessions (`queued` status), cancel mid-generation, compaction on a small context, headless dry-run window numbers vs what the backend reports. Not CI; output goes in `PROVIDER-NOTES.md`. Needs a GPU machine (the user's own, or a rented pod, which costs money: ask first). Any defect found is fixed in the generic seam, never with backend-specific code paths.

## Phase 17: Embeddings through the backend capability (nothing engine-specific to build)

Withdrawn as an extension task. Embeddings already go through the `IEmbeddingProvider` capability (Phase 9): any backend that offers an embeddings endpoint works, and one that does not leaves search keyword-only. If a particular engine (the user's own HartsyInference, say) gains embeddings, that is that engine's work plus, at most, a backend adapter wiring the capability, outside this plan. The extension must not carry engine logic. Possible generic follow-up: make the setup check say clearly when the selected backend has no embeddings, and offer keyword-only.

## Suggested additions (not requested; ranked)

1. **Budget limits for background work.** Sessions and schedules spend money unattended. Per-user and per-schedule caps (max tokens or max turns per run, a daily ceiling) with a visible "stopped: budget" status. Usage totals already exist (Phase 3), so this is mostly policy plus UI. Highest value.
2. **Diagnostics bundle.** A "Copy diagnostics" button on the setup check: the sanitized report plus versions and settings shape, for bug reports. Small.
3. **Config export/import.** Assistants, skills, commands, hooks, MCP servers and schedules as one file, for sharing and backup. Secrets excluded.
4. **Audit viewer.** Tool-call auditing exists; a read-only viewer (filter by thread/tool/user) would make approvals and hooks easier to trust.
5. **MCP beyond tools.** Check whether the client supports resources and prompts; if only tools, add those (resources feed naturally into the knowledge library).
6. **Schedule trigger kinds** beyond time (for example "after an image batch finishes"), if real use shows demand.
7. **Backlog from earlier reviews (skipped, real):** whole-thread parse on every tool call; stale report after an empty session turn; wasted query embedding after an embedding-model change; fixed 5,000-piece limit; host chat's approval mode reset at each scheduled run; duplicate `CleanTitle`; no caller-disconnect signal (12b).

## Not planned (call out so they are not assumed)

OCR for scanned PDFs, legacy `.doc`/`.xls`/`.pptx`, native provider document blocks, resuming interrupted sessions (they are only marked interrupted), image generation inside documents.

## Open questions for the user

(These are for the parked work; the live questions are in the Priority 1 section above.)

1. Live testing: which backends, whose keys, and a spend limit? (Only through Swarm's generic endpoints.)
2. GPU for the local-backend pass: your own GPU server, or rent a pod (cost)?
3. (Withdrawn: local embeddings are not an extension task, see Phase 17.)
4. Documents: is text-layer PDF and .docx enough for v1 (no OCR)? Extra formats wanted?
5. Order: happy with 12, 13, 14, 15, 16, 17, or should documents come before the real-model pass?
6. Include suggested addition 1 (budget limits) as its own phase after 12?

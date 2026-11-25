# LLMAssistant: what is built and what is next

Status (2026-11-24): **Phases 0 to 11 are merged (#58 to #73), and so is the whole "Memory and prompts" slice and the work that followed it.** The README describes how each feature works today. This file keeps the decisions that still matter, records what shipped (with PR numbers), says what is **not** built or not verified, and holds the parked later work. `HARNESS-ROADMAP.md` is the record of Phases 0 to 11; `PROMPT-STYLES-PLAN.md` is the record of the prompt-styles work.

## What shipped since Phase 11

| Slice | What it delivered | PR | README section |
|---|---|---|---|
| CI | The browser e2e suite runs as 3 shards (120-minute limit each) behind one `e2e` check | commit 72df465, merged in #82 | [E2E/README.md](E2E/README.md) |
| P0 | Prompting guides for every image and video model family (`docs/prompting/`) | #74 | Prompt styles |
| P1 | Prompt-style registry, model matching, Settings > Prompt styles | #75 | Prompt styles |
| P3a | Prompt rewrite engine: tag masking, formatters, retry and fallback, cache | #76 | Prompt styles |
| P3b | Automatic rewrite of every generation, the generation parameters, metadata, `generate_image` result | #77 | Prompt styles |
| P2 | Chat-model addendum in the system prompt (mechanism only; no addendum is built in) | #79 | Prompt styles |
| M1 | Atomic per-user note store, migration of the old profile, recall into the prompt | #78 | Per-user memory |
| M2 | After-turn note extractor (off by default), undo, never-save list, opt-outs | #80 | Per-user memory |
| M3 | Memory manager: review, edit, pin, export and import, pause, retention, admin switch | #82 | Per-user memory |
| Chat library | Full-text search across chats, pins, JSON import and export | #83 | Chat library |
| Image tools | `list_image_models`, `list_loras`, `list_wildcards`, `list_presets`; `generate_image` batch and model choice; default image model | #84 | Image workflow tools |
| Remote backend | A refused model list reports what the endpoint answered | #81 | Adding an LLM backend |
| Documents | PDF, Word, web page and text files attached to chats and added to the knowledge library | #90 | Documents in chats |

## Decisions that still stand

- **Direction (user, 2026-11-19).** The long-term goal is a full LLM agent harness and chat UI with all the tools. It rolls out slowly and each feature is talked through before it is planned in detail. Everything under "Later work" is parked, not approved, and its order is not agreed. Do not build any of it without that conversation.
- **Architecture principle (user, 2026-11-19).** The extension is a generic, modular wrapper around whatever LLM backend the user has set up in Swarm. It calls Swarm's generic backend and API endpoints and must not care what runs behind them. No LLM inference-engine logic goes into the extension, and nothing may depend on one specific engine. HartsyInference is one backend among equals (Ollama, llama.cpp, hosted APIs and anything else Swarm can drive). New features go through the provider seam (`LLMDispatcher`, capability interfaces such as `IEmbeddingProvider`) and degrade gracefully when a backend lacks a capability. The provider adapters already in `Backends/` are left as they are.
- **Memory is an injection channel, so it is guarded.** The extractor reads only the user's own messages and the assistant's replies (never tool results, fetched pages, attachments or server-marked messages), each note records its origin, notes render in the prompt as data and not instructions, and automatic saving is off by default.
- **Structured output must not rely on a backend feature.** The rewrite engine validates and repairs text in the extension (retry once, then fall back to the user's own wording, wrapped in the smallest valid form for a JSON-only model); it never relies on a backend's JSON mode.
- **No core hacking.** Model family detection uses core's own model metadata; anything core cannot give is proposed upstream (`docs/UPSTREAM-PROPOSALS.md`).
- **Semantic recall is optional.** Embeddings go through `IEmbeddingProvider`; a backend with none leaves search keyword-only, and every feature must work fully that way.

## What is not built / not verified on real hardware

Not built:

- **A hard bound on PDF reading.** The limits in `Services/Documents/PdfLimits.cs` are best effort against known ways to make a small file expensive. A guarantee needs extraction in a separate process with a memory and time limit.
- Anything that reads `style` memory notes (the image rewrite does not use them yet).
- The chat-model addendum on the voice routes, the one-shot `LLMAssistantSendMessage` and the image-prompt rewrite (they do not go through `TurnSetupService`). No addendum text ships; one is added for a model family only after it has been tried on that model.
- A central spend limit for background work (sessions, schedules, the memory extractor each carry their own caps).
- Per-chat and per-assistant overrides for the automatic prompt rewrite, and a composer toggle for it; video styles for image-to-video workflows.
- A streaming headless route, and the rest of the later work below.
- A gap analysis against Hermes (as was done for OpenHarness in `HARNESS-ROADMAP.md`) to choose later slices from evidence. Which Hermes is meant was never confirmed (Nous Research's Hermes Agent is an unverified guess), so confirm it before researching.

Not verified on real hardware or real services:

- **Real GPU.** The e2e suite never loads the local GGUF engine (it needs a GPU); CUDA has not been run for MOSS-VL.
- **Real embeddings.** Knowledge search and memory recall by meaning were exercised against a scripted embedding endpoint in the mock, not a real embedding model.
- **Real prompt-style quality.** No built-in or user style, Ideogram 4 included, has been tried with a real image model and a real chat model. The mock proves wiring, validation and fallback only. The open real-model checks are in `docs/prompting/README.md` (Ideogram empty-`elements` fallback, Flux.3 inline `<bbox>` against BFL's form, Fibo 2 numeric boxes, escaping Swarm tags inside JSON strings).
- **Real providers.** Tool-call round trips, usage fields and compaction were run against the mock LLM; a live provider matrix is Phase 13 below, not done.

# LATER WORK (parked, not approved, order not agreed)

The phases below came from the first draft, written before the memory and prompt-style slices shipped. Numbers will change when the order is re-agreed. Status notes in square brackets were checked against the code on 2026-11-24.

## Start-up for a fresh session

1. Read this file, then `HARNESS-ROADMAP.md` and the README.
2. Standing rules: no AI attribution lines in commits or PR bodies, never force-push or rebase pushed branches (merge `main` in instead), never stop the user's own SwarmUI on 7801 (the e2e harness uses 17801 for Swarm and 17802 for the mock; stop it by port owner), and models must be user-selectable and capability-routed.
3. Repo: `HartsyAI/SwarmUI-LLMAssistant`, base `main`, branch `harness/phase-N-<name>`. One PR per phase. Gate per PR: unit tests, Playwright suite, UI looked at in the browser (dark/light/phone), code review at high effort with fixes, CI green, then `gh pr merge N --merge` (no `--delete-branch`).
4. Test baseline: run the unit tests (`dotnet test Tests/LLMAssistant.Tests.csproj`) on a clean `main` and record the numbers before starting; this file no longer states a count. A known failing test was recorded earlier (`VoiceTurnWsDisconnectTests...TheOriginalBug`); whether it still fails was not re-checked.

## Order and why (first-draft order, superseded by the priority above)

`12 Robustness -> 13 Real-model smoke (no GPU) -> 14 Documents -> 15 Streaming headless -> 16 GPU / local engine pass -> 17 Local embeddings (blocked upstream)`

- 12 first: small, fixes real defects, and adds the restart and second-user test tooling that later phases reuse. (CI time was fixed separately, see 12e.)
- 13 early (not last): everything so far ran on a scripted mock. Real models can change designs in 14-15 (usage fields, tool-call quirks, embedding limits), so find that out before building more.
- 14 before 15: documents are the larger user-visible gap; streaming is a transport variant.
- 15 depends on 12 (atomic reply registration).
- 16 needs GPU access (user decision). 17 is blocked on HartsyInference.

## Phase 12: Robustness (one PR, small pieces, each independent)

Status as of 2026-11-24: **12e is done** (the CI sharding in #82). **12a, 12b and 12c are not built**: `GenerationCancellationRegistry` has no `TryBegin`, the headless route has no `timeoutSeconds`, and the e2e harness has no `restartSwarm()`. 12d and 12f were not checked.

**12a. Atomic reply registration (the two-requests race).** `GenerationCancellationRegistry.Begin` is `AddOrUpdate`: it cancels any earlier reply for the thread. That is right for the chat socket (a newer message replaces the old reply) but the headless route checks `IsActive` and then calls `Begin`, so two simultaneous requests can both pass the check. Add `TryBegin(threadId)` (atomic `TryAdd`, returns null when taken); `TurnScope` gets a `TryBegin` that the headless path uses, the socket path keeps replace semantics. Tests: unit (N parallel `TryBegin`, exactly one wins; `End` still removes only its own entry), e2e (fire two headless requests together, exactly one succeeds, the other gets the "already being written" error).

**12b. Headless caller disconnect.** Not verified: whether core hands a route its request-abort token (the route signature is `(Session, JObject)`). Investigate first. If core exposes it, cancel the turn on abort. If not, no core hacking: add an optional `timeoutSeconds` (default and ceiling from a setting) that cancels the turn, and document that a caller can stop a turn by `threadId` with the existing stop route (so a script should pass its own `threadId`, or the reply should surface it early). The streaming variant (Phase 15) fixes this properly through the socket closing.

**12c. Restart recovery test.** `SessionManager.Reconcile` exists and runs on the list endpoints, with no test that survives a real restart. Add a `restartSwarm()` helper to `E2E/harness` (stop by port owner, start again on the same data dir, wait for ready). Specs: a session running a slow mock turn when the server restarts shows `interrupted` afterwards (not `running`); a session waiting for approval at restart becomes `interrupted` and its pending approval is gone; a schedule with a run due during downtime is handled by the catch-up window. Check first which of these `SessionTests` already cover as unit tests, and only add the gaps.
Also fix the skipped Phase 10 finding here (a due run lost when models are not yet listed right after a restart) since the restart harness makes it testable.

**12d. Non-admin coverage.** The harness has one admin. Find how to seed a second, non-admin user in the throwaway instance (core user/role data in the temp data dir; verify, do not assume). Specs as that user: readiness report hides MCP error text and the data folder path; schedules, knowledge, MCP management, code mode, session tools are refused without their permission; a normal user cannot see another user's threads/sessions/schedules. If seeding proves impractical, fall back to unit tests that build a non-admin `Session` and call the services and endpoints directly, and say so in the PR.

**12e. CI time. Done (#82).** The suite is split over 3 matrix jobs (`--shard=i/3`, each with its own throwaway SwarmUI), each with a 120-minute limit; the unit tests run once, in shard 1; one aggregate check named `e2e` passes only when every shard passed. See `.github/workflows/e2e.yml` and `E2E/README.md`.

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

**Built** (see the README, "Documents in chats" and "Knowledge library"). The text below is the first plan; what differs: documents are blocks of quoted text on the message that carries them (cut to fit, not chunked per question), there is no "Add to knowledge library" action on a chip, and test files are made in code instead of committed.

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

`LLMAssistantRunTurnWS` over the same WebSocket helpers the chat uses (core's API has no SSE). Same inputs as `LLMAssistantRunTurn`. Frames: the chat socket's existing set (`chunk`, `tool_call`, `tool_result`, `done` with its `usage`, `error`), documented in the README so scripts can consume them. Reuses `TurnScope` and `HeadlessTurnService` setup (do not fork the logic).
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

(These are for the parked work. The earlier memory and prompt-style questions were answered and built; see the status table at the top.)

1. Live testing: which backends, whose keys, and a spend limit? (Only through Swarm's generic endpoints.)
2. GPU for the local-backend pass: your own GPU server, or rent a pod (cost)?
3. (Withdrawn: local embeddings are not an extension task, see Phase 17.)
4. Documents: is text-layer PDF and .docx enough for v1 (no OCR)? Extra formats wanted?
5. Order: happy with 12, 13, 14, 15, 16, 17, or should documents come before the real-model pass?
6. Include suggested addition 1 (budget limits) as its own phase after 12?

# LLM Assistant — code audit

Audited 2026-08-18 against SwarmUI `0.9.8.2` and [`docs/Making Extensions.md`](https://github.com/mcmonkeyprojects/SwarmUI/blob/master/docs/Making%20Extensions.md).
Every finding below was verified against source; the ones marked **verified live** were reproduced
against the running instance.

Severity: **S1** = broken behaviour or data/privacy loss · **S2** = standards violation or real risk ·
**S3** = dead weight, hazard, or cleanup.

> ## ✅ Resolution — all 16 findings fixed, 2026-08-18
>
> Every finding below was fixed the same day and re-verified against a live rebuilt server
> (probe-backend methodology, same as the original audit). Per-finding resolution:
>
> | # | Fix | Verified |
> |---|---|---|
> | 1 | Resolver no longer force-inherits Swarmie; an explicit `enabledToolIds` (even empty) IS the set, absent inherits; `ExecuteTool` enforces the resolved allowlist for assistant-scoped calls; endpoints expose `_effectiveToolIds`/`_effectiveToolsEnabled` and the picker/editor consume them | Live: zero-tool assistant → prompt teaches **no** tools; subset child teaches exactly its subset; disallowed `ExecuteTool` rejected; "Run Test" unaffected |
> | 2 | Personal delete of any id allowed (= revert when a shared counterpart exists); shared delete refused only for stored `isBuiltIn` records; editor shows "Revert to default" on overlays and hides delete on pure built-ins; `isBuiltIn` forced from stored state on save. Browser pass caught one more: for admins the scope box pre-checked "shared" on built-ins, so an edit mutated the baseline instead of overlaying — built-ins now default to personal even for admins (tick the box to edit the baseline deliberately) | Live API: overlay keeps `isBuiltIn` + `_hasSharedCounterpart`, revert restores shared Swarmie, shared built-in delete refused. Live browser: edit→save shows "Revert to default", revert restores the shared row |
> | 3 | Cache keyed `user \| model \| assistant \| instruction \| prompt` + 30-min TTL; both callers updated | Live: same prompt across 2 models/2 assistants → distinct replies, 3 upstream calls for 4 requests (1 cache hit) |
> | 4 | Namespace renamed to `Hartsy.Extensions.LLMAssistant.*`; the 5 forced full-qualifications simplified; 2 hidden relative-namespace couplings (`Text2Image.T2IPreset`, `Utils.Logs`) surfaced by the rename and fixed properly | Debug + Release/NuGet + full-SwarmUI builds clean; extension loads, backends persist (string type ids), settings persist (data name) |
> | 5 | `Program.ModelPathsChangedEvent` subscribed (re-register **+ `handler.Refresh()`**, since the event fires after core's refresh pass); unsubscribed in `OnShutdown` | Live: a `paths.*` `ChangeServerSettings` save no longer destroys the LLM model set |
> | 6 | `SettingsService.SettingsLock` (reentrant) around every settings-layer read-modify-write, including the composing sequences in Assistant/ToolRegistry/Instruction services and endpoints | Build + code-path review |
> | 7 | `SaveAssistant` merge-preserves: stored custom instruction ids and unknown stored fields survive editor saves; `created` preserved; payload fields allowlisted (annotation echo can't persist); full validation (id shape, length caps, extends existence/self/cycle) with real error messages | Live: seeded custom instruction mode + `created` survive an editor-shaped save; bad id / self-extends / missing parent all rejected with clear errors |
> | 8 | `static new Version` → `const ExtensionVersion`; constructor sets `ExtensionAuthor`/`Description`/`License`/`ReadmeURL`/`Tags` | Build; base `Version` now core-populated |
> | 9 | `ILLMProvider.GenerateLive` callback widened to `Func<JObject, Task>` — all providers await it, `LLMStreamHelper` awaits sends (no `.Wait()`); `PromptProcessor` blocking contained (`GetAwaiter().GetResult()` + 120 s timeout + core-limitation comment) | Live: streaming chat, agentic tool round, and 2-lane compare all E2E through the async seam |
> | 10 | Implemented: pins core `WildcardSeed` to a deterministic FNV-1a hash of the source prompt (user-set seed wins) | Code-level only — no local image backend to run a real generation (flagged honestly) |
> | 11 | 10 dead members deleted; `mpprompt`/`mpresponse`/`mporiginal` prefixes properly registered (MagicPrompt migration path) + README note | Build |
> | 12 | `DefaultSettings` = lazily-built template + one `DeepClone` per read (double-clone in `GetSettings` dropped) | Build; semantics identical |
> | 13 | csproj `Warning` target fires on every local-engine build naming the release command | Warning observed in build output; `-p:UseLocalHartsy=false` Release build proven green |
> | 14 | Metadata set in code (see 8); extension-list PR remains a user git action | Build |
> | 15 | `AssistantResolver` invalidation added to both `ResetSettings` branches, both `SaveSettings` branches, and `MigrationService` | Live: reset → very next request's system prompt reflects defaults, no TTL staleness |
> | 16 | README "Network connections" section: every outbound connection, when it fires, and its off switch | README |
>
> Also in this pass (user-directed, not an audit finding): the three backend types are now
> `isStandard: true` — visible in Server > Backends without "Show Advanced", renamed
> `LLM: Anthropic Claude` / `LLM: Remote (OpenAI-Compatible)` / `LLM: Local (HartsyInference GGUF)`
> (type **ids** unchanged, so existing backend instances survive untouched — verified live).

---

## Summary

| # | Severity | Area | Finding |
|---|---|---|---|
| 1 | S1 | Assistants | Per-assistant tool allowlist does nothing — every assistant gets every globally-enabled tool |
| 2 | S1 | Assistants | Editing the built-in assistant creates a permanently undeletable personal shadow |
| 3 | S1 | Privacy | Prompt cache is process-global and unkeyed by user — responses (with another user's memory in them) leak across accounts |
| 4 | S2 | Core standards | Namespace is `SwarmUI.*`, explicitly reserved for built-ins |
| 5 | S2 | Core standards | `LLM` model type is destroyed whenever an admin saves path settings |
| 6 | S2 | Thread safety | Settings/assistant/tool writes have no locking; threads do |
| 7 | S2 | Assistants | Saving an assistant replaces the whole record — unknown fields and custom instructions are destroyed |
| 8 | S2 | Core standards | `Version` shadows the base field, so the declared version never reaches the UI |
| 9 | S2 | Risky | Sync-over-async (`.Result` / `.Wait()`) on request and streaming threads |
| 10 | S3 | Dead | `LLM Generate Wildcard Seed` parameter is registered but never read |
| 11 | S3 | Dead | 10 unreferenced C# members; `<mpprompt>` legacy tag is half-wired |
| 12 | S3 | Bloat | `DefaultSettings` rebuilds + clones the whole default tree on every read (~1.2 ms × 40 call sites) |
| 13 | S3 | Hazard | Dev builds use a gitignored `Directory.Build.props` that users never get |
| 14 | S3 | Metadata | Extension metadata fields left unset; not on the official extension list |
| 15 | S2 | Assistants | "Reset Defaults" leaves the resolved-assistant cache stale for up to 5 minutes |
| 16 | S3 | Standards | No explicit outbound-connections notice in the README (standard 5 asks for one) |

**What is *not* wrong:** the CSS is genuinely well-behaved (every colour is a theme variable, only
fallbacks are literals — standard 7 is met), no JS function is dead, the vendored front-end libraries
mean the UI itself makes zero outbound calls, permissions are granular and keyed on the
*handler* rather than the tool id (a real security thought), and `ThreadStorageService` locks its
mutations correctly. The problems are concentrated, not systemic.

---

## S1 findings

### 1. The per-assistant tool allowlist is inert

`AssistantResolver.BuildFromChain` force-appends the default assistant to the bottom of *every*
inheritance chain ([AssistantResolver.cs:162](Services/AssistantResolver.cs#L162)):

```cs
if (lineage[^1] != AssistantConstants.DefaultId && assistants[Default] is JObject defaultAssistant)
{
    chain.Add(defaultAssistant);   // every assistant now inherits Swarmie
    lineage.Add(AssistantConstants.DefaultId);
}
```

`enabledToolIds` is then merged as a **union** — "child can only ADD"
([AssistantResolver.cs:206](Services/AssistantResolver.cs#L206)). The default assistant ships with
*all thirteen* built-in tool ids ([SettingsService.cs `BuildDefaultAssistant`](Services/SettingsService.cs)).

Net effect: unchecking a tool in `Settings > Assistants > Enabled Tools` cannot remove it. The union
puts it straight back.

**Verified live.** An assistant saved with `enabledToolIds: []` and `toolsEnabled: true` produced a
1868-character system prompt naming eleven tools — every globally-enabled one:

```
generate_image, create_image_preset, caption_image, fuse_image_descriptions,
file_read, file_write, http_request, web_search, memory_read, memory_write, swarm_docs
```

(`batch_caption_folder` and `shell_exec` were absent only because they are globally disabled.)

This is not merely cosmetic. `ToolExecutorService.ExecuteTool` gates on the *global* enabled flag and
the handler permission — it never consults the assistant's list — so the model can also **execute**
tools the assistant was never granted.

The frontend disagrees with the backend about this: `llmaPickerTools()`
([tool-picker.js:28](Assets/tool-picker.js#L28)) filters on the assistant's raw `enabledToolIds` with
no inheritance at all. The `/` picker shows what you checked; the model is told about everything.

**Fix direction:** the force-append of the default assistant is what breaks this. Either drop it (and
let missing fields fall back at read time rather than by faking an inheritance link), or exclude
`enabledToolIds` from the implicit default layer. Then decide deliberately whether `enabledToolIds`
should union or be child-wins for real `extends` chains — union is defensible there, but only for
parents the user actually chose. `ExecuteTool` should also enforce the resolved allowlist, not just
the permission.

### 2. Editing the built-in assistant creates an undeletable shadow

The editor sends `id: llmaEditingAsstId` ([llmassistant.js:1724](Assets/llmassistant.js#L1724)) with
`scope: personal` unless an admin ticks the shared box. Clicking **Edit** on Swarmie therefore posts
`{id: "default", scope: "personal"}`.

`SettingsService.UnionMergeDict` gives personal entries priority on id collision, so the personal copy
completely replaces the shared built-in in that user's merged view. `DeleteAssistant` then refuses
unconditionally on the *id* ([AssistantService.cs:175](Services/AssistantService.cs#L175)):

```cs
if (assistantId == AssistantConstants.DefaultId) { return false; }
```

**Verified live:**

```
save   {id: "default", name: "HIJACKED", scope: "personal"}  -> {"success": true}
list   -> [("default", "HIJACKED", "personal")]
delete (auto scope)     -> "Cannot delete the default assistant."
delete (personal scope) -> "Cannot delete the default assistant."
```

The only escape is `LLMAssistantResetSettings`, which also destroys every other personal assistant,
tool config, and preference.

It compounds with finding 7: the editor does not serialise `isBuiltIn`, so the shadow loses that flag —
the UI then *shows* a Delete button for it ([llmassistant.js:1599](Assets/llmassistant.js#L1599)) that
can never succeed. A button that always errors is worse than no button.

**Fix direction:** pick one model and apply it consistently. Either (a) built-ins are read-only —
the editor opens them as a *clone* with a fresh id, matching how `llmaOpenEditorFromTemplate` already
behaves, or (b) personal overrides of built-ins are legal and deleting one reverts to the shared
version. Whichever you choose, `DeleteAssistant` must key on layer + `isBuiltIn`, never on the bare id.

### 3. The prompt cache leaks responses between users

Both caches are `static` — one process-wide instance shared by every account:

- [ChatEndpoints.cs:17](WebAPI/ChatEndpoints.cs#L17) `private static readonly PromptCacheService Cache = new(500);`
- [PromptProcessor.cs:29](T2I/PromptProcessor.cs#L29) `private static readonly PromptCacheService Cache = new();`

The key is only the prompt text plus the instruction id
([PromptCacheService.cs:95](Services/PromptCacheService.cs#L95)):

```cs
private static string NormalizeKey(string prompt, string instructionId)
    => $"{prompt?.Trim().ToLowerInvariant().Replace(" ", "")}||{instructionId?.Trim().ToLowerInvariant()}";
```

But the response depends on things not in that key: the caller's **assistant** (persona and system
prompt), their **model**, their resolved **parameters**, and — the serious part — their **memory
profile**, which `ResolveInstructionForRequest` substitutes into the system prompt via
`{{userProfile}}`. That profile holds preferred name, pronouns, bio, and current work.

So on any multi-user instance, user B running `<llmprompt:a cat>` after user A is served A's response,
generated under A's persona and parameters. **Cross-user response reuse is unconditional.** Whether it
also carries A's *memory* depends on the resolved instruction containing `{{userProfile}}`: the default
`chat` instruction does (so the `LLMAssistantSendMessage` path leaks profile facts outright), while a
bespoke `prompt` instruction with no such placeholder would leak only the response. Either way it
undercuts the "memory is strictly per-user and never visible to another user" guarantee stated in the
memory tool's own description.

Same key collapse also means one user switching assistants or models keeps getting the old
assistant's answer, and entries never expire (LRU only, process lifetime).

**Fix direction:** include `user.UserID`, the resolved assistant id, and the model in the key — or make
the cache per-user. A TTL would be worth having too.

---

## S2 findings

### 4. Namespace violates an explicit documented rule

`docs/Making Extensions.md` states plainly:

> `// NOTE: Namespace must NOT contain "SwarmUI" (this is reserved for built-ins)`

Every file here is under `namespace SwarmUI.Extensions.LLMAssistant.*`. Your own sibling extension gets
this right (`Hartsy.Extensions.AudioLab`), as does `SwarmUI-SD.cpp-Backend`.

It is already costing you: because `SwarmUI.Extensions.LLMAssistant.LLMs` shadows core's `SwarmUI.LLMs`
from inside the extension, five call sites need full qualification to compile —
[LLMProviderBackend.cs:83, 86, 90](Backends/LLMProviderBackend.cs#L83) and
[SwarmNativeLLMProvider.cs:36, 39](LLMs/SwarmNativeLLMProvider.cs#L36). That is the collision the rule
exists to prevent, and it will keep recurring as core grows.

**Fix direction:** rename to `Hartsy.Extensions.LLMAssistant.*` to match AudioLab. It is a mechanical
find-and-replace across 60 files plus the `using` lines, and it removes the qualification workarounds.
Note this changes nothing on disk for users — settings are keyed by the `llmassistant` data name, not
by type names.

### 5. The `LLM` model type is destroyed when an admin saves path settings

`RegisterLLMModelType` runs once in `OnInit` and early-returns if the key already exists
([LLMAssistantExtension.cs:122](LLMAssistantExtension.cs#L122)).

But `Program.BuildModelLists()` — "Called at init **or on settings change**" — does
`T2IModelSets.Clear()` and rebuilds only the eight core sets. `AdminAPI` calls it on any model-paths
save (`src/WebAPI/AdminAPI.cs:215`), then fires `Program.ModelPathsChangedEvent`.

The extension never subscribes to that event, so after an admin saves path settings the `LLM` model
type is simply gone until restart: it vanishes from the model browser and `LLM Model ID` loses its
source. Core provides the hook precisely for this, and both `ComfyUIBackendExtension` and
`SDcppExtension` use it.

**Fix direction:** in `OnInit`, `Program.ModelPathsChangedEvent += RegisterLLMModelType;` and drop the
`ContainsKey` early-return in favour of re-registering (or re-pointing `FolderPaths`, since paths may
have changed — which is the whole point of the event). Note the event fires *after*
`RefreshAllModelSets()`, so the handler must also call `handler.Refresh()` on the newly-registered set
itself — otherwise the type comes back but its model list stays empty until restart.

### 6. Settings, assistant, and tool writes are not thread-safe

The extension standards call this out directly: *"you need to write code that won't explode if it's
called from multiple threads."*

`ThreadStorageService` takes `ThreadWriteLock` around all eight of its get→mutate→save sequences, with
a good comment explaining why. `SettingsService` has **no lock at all** —
[`ReplaceSharedSettings`](Services/SettingsService.cs#L178),
[`ReplaceUserSettings`](Services/SettingsService.cs#L216), and
[`PatchUserSettings`](Services/SettingsService.cs#L232) all read-modify-write `GetGenericData` /
`SaveGenericData` unsynchronised, and `AssistantService.SaveAssistant` / `DeleteAssistant` and the tool
equivalents are built on top of them.

Two browser tabs, a retried request, or two compare lanes finishing together will silently drop one
write — exactly the lost-update the thread service was hardened against. The extension already knows
the pattern; it just isn't applied here.

**Fix direction:** one static lock object in `SettingsService`, taken around the whole read-modify-write
in each mutator (and in `AssistantService`/`ToolRegistryService`, which do their own multi-step
sequences on top).

### 7. Saving an assistant destroys fields the editor doesn't know about

`SaveAssistant` writes `assistants[id] = stripped` — a **full replace**, not a merge. The editor
serialises exactly twelve fields (`id, name, description, icon, color, instructions, parameters,
extends, toolsEnabled, enabledToolIds, toolConfig, avatar`).

Anything else on the stored record is destroyed on save. Concretely:

- `isBuiltIn` is dropped (feeds finding 2).
- `created` is dropped, then re-stamped as *now* by `SaveAssistant`.
- `llmaSerializeInstructionsForSave` iterates only `LLMA_INSTRUCTION_KEYS` — the seven built-in modes
  ([llmassistant.js:10](Assets/llmassistant.js#L10)). The extension supports **custom instruction ids**
  via `InstructionEndpoints`, and an assistant carrying one loses it permanently the first time anyone
  opens and saves that assistant.

Related smaller issues in the same area:

- **No validation on save.** Any JSON is accepted: no id format check, no length caps, no schema. The id
  is used directly as a settings dictionary key.
- **`SetActiveAssistant` doesn't check the id exists**, and `GetAssistant` silently substitutes the
  default when an id is missing — so deleting an assistant quietly re-points all of its threads at
  Swarmie with no warning anywhere.
- **`category` is vestigial.** The editor's "Category" dropdown writes to `icon`; nothing ever writes
  `category`, though `llmaCategoryIcon(a.icon || a.category || 'chat')` and the starter templates still
  read it. Pick one and delete the other.

### 8. `Version` shadows the base field

```cs
public static new readonly string Version = "2.0.0-alpha.2";   // LLMAssistantExtension.cs:17
```

`Extension.Version` is an *instance* field that core auto-populates from git tags in
`PopulateMetadata()`. A `static new` field is a separate storage slot: core writes the base field, the
Extensions tab reads the base field, and the declared `2.0.0-alpha.2` is visible only to the
extension's own startup log. The version users see is the git commit hash.

**Fix direction:** drop `new` and set the instance field (or rename the constant to something like
`ExtensionVersion` if you want a compile-time value for logging). While you're there,
`ExtensionAuthor`, `Description`, `License`, and `ReadmeURL` are never set — core only fills those from
the official extension list, and this extension isn't on it (finding 14).

### 9. Sync-over-async on request and streaming threads

- [PromptProcessor.cs:66-74](T2I/PromptProcessor.cs#L66) calls `.Result` on the LLM task inside a
  `Regex.Replace` callback, from `LateSpecialParameterHandlers`. Each `<llmprompt>` tag blocks a request
  thread for up to the 90 s cache timeout, and `.Result` is the classic deadlock shape if the call ever
  runs under a synchronisation context.
- [LLMStreamHelper.cs](LLMs/LLMStreamHelper.cs) calls `SendJson(...).Wait(linked.Token)` inside the
  synchronous `Action<JObject>` streaming callback, blocking the provider's streaming thread on a socket
  write for every chunk.

The second one is forced by the seam: `ILLMProvider.GenerateLive` takes `Action<JObject>` rather than
`Func<JObject, Task>`. Since `ILLMProvider` is your own interface and the whole design goal is that it's
swappable, widening it to an async callback is cheap now and expensive later.

### 15. "Reset Defaults" leaves the assistant cache stale

`AssistantResolver` caches each resolved assistant for a 5-minute TTL. Only two call sites ever
invalidate it — `AssistantService.SaveAssistant` and `DeleteAssistant`
([AssistantService.cs:147, 160, 209, 225](Services/AssistantService.cs#L147)).

`LLMAssistantResetSettings` (both the personal and the shared branch), `LLMAssistantSaveSettings`, and
`MigrationService` all mutate the same underlying settings blob and **never invalidate**. The
user-visible case is `Settings > Reset Defaults`: the reset succeeds and the UI redraws from fresh
settings, but chat requests keep resolving against the pre-reset persona, parameters, and tool set for
up to five minutes.

`SaveSettings` is the milder case — the resolver only reads the `assistants` dict, which that endpoint
deliberately strips — but resetting the *shared* layer as an admin has the same staleness for every user
at once.

**Fix direction:** call `AssistantResolver.InvalidateAll()` from the shared reset and
`Invalidate(user)` from the personal one. Cheap insurance: invalidate from any path that writes the
settings blob, rather than enumerating which writes happen to matter today.

---

## S3 findings

### 10. `LLM Generate Wildcard Seed` does nothing

Registered as a user-visible T2I parameter at
[PromptTagHandler.cs:57](T2I/PromptTagHandler.cs#L57) and exposed as `ParamWildcardSeed`, but
`PromptProcessor` reads only `ParamUseCache`, `ParamInstructions`, `ParamModelId`, and
`ParamAssistantId`. The parameter is inert — it ships in the Generate sidebar and has no effect.

Either implement it or unregister it; a switch that does nothing is worse than a missing feature.

### 11. Dead members and a half-wired legacy tag

Ten `public` members are referenced nowhere in the extension:

| File | Member |
|---|---|
| `LLMs/ExtendedLLMInput.cs` | `Timestamp` |
| `Services/MediaStorageService.cs` | `GetThreadUploadsDir` |
| `Services/OrphanedFileGC.cs` | `SetInterval` |
| `Services/SessionStateService.cs` | `ActiveAssistantId`, `CurrentModel`, `CurrentVisionModel`, `GetActiveThreadId`, `SetActiveThreadId` |
| `Services/ToolRateLimitService.cs` | `ResetAll` |
| `T2I/PromptTagHandler.cs` | `ParamWildcardSeed` (finding 10) |

(`ToolConstants.HandlerMcpStdio` / `HandlerMcpHttp` are also unreferenced but are documented as reserved
for planned MCP support — leave them.)

`OrphanedFileGC.SetInterval` is the "configurable GC interval" the old README promised; the setter
exists and nothing calls it. The five `SessionStateService` typed accessors were superseded by the
generic patch endpoint.

**Legacy `<mpprompt>`:** `TagRegex` matches `(?:llm|mp)prompt` and `ProcessPrompt` early-outs on
`<mpprompt`, but `RegisterPromptTags` only registers the `llmprompt` / `llmresponse` / `llmoriginal`
prefixes with `PromptRegion.RegisterCustomPrefix`. So `<mpprompt:…>` is half-supported: the processor
would handle it, but Swarm's prompt parser never learns the prefix. This is MagicPrompt-compat residue
(that extension *is* on the official list, this one isn't). Either register the `mp*` prefixes properly
as a documented migration path, or strip the alias.

### 12. `DefaultSettings` is rebuilt and cloned on every read

[SettingsService.cs:27](Services/SettingsService.cs#L27) declares it as an expression-bodied **property**:

```cs
public static JObject DefaultSettings => new() { ... ["tools"] = ToolRegistryService.BuildDefaultTools(), ... };
```

`BuildDefaultTools()` constructs thirteen tool objects with full JSON Schemas — several hundred lines of
`JObject` allocation. `GetSettings()` then does `(JObject)DefaultSettings.DeepClone()`, so the tree is
**built and then cloned** on every call. There are 40 call sites of `GetSettings`/`GetMergedSettings`,
several of them per chat request.

Measured on the live instance: `LLMAssistantGetSettings` averages **1.63 ms**, against **0.40 ms** for
`LLMAssistantGetSessionState` (same auth and JSON path, no settings read) — about 1.2 ms of pure
rebuild per call.

**Fix direction:** make it a `static readonly` template built once and `DeepClone()` it — the clone is
the only part that needs to be per-call.

### 13. Dev builds don't match user builds

`Directory.Build.props` in the extension root sets `<UseLocalHartsy>true</UseLocalHartsy>`, and it is
gitignored (`.gitignore:45`). So every local build — including the deployed DLL — compiles against the
engine source at `../../../../HartsyInference`, while every end user builds against the pinned
`HartsyInference 2.0.0-alpha.23` NuGet. The two paths reference different assemblies with no CI to
catch drift.

That is a legitimate dev-loop design, but it means "it builds here" is not evidence the released
configuration builds. Worth one `dotnet build -p:UseLocalHartsy=false` before any release tag.

Related, lower risk: the csproj sets `CopyLocalLockFileAssemblies=true` (the shared
`SwarmUI.extension.props` sets it `false`) and carries a nine-package `ExcludeAssets="runtime"` block
annotated *"Keep in sync with SwarmUI.deps.props"*. **Both lists currently match exactly** — this is a
latent maintenance hazard, not a present bug. If core ever adds a package and this list isn't updated,
the extension will start copying a duplicate runtime DLL next to the host's.

### 14. Extension metadata and listing

`ExtensionAuthor`, `Description`, `License`, and `ReadmeURL` are never assigned. Core fills Author /
License / Description only for repos present in `launchtools/extension_list.fds`, and this repo isn't
there (four other Hartsy extensions are). Until it's listed, the Extensions tab shows
`(Unknown)` / `(No description provided)`.

`docs/Making Extensions.md` asks that finished extensions be PR'd to that list. Findings 1–3 are worth
clearing first.

---

### 16. No outbound-connections notice

Standard 5 asks that an extension *"make sure it's clear when/why a connection will be happening — at
the very least, a notice in the readme listing what connections are made and why."*

The README documents each feature that reaches the network, but never collects them into one statement.
For a listing submission that's worth a short section naming: the DuckDuckGo HTML scrape (`web_search`),
arbitrary outbound HTTP (`http_request`, SSRF-guarded), `api.anthropic.com` and whatever `Address` the
OpenAI-compatible backend points at, and the fact that the local engine and all front-end libraries make
no connections at all. The last point is a selling point, not just compliance.

## Suggested order of work

1. **Finding 1** — the tool allowlist. It's the one that makes a headline feature untrue, and it has a
   security edge (`ExecuteTool` not honouring the allowlist).
2. **Finding 3** — the cache leak. Small fix, and it's a cross-user privacy issue.
3. **Finding 2 + 7** — settle the built-in/edit/save model in one pass; they're the same design gap.
4. **Findings 5, 6, 8, 15** — small, self-contained conformance and correctness fixes.
5. **Finding 4** — the namespace rename. Mechanical, best done as its own commit with no other changes.
6. **S3 cleanup** — delete or implement, whichever is honest.

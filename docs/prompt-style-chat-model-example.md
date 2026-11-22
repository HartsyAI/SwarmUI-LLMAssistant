# Example: a chat-model prompt style (not built in)

This is **only an example** of the file format. Nothing like it ships with the extension: an addendum is added for a model family only after it has been tried on that model, and which families to cover is up to you. Copy it, change the conditions to your own model, and judge the effect on a real model before you keep it.

Save it in Settings > Prompt styles > By chat model (+ Add Style), or as `Data/LLMAssistant/prompt_assets/users/<you>/styles/small-local-tools.md`. The id is the file name.

```markdown
---
name: Small local model, tighter tool calls
description: Example only. Short rules for a small local model that fumbles tool calls.
kind: llm
matches: glob:*-3b*, glob:*-4b*
tasks: tools
---

When you call a tool, call exactly one tool per step, with every argument the tool needs and no others.
Never write a tool call inside prose. After a tool result arrives, answer in one or two short sentences unless more was asked for.
```

How it behaves:

- It is added to the system prompt right after the assistant's own instruction, only for a model whose id matches one of the conditions, and only on turns that offer tools (`tasks: tools`; leave `tasks` out to apply to every chat turn).
- Check it in Settings > Prompt styles > Which style does a model get? (type a model id): it says whether the addendum applies, why, and how many tokens it adds. A dry run (`LLMAssistantRunTurn` with `dryRun: true`) reports the same for a real turn.
- Switch it off for everyone with *Tune prompts for the chat model*, for this style with its own switch, for one model with a rule set to *no style*, or for one assistant in its editor.
- Conditions: `exact:`, `family:`, `provider:`, `tag:` or `glob:` (a model name pattern with `*` and `?`; a pattern cannot contain a comma). A name glob is what still works when a backend does not report a model's family.

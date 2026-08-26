# Prompts

Every piece of prose SuHarness sends to a model lives here, in plain Markdown, so it can be tuned
without touching C#. The code owns the *order* prompts are assembled in and the *conditions* each
one is gated on; these files own the *wording*.

## Layout

| Folder | What it holds |
| --- | --- |
| `system/` | The agent's system prompt: persona, approach, per-tool guidance, orchestration, plan mode, and the focused workflow roles. |
| `workflow/` | The multi-agent coding workflow: Explorer, Planner, Senior Developer, Junior Developer, Reviewer, Verifier. |
| `continuation/` | Text injected mid-turn when a turn stalls, skips validation, or opens a goal round. |
| `compaction/` | The instruction that condenses a long conversation into a checkpoint. |
| `reminder/` | What the repeat-tool loop guard says when a model keeps issuing the same call. |
| `ralph/` | One round of a fresh-agent Ralph loop. |

A prompt is addressed by **id**: its path under `Prompts` without the extension, always with forward
slashes. `Prompts/workflow/reviewer.md` is `workflow/reviewer`.

## Editing a prompt

Edit the file and restart SuHarness. No rebuild is needed — prompts are read from the `Prompts`
folder beside the running binary, which the build copies from here. Text is cached at first use, so
a running process will not pick up an edit.

To try prompts from somewhere else without touching the repository, point `SUHARNESS_PROMPTS` at a
directory of prompt files:

```
SUHARNESS_PROMPTS=/path/to/experiment  ./run-su-harness.sh
```

That directory wins for any id it defines; anything it does not define falls back to the normal
files. It is the intended way to A/B a prompt across benchmark runs. Pointing it at a directory
that does not exist is an error rather than a silent fallback, so a typo cannot quietly run the
wrong prompts.

## Writing rules

**One line here is one line in the prompt.** Lines are never re-wrapped or joined. A paragraph that
must reach the model as a single line has to be a single long line in the file, however wide — turn
on soft wrap rather than breaking it. Several prompts are deliberately one very long line.

**Leading whitespace is content.** A few prompts carry leading tabs inherited from the C# literals
they came from. That whitespace is part of what the model sees, so it is preserved as-is.

**`## name` starts a section.** A file may hold several named blocks that the code selects between —
one per tool, one per access mode, one per role. A section runs to the next `## ` heading. Because
of that, a file that is read whole and whose *content* legitimately contains `## ` headings — the
compaction instruction is the one that does — must never be given sections.

**`{{name}}` is a placeholder.** The code substitutes these at runtime. Both directions are checked
on every load: a placeholder with no value supplied, or a value with no matching placeholder, throws
rather than reaching the model malformed. Single braces are ordinary text, which is why the JSON
schemas in these prompts pass through untouched. Placeholders are substituted verbatim — a value's
own line endings are never rewritten.

**`<!-- ... -->` is a note to editors.** Comment lines are stripped before the prompt is used, and
may span several lines. A comment must start its own line; `<!--` is deliberately never recognised
mid-sentence, because these prompts teach models about HTML.

**Renaming a file or section is a code change.** Ids and section names are referenced from C#, and
an id that no longer resolves throws at first use, naming the id and where it was looked for.

## How a prompt is found

In order, first hit wins:

1. the directory named by `SUHARNESS_PROMPTS`
2. a `Prompts` folder beside the running binary
3. the copy embedded in `Suharness.Abstractions`

The embedded copy exists so a published binary can never be missing its prompts.

## Line endings

These files are stored with LF (enforced by `.gitattributes`). The loader converts to whatever each
prompt needs — some are assembled with `Environment.NewLine`, some require CRLF because the code
slices them with literal `\r\n` markers. That choice belongs to the calling code, not to the file,
so editing a prompt never has to think about it.

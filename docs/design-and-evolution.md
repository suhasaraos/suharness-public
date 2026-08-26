# Why the Harness Matters as Much as the Model

> **Historical note:** This document captures the architecture during the first public-release phase. SuHarness subsequently added a persistent prompt queue, typed team workflow, and further benchmark-driven hardening. See [Why I Built SuHarness](why-i-built-suharness.md) for the current project story and limitations.

## How SuHarness evolved from a local-model experiment into a model-independent agentic engineering environment

Large language models have become remarkably capable programmers. They explain unfamiliar code, generate functions, diagnose bugs, design architectures, and increasingly operate tools. Yet moving from an impressive coding demo to sustained work on a real repository exposes a distinction that is easy to miss:

**A capable model is not the same thing as a capable coding agent.**

Between the model and the codebase sits another system: the **agent harness**.

The harness decides what the model can see, what it may do, how it remembers, when it is allowed to change something, what happens when a command fails, how tool output comes back, how much context that costs, and how a human inspects the result. It is the machinery that converts raw model capability into engineering work — or fails to.

SuHarness exists because of a narrower question underneath that one:

> **What has to be true of the harness for a model running on your own machine to do real repository work?**

That constraint shaped everything. This document traces how, what the resulting system does differently, and what is deliberately still unbuilt.

---

## 1. The organizing constraint: local models

It would have been easier to build against a frontier API and assume abundance — a very large context window, reliable tool-calling, structured output that always parses, and a provider that reports its own token usage honestly.

SuHarness assumed none of that, because the motivating case was a model running locally through an OpenAI-compatible endpoint such as KoboldCpp. The reference configuration during development was a quantized 35B open-weight model — `koboldcpp/Qwen3.6-35B-A3B-MTP-UD-Q4_K_XL` on KoboldCpp `1.117.1`.

Running a model like that as a software engineering agent surfaces problems that ordinary chat never does:

- **Context is genuinely scarce**, and a large advertised window does not mean the model uses all of it well.
- **Tool schemas are charged before the task is described.** Connect a large MCP server and thousands of tokens are spent before the model reads your request.
- **Local servers report little.** Often just a model ID — no context window, no pricing, sometimes no token usage at all.
- **Failure modes are different.** A server loading weights into VRAM refuses requests in ways indistinguishable from a permanent error.
- **Instruction-following is weaker.** The model is likelier to loop, to stop mid-task, or to answer as though it had finished when it had not.

Each of these could be treated as a model deficiency to wait out. SuHarness treats them as harness responsibilities, which produced its central design rule:

> **Never make the model solve an infrastructure problem the harness can solve deterministically.**

The model should not have to remember every file in a 125,000-file repository through conversation history. It should not have to reason about which of 142 connected tools are relevant. It should not have to infer whether it is allowed to write to disk. It should not have to recover from its own truncated turn.

This turns out to be the useful thing about designing for local models: **the discipline it forces improves the frontier case too.** A harness that wastes nothing is better on every model. Nothing about the architecture is local-only — the same session runs against OpenRouter or any OpenAI-compatible route — but the constraints that shaped it came from the local case.

---

## 2. The harness is part of the intelligence

The most useful idea from the research literature here comes from SWE-agent, whose authors argued that language models are a new category of computer user and benefit from interfaces designed around their strengths and weaknesses — an *agent–computer interface*. Changing that interface measurably changed how well agents navigated repositories and edited files.

The implication is that two systems running the identical model can perform very differently. One gives the model well-shaped tools, clear descriptions, safe edit primitives, clean observations, and durable state. The other hands it a terminal and wishes it luck.

Related findings point the same way. *Lost in the Middle* showed that models use information at the beginning and end of a long context better than material buried in the middle — so a large window is not the same as reliable recall. CodePlan framed repository-scale change as a planning problem because edits propagate through dependencies no single file reveals. Reflexion showed agents improve by feeding execution feedback back into subsequent attempts. Agentless showed that a simpler structured pipeline can beat an elaborate agent, so complexity has to earn its place.

Read together: **model capability and harness capability multiply.** A strong model in a poor harness wastes intelligence. A well-built harness makes a smaller model viable — which is precisely the local-model case.

---

## 3. How it actually evolved

SuHarness was built in a concentrated burst — the repository's history runs from 18 to 20 August 2026 across 37 commits. That compression makes the progression unusually legible, because each phase exists as a commit responding to a specific limitation hit while trying to do real work.

### Phase 1 — Make it act at all

`Initial commit` established a .NET 10 solution and a Blazor Server UI. `Implement core file manipulation tools and session management` gave the model read, write, edit, glob, and grep against a scoped workspace.

This is the point where a chat interface becomes an agent: the model stops describing changes and starts making them.

### Phase 2 — Make the loop honest

`Enhance session event model and improve runtime behavior` and `Add JSONL session store implementation` addressed a subtler problem. A model is stateless between calls; the continuity a user perceives is entirely reconstructed by the harness. If that reconstruction is sloppy — duplicated history, lost tool results, a tool call with no matching result — the agent degrades in ways that look like model stupidity but are not.

SuHarness settled on an **append-only JSONL transcript** as the source of truth, with the model-visible history rebuilt from it on every turn. That separation between *what happened* and *what the model is shown next* became load-bearing for everything after it.

### Phase 3 — Make autonomy a dial

`Introduce access modes for session management and toolset functionality` split capability into `ReadOnly`, `WorkspaceWrite`, and `FullAccess`.

Shortly after, `Implement user question handling and plan mode functionality` added a separate axis. This distinction matters and is often collapsed elsewhere: **planning depth and execution authority are different questions.** You may want deep analysis that touches nothing, or a quick fix with full permission to build and test. Access mode answers *what may it do*; plan mode answers *how deliberately should it work*.

### Phase 4 — Survive scale

Three commits in sequence — timeouts and truncation in `FileWalker`/`SearchTools`, then the spill policy, then the repeat-tool reminder — all respond to the same discovery: **the failure mode of a large repository is not an error, it is an unbounded result.**

A `grep` across a 15 GB tree that returns everything does not crash. It silently consumes the entire context window. So every tool got a cap: `read` returns at most 2,000 lines or 50 KB with a continuation footer, `glob` at most 100 paths, `grep` at most 250 matches grouped by file. Against a ~125,000-file Unity project, both return bounded results in under five seconds.

Results too large to inline are **spilled** — written to disk, replaced in context with a preview and a path (default threshold 50,000 bytes). The model can read them back with `read` or search them with `grep`. Nothing is lost; it simply stops being resident.

### Phase 5 — Survive the model

`Refactor agent lifecycle tool handling and introduce tool call policies` added a pipeline every tool call passes through: `tools/pre-execute` → dispatch → `tools/post-execute` → `tools/result`. Handlers return allow, deny, or ask, and **decisions combine by strictness rather than registration order** — a refusal cannot be reversed by a handler that happened to subscribe later.

Alongside it came recovery behaviour aimed squarely at weaker and local models:

- **The repeat-tool reminder.** A model calling the same tool with identical arguments is not progressing. After 3, 5, and 8 consecutive identical calls an escalating reminder rides back on the tool result. It is advisory — no call is ever refused or rewritten — and arguments are compared canonically, so reordering an argument object does not launder a loop.
- **Context-overflow repair.** An overflow condenses the transcript into a checkpoint and retries within the same turn. Without it a session is permanently stuck, because every later turn replays the same oversized history.
- **Provider-neutral retry.** Transient failures retry with bounded exponential backoff and jitter, honouring `Retry-After` when it fits. Crucially, `"mode": "Always"` retries indefinitely — the behaviour a local server needs while it loads a model into VRAM, where refusals are temporary but look permanent.

Later, `implement auto continuation for unfinished turns` closed the last gap: a model that stops mid-task while claiming to continue gets exactly one automatic continuation prompt.

### Phase 6 — Reach outside the repository

`Add MCP settings component and comprehensive tests for capability handling` connected external Model Context Protocol servers, and immediately produced the problem described in section 5.

### Phase 7 — Make it reversible and accountable

The final phase is about trust rather than capability: Git-backed turn snapshots, then per-file diff and restore, then usage normalization, then turn-based token accounting.

The pattern across all seven phases is consistent. **Each addition removed a reason the model could not finish useful work** — not a feature chosen from a roadmap.

---

## 4. Differentiator: undo that the workspace never sees

Once an agent can change twenty files in a turn, the question stops being *can it edit* and becomes *can I undo this*.

SuHarness snapshots each turn's file changes into **a Git object database the harness owns**, stored under the SuHarness home directory rather than inside the workspace. Working on a repository never writes to that repository's `.git`, so agent history and project history never entangle.

Measured against a 24,000-file workspace on Windows: **121 seconds to index once, then 180 ms to capture a turn and 570 ms to roll one back.** Because the first index is minutes of work, it runs in the background from the moment a workspace opens rather than inside your first turn.

The honesty of the feature is as important as the feature. The confirmation states what undo does *not* cover: anything a shell command did beyond the filesystem — a package install, a database migration, a request that already left the machine. Files over 2 MB are skipped and the snapshot record says which. A workspace that cannot be captured leaves the turn uncovered and says so, rather than silently delaying the work you asked for.

Unreadable snapshot configuration falls back to *enabled*, on the reasoning that a safety feature should not be what a typo switches off.

---

## 5. Differentiator: treating the tool catalog as a context budget

This is where the local-model constraint produced something most harnesses do not have.

Unity's MCP server publishes **142 tools**, whose schemas cost roughly **20,000 tokens** — paid on every request of every turn, before the task is described. On a small local model, most of the context is gone before the model reads what you asked for.

So a server publishing more than **40 tools** has its catalog narrowed to what the turn plausibly needs. Tools are grouped (`terrain`, `rendering`, `hierarchy`, `scripting`, `runtime`, `testing`, …) by the vocabulary in their own names and descriptions — **never by a hard-coded list**, so a new server version regroups itself instead of falling off a whitelist. The turn's wording then selects groups by how much of each group's vocabulary it uses.

The results are task-specific rather than fixed:

| Prompt | Tools kept, of 142 |
| --- | --- |
| "Add some trees to the terrain and bake the navmesh" | 51 |
| "Change the snow material shader colour" | 62 |

Every rule **fails toward keeping tools**, because each is a keyword heuristic and a wrong guess should cost tokens rather than capability:

- `core`, `hierarchy`, `scene`, and unclassified tools are kept for every task — checking your own work is never optional.
- Wording that matches nothing **widens** to a broad safe subset, and says so.
- A filter that would empty a catalog is discarded: that means the classifier is wrong about the server, not that the task needs no tools.
- A catalog at or below the threshold is never touched. Exa's two tools are never filtered because Unity's 142 happen to be connected as well — **the decision is per server.**

Narrowing is only safe with an exit, so a narrowed turn also receives a **`request_capability`** tool. The agent names the group it lacks and what it could not do; the group arrives on the *next* turn. The tool says so plainly rather than implying the tools have appeared, because a model's tool catalog is fixed for the duration of its request.

That last detail is the design philosophy in miniature: **when the harness cannot give the model what it wants, it tells the truth about why instead of failing silently.**

---

## 6. Differentiator: token accounting that refuses to guess

Local and OpenAI-compatible servers report wildly inconsistent usage metadata. The easy path is to fill gaps with estimates. SuHarness does not.

**An unreported count is never stored as zero.** The harness has no tokenizer it can trust for an arbitrary endpoint, so a provider that reports nothing yields `Tokens: —` and *"usage not reported by provider"* rather than a guess presented as a measurement. A blank price means unknown and stays unknown — it is never treated as free. Re-fetching a model catalog fills blanks and nothing else, so it can never overwrite a context window or rate you set by hand.

Accounting runs at three levels that are deliberately kept apart:

```
User turn → LLM call → tool → LLM call → tool → LLM call → final response
```

That is **one turn containing three model calls**, never three turns. The turn boundary comes from the runtime, because nothing in the request stream itself distinguishes a turn's last request from the next turn's first.

The distinction that matters most in practice:

- **Current context** is the prompt of the *latest* call — everything actually sent: system prompt, tool schemas, history, tool results, the user's message. It is shown against the window: `72.6K / 262K (27.7%)`.
- **Tokens processed** is the sum across calls. Every request replays the conversation, so a four-call turn reprocesses most of its history four times.

A session can process millions of tokens through a 262K window without ever coming close to filling it. Cumulative input is never used to compute window utilisation, and cached input is a *subset* of the input count rather than context on top of it. Conflating these is how usage displays end up meaningless — and on a local model, where the window is the binding constraint, a meaningless context indicator is worse than none.

---

## 7. Differentiator: the model-facing contract is treated as an API

Tool names, parameter names, descriptions, and result formatting are treated as a **contract with the model**, versioned as carefully as a public API — because changing them changes behaviour for every existing session that replays its history.

MCP tools get stable, server-qualified names such as `mcp__exa__web_search_exa`. A public name is a pure function of `(server name, raw tool name)`: connection order, a re-sync, and other servers never rename a tool. Names exceeding the provider's 64-character budget are sanitized and truncated with a deterministic hash appended, so **two raw names can never collapse into one.**

Errors are part of the contract too. A refused, escalated, or timed-out call always returns *text* rather than throwing. The model needs a result for every call it made — and a readable one is what lets it choose a different approach instead of stalling.

The same reasoning drives the **observation policy**: a file must be read by this session before it can be edited or overwritten, and a file that changed since that read must be re-read. This is what stops an agent from blind-overwriting your concurrent edits.

---

## 8. Differentiator: extension without a plugin SDK

SuHarness has no plugin API, and the claim is that most extensions do not need one. Because it is open source and already knows how to work against a source repository, the way to extend it is to **open this repository as the workspace and describe the change.**

What makes that practical rather than aspirational is the rest of the architecture: plan mode forces an agreed plan before a file is written; access modes bound what a self-modifying session may do; the observation policy prevents blind overwrites; turn snapshots make any change reversible; and `bash` runs `dotnet build` and `dotnet test`, so compilation failures and test regressions return as ordinary tool results the agent can act on.

Some boundaries remain legitimately external and stay that way: **MCP servers** for third-party capabilities, **skills** discovered from `.suharness/skills` and `.agents/skills`, **model providers** declared at runtime, and **`IAgentFactory`** for replacing the agent implementation wholesale.

This replaces extension *ceremony*, not engineering discipline. Changes are still expected to respect separation of concerns and testability.

---

## 9. Why C# and .NET

Most agent tooling is Python or TypeScript. SuHarness is C# on **.NET 10**, built on **Microsoft Agent Framework** — roughly 25,000 lines across six projects with 450 tests.

The practical arguments are strong typing across model and tool contracts, mature async and streaming primitives, and a dependency-injection architecture that makes the layering enforceable rather than conventional:

```
Abstractions ← Core / Tools ← Infrastructure ← Web ← Cli
```

There is a strategic argument too. Microsoft Agent Framework's own documentation now defines an **agent harness** as the scaffolding required for long multi-step agent work — planning, todo tracking, context compaction, file memory, file access, tool approval — and distinguishes *agents* (model-driven tool use), *harnesses* (long-running interactive work), and *workflows* (explicit sequencing). That is a close description of the problem space SuHarness occupies, which suggests the abstraction boundary is a real one rather than a local invention.

---

## 10. Unity as the stress test

A game project is a deliberately unfair test: large file counts, generated files, C# source, serialized scenes, binary assets, physics, runtime state, editor state, and validation that is partly visual.

Through the Unity MCP server the agent attaches to a **live editor** rather than files on disk — so it reads and drives actual scene state. Because those tools change a live project the harness cannot inspect, the Unity preset ships with `requiresApproval` on, routing every call through the human gate.

An instruction like *"build a level where the skier must jump between cliffs and fails without enough speed"* is not a code-generation task. It is an engineering objective requiring the agent to inspect existing architecture, understand movement and jump physics, find the level-construction system, judge difficulty against existing levels, change code and scene state, run the editor, observe results, and iterate.

Long-horizon work like that exposes harness weaknesses far faster than isolated coding benchmarks. Most of the caps, budgets, and recovery paths described above exist because this workload broke something.

---

## 11. What is actually built

| Layer | Capability |
| --- | --- |
| **Models** | Any OpenAI-compatible route — local or hosted; per-session selection |
| **Agent loop** | Multi-step model → tool → observation, 100 round-trips per turn by default |
| **Workspace tools** | `read`, `write`, `edit`, `glob`, `grep`, `bash`, `skill`, `lsp` — all bounded and scoped |
| **Access control** | `ReadOnly` / `WorkspaceWrite` / `FullAccess`, plus a two-tool `Minimal` preset |
| **Planning** | Plan mode with `exit_plan_mode` as the only exit; todo tracking |
| **Sessions** | Append-only JSONL transcripts; resumable, renameable, archivable |
| **Context** | Spill store, checkpoint compaction at 80% of window, overflow repair |
| **MCP** | Runtime server config, per-server approval, backoff-based reconnection |
| **Tool budget** | Per-server catalog narrowing above 40 tools, with `request_capability` as the exit |
| **Orchestration** | `subagent`, `subagent_fork`, `workflow`, `ralph`, agent inbox, jobs, goals |
| **Undo** | Harness-owned Git snapshots; per-file diff and revert |
| **Policy** | `pre-execute` → dispatch → `post-execute` → `result`, combining by strictness |
| **Resilience** | Repeat-tool reminders, typed retry policies, one automatic continuation |
| **Accounting** | Three-level token accounting; optional pricing; never guesses |
| **Multimodal** | Content-addressed image attachments, verified on replay |

Worth correcting a common misreading: **orchestration is not aspirational here.** The `workflow` tool runs a sandboxed JavaScript-subset interpreter supporting `agent`, `pipeline`, `parallel`, and `phase` — with no filesystem, network, timers, or Node APIs — so fan-out and fan-in across subagents already work. `ralph` runs a fixed-orchestration loop where each round spawns a fresh subagent carrying the immutable objective plus the previous round's handoff.

What is *not* built is the layer above that.

---

## 12. What is not built

Two capabilities are genuinely still ahead, and they are related.

**Persistent, dependency-aware prompt graphs.** The `workflow` tool orchestrates *within* a turn, as a script the model writes. It does not give prompts durable identities, dependency edges, blocked/ready states, or resumption across restarts. The difference between a script and a scheduler is persistence and recovery.

**Capability-aware model routing.** Today a session selects one model and its subagents inherit it. The ambition is an orchestrator that decomposes an objective and routes each piece to the model best suited to it — a reasoning model for architecture, a coding specialist for implementation, a cheap local model for repetitive passes, a vision model to look at a rendered frame.

That is materially harder than running several copies of one agent in parallel. An orchestrator must reason about capability, cost, latency, context requirements, modality, and how to reintegrate outputs. It belongs after the single-agent case is genuinely solid — which is the same reasoning Anthropic, OpenAI, and Microsoft all give for introducing orchestration progressively rather than by default.

Combined, the two would move SuHarness from a capable single-agent harness toward a multi-model execution platform. Separately, each is a large piece of work.

---

## 13. The lesson

The industry's attention naturally goes to models: parameter counts, benchmarks, context windows, quantization, tokens per second. All of it matters.

But an autonomous engineering system is not a model benchmark. Its effective capability looks more like a product than a sum:

```
Agent effectiveness
      =  model capability
      ×  context quality
      ×  tool quality
      ×  planning quality
      ×  feedback quality
      ×  state management
      ×  safety and control
      ×  user experience
```

Not a literal equation — but it captures the point that **failure in any one term neutralizes strength in the others**. A brilliant model given irrelevant context fails. A strong coding model with unreliable edit primitives corrupts files. A model handed 142 tools picks the wrong one. An autonomous agent without permission boundaries is dangerous. An agent whose changes cannot be inspected cannot be trusted. A perfect workflow on an incapable model still fails.

The harness is what connects those terms. Designing for a local model made that unavoidable, because there was no surplus capability to absorb a weak link.

As models become more capable, more numerous, and more interchangeable, the interesting question shifts. It stops being:

> Which model is smartest?

and becomes:

> **Which system lets that intelligence do the best work?**

That system is the harness. That is what SuHarness is built to explore.

---

## Further reading

- [`README.md`](../README.md) — full feature and configuration reference
- [`SECURITY.md`](../SECURITY.md) — the threat model, and why there is no authentication
- [`CONTRIBUTING.md`](../CONTRIBUTING.md) — architecture, layering rules, and adding a tool
- [`CHANGELOG.md`](../CHANGELOG.md) — release history

**Referenced research:** SWE-agent (agent–computer interfaces) · *Lost in the Middle* (positional context degradation) · CodePlan (repository change as planning) · Reflexion (learning from execution feedback) · Agentless (simpler pipelines as a baseline) · SWE-bench (realistic multi-file evaluation)

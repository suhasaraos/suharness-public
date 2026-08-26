# Why I Built SuHarness

## Making large codebases workable for small local models

I did not build SuHarness because the world needed another chat window for code. I built it around a harder question:

> Can a small, locally hosted model do useful work in a large, unfamiliar codebase if the surrounding system carries more of the engineering burden?

That distinction matters. A model can be good at producing code in isolation and still be unreliable inside a real repository. It has to find the right files, understand local conventions, preserve constraints across many tool calls, make a coherent change, run the right validation, recognize when that validation is insufficient, and recover when any of those steps go wrong. Large models struggle with this too. Smaller local models simply make the weaknesses impossible to ignore.

SuHarness is my attempt to treat those weaknesses as systems problems rather than waiting for a model to make them disappear. It has grown from a small agent loop into a local-first coding harness with bounded repository discovery, persistent sessions, recovery policies, snapshots, a durable prompt queue, and an explicit multi-role team workflow.

It is still buggy. Some parts are too specific. Some provider integrations are fragile, and some validation logic has produced conclusions that were later proved wrong. I am open sourcing it anyway—not because it is finished, but because it has become more than an experiment. It is now a working engineering system whose failures are useful, inspectable, and fixable.

## The constraint was the point

The original target was not “the best model available through an API.” It was a small model running locally through an OpenAI-compatible endpoint.

That choice creates real constraints:

- Context is scarce, so sending the entire repository is usually impossible and often counterproductive.
- Tool use can be inconsistent, especially across providers that serialize or interpret tool calls differently.
- Long tasks drift. A model may forget the acceptance criteria, repeat the same search, or stop after explaining what it intends to do.
- Repository-scale work is not one act of code generation. It is a sequence of discovery, planning, editing, building, testing, reviewing, and sometimes repairing.
- A plausible answer is not the same thing as a correct patch.

Research supports the broader premise behind this design. The [SWE-agent paper](https://arxiv.org/abs/2405.15793) showed that the interface between an agent and a computer materially affects repository-level performance. [CodePlan](https://www.microsoft.com/en-us/research/publication/codeplan-repository-level-coding-using-llms-and-planning/) frames repository work as a planning problem because changes are interdependent and the relevant code cannot simply be placed in one prompt. The [Lost in the Middle study](https://arxiv.org/abs/2307.03172) also found that a large context window does not guarantee reliable use of information throughout that window.

Those results match my experience: the model is only one component. The harness decides what the model sees, what it can do, what evidence comes back, and what happens when it fails.

## From an agent loop to a harness

The first SuHarness commit landed on 18 August 2026. The early version was deliberately small: an event loop, a session, a handful of workspace tools, and support for OpenAI-compatible model endpoints. The goal was to get a local model to inspect and change a real repository without giving it an unbounded shell and hoping for the best.

The Git history shows the system growing in response to concrete failure modes rather than from a fixed product blueprint.

### 1. Give the model a controlled way to work

The first layer was a practical tool surface: list and search files, read bounded ranges, apply patches, run commands, inspect diffs, and keep a durable JSONL session record. Access modes and plan mode separated inspection from mutation. Approval gates made higher-risk actions visible instead of silently granting the model whatever it requested.

This established a basic rule that still shapes the project: capability should be explicit. The harness should know whether an operation reads, writes, or executes, and the user should be able to narrow those capabilities for a session or queued job.

### 2. Make repository discovery survive scale

The next problems appeared as soon as the repository stopped fitting comfortably into a prompt. Search output flooded the context. Long files consumed the budget. Repeated directory scans displaced the actual task. The response was not “use a bigger prompt”; it was to build better context machinery:

- bounded search and file reads;
- truncation with explicit spill files instead of silent loss;
- symbol and definition lookup;
- repository indexing;
- optional language-server integration;
- tool discovery and Model Context Protocol support;
- capability narrowing so a model sees the tools relevant to its current job.

This is especially important for smaller models. They benefit from high-signal slices of a codebase: the symbol, its callers, the nearby tests, and the build result—not thousands of loosely related lines.

### 3. Treat failure as a state, not a surprise

The early agent could fail in predictable ways: repeat the same tool call, stop without a final answer, misclassify a test failure, exceed a turn limit, or leave the workspace half changed. SuHarness gradually acquired recovery and control mechanisms:

- repeat-call and no-progress detection;
- bounded automatic continuation;
- token and turn budgets;
- tool lifecycle policies;
- snapshots, diffs, and restore points;
- richer diagnostics and usage telemetry;
- explicit blocked, failed, cancelled, and completed states.

This part of the harness is not glamorous, but it is the difference between a demo and something I can leave running on a real task. A useful coding agent must make failure legible. It should preserve enough state to explain what happened and, where safe, continue from it.

## Why the prompt queue appeared

A chat interface works for one interactive request. It is a poor interface for a backlog.

By 20 August, SuHarness had a persistent prompt queue. A queued item can carry its own provider and model, reasoning effort, access level, priority, scheduled time, plan-mode setting, session, and dependency. Jobs move through explicit states such as queued, waiting, ready, running, completed, failed, blocked, and cancelled. A failed prerequisite blocks downstream work instead of letting it proceed on a false assumption, and interrupted work can be returned to the queue.

The queue changes the unit of work. Instead of babysitting a conversation, I can define several bounded jobs and let the harness preserve their intent and ordering. That matters when local inference is slow or when different stages need different models.

The current queue is intentionally modest. It is a durable, single-process scheduler, not a distributed build system. It does not yet provide the concurrency controls, workspace locking, or general DAG resumption I ultimately want. But it establishes the right abstraction: prompts are work items with policy and lifecycle, not disposable chat messages.

## Why one agent became a team

Delegation appeared early in the project, but by 25 August it had evolved into a typed coding workflow with six roles:

1. **Explorer** gathers repository evidence.
2. **Planner** turns that evidence into an implementation plan.
3. **Senior Developer** defines the approach and bounded microtasks.
4. Up to two **Junior Developers** implement those microtasks sequentially in the shared workspace.
5. **Reviewer** inspects the patch and can request repair.
6. **Verifier** runs independent checks and makes the final technical assessment.

Each role gets a fresh context and can be assigned its own provider and model. The workflow is bounded: it limits delegated implementation tasks and repair rounds, and it returns a structured result to the parent agent.

The reason for roles is not to simulate an org chart. It is to separate incompatible cognitive jobs. Exploration rewards breadth. Implementation rewards focus. Review should be skeptical of decisions made during implementation. Verification should care about observable behavior, not how convincing the patch looks.

There is evidence for keeping this architecture simple and measurable. [Agentless](https://arxiv.org/abs/2407.01489) demonstrated that a relatively direct localization, repair, and validation pipeline can outperform more elaborate agents. [Reflexion](https://papers.neurips.cc/paper_files/paper/2023/hash/1b44b878bb782e6954cd888628510e90-Abstract-Conference.html) showed the value of explicit feedback and retry. My conclusion is not that more agents are automatically better. It is that decomposition should exist only where it creates better evidence, tighter context, or a useful feedback boundary.

Team mode is also not fully local by default today. The current fallback configuration assigns a cloud Gemini model to junior implementation unless the user overrides every persona. That was useful for hybrid experiments, but it is a provider assumption I intend to remove in favor of capability-based, provider-neutral routing.

## What the comparisons actually showed

I used a five-prompt legacy C# benchmark against the nopCommerce codebase to see how the harness behaved on repository changes involving routing, HTML helpers, dependency injection, and tests. It is a small development benchmark, not a public leaderboard and not evidence of general superiority.

In the strongest controlled comparison, a preserved GitHub Copilot CLI run using Gemini 3.7 Flash was compared with clean SuHarness runs using the same model family through OpenRouter:

| System | Outcome | Wall time | Recorded tool calls | Composite score |
| --- | ---: | ---: | ---: | ---: |
| GitHub Copilot CLI | 5 complete | 52m 25.5s | 586 | 7.7/10 |
| SuHarness | 4 complete, 1 partial | 29m 53s | 362 | 7.5/10 |

SuHarness was about 43% faster and recorded about 38% fewer tool calls, but those numbers need careful qualification. The Copilot run was preserved rather than rerun. SuHarness's final result was composed from independently clean prompt runs. Provider telemetry and call categories were not identical, and the sample contained only five tasks from one legacy repository. These results are useful for diagnosing this harness; they are not a fair basis for claiming that SuHarness is universally faster or better.

The correctness failures matter more than the headline numbers. All five SuHarness workspaces had focused tests that passed, yet one prompt was still only partially correct because its implementation missed required semantics. Another hit its turn budget before producing a final response. An earlier full SuHarness rerun had completed only one of the five prompts. Improving discovery, validation classification, and continuation policies produced the later result. That progression is the real evidence: harness changes measurably changed outcomes even when the underlying task set did not.

The local-model results were harsher and more informative. An Ornith 1.0 run completed none of the five prompts: four were partial, one failed, no focused task test passed, and the run took about 69 minutes 45 seconds. It often found relevant files but lost task constraints, substituted prose for action, or accepted weak validation. A tuned run created more artifacts but also exposed a dangerous failure mode: the harness could report success when the behavior was still wrong.

Later hybrid team runs went further. A local parent handled exploration, planning, review, and verification while a cloud model performed bounded implementation tasks. One candidate built and passed its generated tests and an additional seven-case probe. I initially accepted it. I then retracted that result after an independent check showed malformed HTML for an existing self-closing-anchor case.

That retraction is part of the project record because it captures the central lesson: tests written inside the same reasoning loop can validate the wrong interpretation. A reviewer can inherit the implementer's blind spot. More roles do not create independence unless their evidence is genuinely independent.

Earlier benchmark reports are preserved in the Git history, including the [five-prompt validation comparison](https://github.com/suhasaraos/SuHarness/blob/69d112bb947814d77888b2dd9711ad3aa10c7cf9/benchmark-results/2026-08-21-validation-classification-rerun/FINAL-COMPARISON.md) and the [first local Ornith run](https://github.com/suhasaraos/SuHarness/blob/ab74c62790843da818d4da59a60ae7bc53af5668/benchmark-results/2026-08-21-local-ornith-run/FINAL-COMPARISON.md). Their value is diagnostic, and their limitations should travel with any number quoted from them.

## Why I am open sourcing it now

It would be easier to wait until the rough edges were gone. I do not think that would produce a better harness.

First, harness behavior needs to be inspectable. The model receives a filtered view of the repository and acts through policies written by the harness. Those policies can introduce bias, hide failures, or accidentally make a benchmark easier. Open source makes the interface, prompts, tool contracts, recovery rules, and validation decisions available for scrutiny. OpenAI has made a similar argument in describing its own [open-source Codex harness](https://learn.chatgpt.com/blog/codex-as-a-platform): the harness is the layer that assembles context, tools, boundaries, approvals, and the work loop, and opening it lets developers inspect and adapt those choices.

Second, local inference is too diverse for one developer to cover. Different servers vary in tool-call formats, streaming behavior, token accounting, context limits, and error responses. Hardware and operating systems introduce another layer of variation. I have already seen ostensibly OpenAI-compatible providers return empty post-tool responses or emit tool calls as literal text. The only credible route to flexibility is to let people test and improve the adapters they actually use.

Third, open development creates an audit trail. The comparison failures, false positives, and retracted acceptance are not embarrassing debris to delete; they are evidence about where agent systems break. If SuHarness eventually becomes reliable, I want that claim to rest on reproducible history rather than a polished demo.

Fourth, the project can improve itself. SuHarness can inspect and modify its own repository under the same limits it applies elsewhere. That does not make it self-sufficient, but it creates a useful feedback loop: every weakness encountered while extending the harness can become a test, policy, tool, or clearer boundary.

Finally, I cannot test every model, provider, language server, repository shape, and local setup myself. Apache 2.0 gives people room to use the project, adapt it, build on it, and contribute fixes without pretending that the current implementation is a finished product.

## What SuHarness is—and is not—today

As of 25 August 2026, the main branch contains 49 commits. Excluding generated build output, it has roughly 22,500 lines of source and 10,800 lines of tests. The current test run contains 641 cases: 638 pass and three live MCP tests are skipped when their external service is unavailable.

That test count is a health signal, not proof of autonomous correctness. The repository currently provides:

- OpenAI-compatible local and remote model connections;
- bounded file, search, patch, shell, index, symbol, and optional LSP tools;
- persistent sessions, approvals, access modes, snapshots, budgets, and recovery policies;
- MCP integration and tool-capability narrowing;
- a durable prompt queue with priorities, schedules, and success dependencies;
- a configurable role-based team workflow;
- a local web interface and command-line entry point.

It also has important limitations:

- It is designed as a single-user, loopback application and has no authentication layer.
- Shell access can execute commands with the permissions of the user running the harness.
- Provider compatibility is uneven despite similar API shapes.
- Validation heuristics can still confuse “a command ran” with “the requested behavior is correct.”
- Parts of the typed coding workflow encode lessons from a narrow C# benchmark and need to become more general.
- The default team fallback still contains a cloud-provider assumption.
- Queue execution is local and effectively serial rather than a safe concurrent scheduler.
- The benchmark evidence is small, repository-specific, and unsuitable for broad model rankings.

These are not footnotes. They define where the next work is.

## Where I want to take it

The direction is a more flexible, evidence-driven harness rather than an ever larger pile of prompts.

The immediate priorities are:

- provider-neutral persona assignment based on capabilities, cost, latency, and context limits;
- stronger structured validation that distinguishes build success, focused tests, independent behavioral checks, and unresolved uncertainty;
- reusable workflow profiles instead of task-specific orchestration rules;
- safer queue concurrency with workspace resource locks and resumable dependency graphs;
- better grounding and context compression for small local models;
- broader, reproducible evaluations across repositories, languages, providers, and model sizes;
- clearer security boundaries and deployment options beyond the current trusted local setup.

I expect the architecture to keep changing. The queue may become more capable. Team roles may become fewer, more dynamic, or model-selected. Some recovery logic may prove unnecessary as models improve. That is healthy. The objective is not to preserve today's design; it is to preserve the problem-solving principles behind it.

## More than an experiment

SuHarness began as an experiment in making small local models useful on large codebases. It crossed the line into something more when failures stopped being isolated anecdotes and started turning into durable engineering: a bounded search tool, a recovery state, a snapshot, a queue dependency, a reviewer role, an independent test, or a retracted result.

I am not claiming that it solves autonomous software engineering, or that a small local model now matches the best hosted coding agents. The evidence does not support either claim. I am claiming something narrower and, I think, more useful:

> A carefully designed harness can make smaller models substantially more capable, observable, and safe on real repository work—and the harness itself is an open engineering problem worth working on.

That is why I built SuHarness. That is why I am continuing to fix it. And that is why I am opening the work before it is finished.

---

### Evidence note

Project chronology, code and test counts, workflow behavior, and queue semantics in this article were checked against the SuHarness Git history and source tree on 25 August 2026. The test suite was run locally with 638 passing and three skipped live-service tests. Benchmark figures come from the project's preserved comparison reports; they should be read with the methodological caveats stated above.

# Changelog

All notable changes to SuHarness are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

While SuHarness is pre-1.0, minor versions may contain breaking changes. Tool
names, parameter names, tool descriptions, and result formatting are treated as
a model-facing contract: changes to them are called out as **Changed** even when
no C# API moved, because they alter behaviour for existing sessions.

## Unreleased

### Added

- **Reasoning effort.** A reasoning model can be asked for a `Minimal`, `Low`,
  `Medium`, or `High` reasoning budget, sent as `reasoning_effort` on the
  OpenAI-compatible request. The default is set per model on its catalog row and
  overridden per session from the composer's model picker; switching models drops
  the override. Leaving it at **Default** sends no reasoning parameter at all,
  which is what an endpoint that does not know the field requires — it rejects
  the request rather than ignoring it, so the setting is per-model rather than
  per-route, where one provider serves reasoning and non-reasoning models
  together. Reasoning tokens were already counted; now they can be asked for.

- **Persistent workspace index.** `glob` and `grep` are served from a retained
  table of the workspace instead of walking the tree on every call. One walk is
  held in memory, written to `~/.suharness/index/`, and reused across turns,
  sessions, and process restarts. A two-second freshness window collapses a burst
  of searches within one turn into a single traversal; past it the tree is
  rescanned and the table replaced only if something actually changed. `write`
  and `edit` fold their own changes in directly, and `bash` marks the table for
  confirmation. Results are byte-identical to the walker, which remains the
  fallback for a full-access session searching outside its workspace.
- **`symbol` tool.** Finds where a type, method, or function is declared, by
  name, from a declaration table built over the same walk. A tolerant line
  scanner covers C#, Java, Python, TypeScript, JavaScript, Go, Rust, C, C++,
  Ruby, PHP, Kotlin, Swift, and Scala. It needs no build and answers on code that
  does not compile, at the cost of not resolving overloads or receivers. Matching
  runs from an exact name down to a subsequence, so one call replaces a run of
  guessed glob patterns and cannot miss on capitalisation.
- **Built-in language servers for .NET, Java, and Python.** Launch recipes for
  `csharp-ls`, the Roslyn language server, OmniSharp, Eclipse JDT, Pyright,
  pylsp, and jedi. Nothing is installed on your behalf: a preset becomes a server
  only if its command resolves on `PATH`, and only one preset per language is
  used. Any other language is added by declaring it in `lsp.json`, which produces
  the same record the presets do.
- **`workspace/symbol` and `textDocument/documentSymbol`.** The `lsp` tool gains
  `symbol` and `document_symbols` operations, and accepts a `symbol` name in
  place of `line` and `column`. Reaching a semantic operation previously required
  already knowing a file, line, and column, which could only be found by
  searching — the step the semantic query was meant to replace.

### Changed

- **Language servers are pooled and long-lived.** A server is now initialised
  once per workspace and reused for every later query, with idle shutdown,
  crash recovery, and document sync on change. Each query previously started a
  process, completed the handshake, and killed the process tree, so the project
  load was paid per question and routinely exceeded the query budget before the
  server could answer.
- **`ILanguageServerService`.** `IsConfigured` becomes `IsConfiguredFor(root)`
  and `Servers(root)` is added, because a server's resolved arguments depend on
  the workspace. `LanguageServerConfig` gains `InitializeTimeoutMs`,
  `IdleShutdownMs`, and `WarmupRetries`; `lsp.json` gains `useBuiltIns`.
- **`glob` tie-breaking.** Files with identical modification times are now
  ordered by path, so repeated calls return byte-identical output.
- **`grep` file order.** Matches are grouped in path order rather than in the
  order the walk happened to reach them. This is model-facing: `grep` stops
  retaining after 250 matches, so traversal order decides which matches survive
  the cap, and the previous order was an artifact of depth-first traversal.

## 0.1.0 - 2026-08-26

First public release. SuHarness has been usable for a while; this is the point
at which the repository became something other people can read, build, and
contribute to.

### Added

- **Session harness.** A model-neutral turn loop over Microsoft Agent Framework,
  with append-only JSONL transcripts, resumable sessions, and full reconstruction
  of model-visible history — assistant tool calls and tool results included —
  from the log.
- **Workspace-scoped tools.** Read, write, edit, glob, grep, and bash, resolved
  through a single workspace scope, with a read-before-mutate rule enforced per
  session.
- **Access modes.** Read, Write, and Execute, selectable per session from the
  composer.
- **Turn snapshots and revert.** Each turn's file changes are captured into a Git
  object database the harness owns, held outside the workspace so a user's own
  repository is never written to. A turn can be undone after the fact.
- **MCP support.** Servers declared in `configuration/mcp.json` contribute tools
  under stable `mcp__<server>__<tool>` names. Server secrets are stored beside
  the model provider keys, protected and write-only. A server may be marked
  *requires approval*, which routes each of its calls through the human gate.
- **Capability narrowing and escalation.** A large tool catalog is narrowed per
  turn, with an explicit way for the agent to ask for a capability group the
  filter withheld.
- **Orchestration.** Goals, jobs, subagents, an agent inbox, and a plan mode.
- **Spill store.** Oversized tool results move out of the context window and are
  replaced with a preview plus a path the model can read back.
- **Repeat-tool reminder.** An advisory loop-breaker that counts consecutive
  identical calls and nudges the model to change approach. It never vetoes a call.
- **Compaction.** Transcript compaction for long sessions.
- **Token accounting.** Per-turn usage and cost tracking, normalised across
  providers, written through to the session record at turn boundaries.
- **Image attachments.** Content-addressed, magic-byte validated, integrity
  checked on read.
- **Language server integration.**
- **Skills.** Loaded from `.suharness/skills`, `.agents/skills`, and the
  SuHarness home.
- **Workspace instructions.** The `AGENTS.md` / `CLAUDE.md` chain from the
  project root down.
- **Browser UI.** A Blazor Server interface with streaming responses, session
  management, workspace picking, and settings for models and MCP servers.
- **`suharness web` CLI**, which owns the loopback-only binding policy.

### Security

- **Markdown rendering is sanitised.** Assistant text, tool results, and MCP
  output are not authored by SuHarness, so raw HTML is escaped and link and image
  destinations are restricted to an allow list of schemes. A `javascript:` URL
  can no longer become a live link. URL normalisation matches what a browser does
  before it parses a scheme, so tab, newline, carriage-return, and leading-control
  obfuscation do not bypass the check. See `SafeMarkdown`.
- **Security headers on every response.** A strict Content Security Policy with a
  per-response nonce for the host page's single inline script, so `script-src`
  never needs `'unsafe-inline'`; plus `X-Content-Type-Options`, `X-Frame-Options`,
  `Referrer-Policy`, `Cross-Origin-Opener-Policy`, `Cross-Origin-Resource-Policy`,
  and `Permissions-Policy`.
- **The accepted `Host` header is restricted to loopback names.** Previously any
  host was accepted, which allowed a hostname pointed at `127.0.0.1` by someone
  else's DNS to reach the UI from a page the user was visiting.
- **The threat model is written down.** [SECURITY.md](SECURITY.md) states what
  SuHarness assumes — single user, loopback only, no authentication, agent code
  execution with the user's privileges — and documents that Data Protection keys
  are stored in plaintext on Linux and macOS, where DPAPI has no equivalent.
- **Credentials are write-only in the UI** and are never returned to the browser
  once saved.

### Documentation

- [SECURITY.md](SECURITY.md), [CONTRIBUTING.md](CONTRIBUTING.md), and
  [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) covering the full 44-package
  dependency closure, all permissive.

The dependency notice is updated whenever the restored package closure changes.

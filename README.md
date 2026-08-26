# SuHarness

SuHarness is an agent harness built on .NET 10 and Microsoft Agent Framework.

SuHarness is **self-extensible**. It is an open-source coding-agent workspace whose own source is
part of its interface: the ordinary way to add a capability is to open this repository as the
workspace and ask SuHarness to build it. There is no plugin SDK to learn, no manifest to author, and
no registration ceremony — see [Extending SuHarness](#extending-suharness).

The repository intentionally preconfigures **no model, endpoint, deployment name, or credentials**. The Web UI can declare OpenAI-compatible provider routes at runtime; Microsoft Agent Framework creates the selected agent on demand.

## Run

```powershell
dotnet run --project src/Suharness.Cli -- web
```

The Web UI is served at `http://127.0.0.1:3090` by default. Session metadata and append-only JSONL events are stored under `%USERPROFILE%\.suharness\sessions`, or beneath `SUHARNESS_HOME` when set.

`run-su-harness.bat` (Windows) and `run-su-harness.sh` (Linux, macOS) build and launch in one step, and take an optional port.

> [!IMPORTANT]
> **SuHarness is a single-user tool with no authentication, and its `bash` tool runs commands with your privileges.** It is meant to be reached only from the machine it runs on, which is why `suharness web` refuses to bind anywhere but loopback. Running `Suharness.Web` directly with a different `--urls` bypasses that check — do not expose SuHarness to a network. On Linux and macOS the credential key ring is stored in plaintext, so treat `~/.suharness` as being as sensitive as `~/.ssh`. Read [SECURITY.md](SECURITY.md) before deploying it anywhere.

## Design background

[Why I Built SuHarness](docs/why-i-built-suharness.md) explains the local-model goal, how the
prompt queue and team workflow emerged, what evaluation failures revealed, and why the
project is being developed in the open before it is finished.

## Architecture

- `Suharness.Abstractions` contains the session, persistence, tool, and Agent Framework contracts.
- `Suharness.Core` owns the model-neutral session loop and turn orchestration.
- `Suharness.Tools` implements the workspace-scoped tool set the agent calls.
- `Suharness.Infrastructure` provides append-only local JSONL persistence and workspace snapshots.
- `Suharness.Web` provides the interactive server-rendered browser UI.
- `Suharness.Cli` owns the `suharness web` command and loopback binding policy.

## Tools

The agent is given a stable workspace tool set with bounded results and consistent error wording. Tool names,
parameter names, and result formats are deliberately consistent so transcripts remain predictable.

| Tool | Parameters | Notes |
| --- | --- | --- |
| `read` | `file_path`, `offset`, `limit` | Line-numbered window; 2000 lines and 50 KB per call, with a continuation footer. |
| `write` | `file_path`, `content` | Creates, or replaces a file this session has already read. |
| `edit` | `file_path`, `old_string`, `new_string`, `replace_all` | Literal replacement; unique match unless `replace_all`. |
| `glob` | `pattern`, `path` | Files only, up to 100 paths in modification-time order. Hidden and ignored files included; VCS directories excluded. |
| `grep` | `pattern`, `path`, `include` | Up to 250 matches grouped by file. Honours `.gitignore` and skips hidden and binary files. |
| `symbol` | `name`, `kind`, `limit` | Finds where a type, method, or function is declared, by name, from the workspace declaration index. Case-insensitive and partial-name tolerant. |
| `skill` | `name` | Loads reusable project or user instructions on demand. |
| `lsp` | `operation`, `file_path`, `symbol`, `line`, `column` | Semantic definition, reference, implementation, hover, document symbol, and project-wide symbol queries when a server is available. |
| `bash` | `command`, `description`, `timeoutMs`, `workdir` | Fresh `bash -c` per call; non-zero exits are reported as `[exit code: N]`. |

Every path resolves against the session workspace, and a path that escapes it is refused. Mutations
are gated by a per-session observation log: a file must be read by this session before it can be
edited or overwritten, and a file that changed since that read must be re-read first. This observation
policy is what stops a blind overwrite from discarding work.

These caps are what make a very large workspace usable. Against a 15 GB, ~125,000-file Unity project,
`glob` and `grep` each return a bounded result in under five seconds.

### The workspace index

`glob` and `grep` are served from a retained table of the workspace rather than by walking the tree
per call. One walk is kept in memory, written to `%USERPROFILE%\.suharness\index\`, and reused across
turns, sessions, and process restarts. The results are identical either way — the index records what
each search policy would have decided about every file, so `glob` still sees hidden and ignored files
and `grep` still skips them — and the walker remains the fallback for a full-access session searching
outside its workspace.

Freshness has two stages. A short window (two seconds) serves repeated searches with no filesystem
access at all, which is what collapses a burst of searches within one turn into a single traversal.
Past that window the tree is rescanned and the table replaced only if something actually changed, so
an unchanged workspace keeps its table and everything cached on it. `write` and `edit` fold their own
changes in directly, and `bash` marks the table for confirmation because a shell command can change
the tree in ways nothing else reports.

Directory write stamps are deliberately *not* used to decide what to rescan. The system clock advances
in roughly fifteen-millisecond steps, so a file created and another deleted within one step leave a
directory reporting the write time it already had, and the change is missed. Finding the
subdirectories to descend into requires enumerating each directory anyway, so skipping the file half
of that enumeration saved very little and cost correctness.

Both tools also visit files in a stable path order, in the indexed path and the walker alike. Order
is not cosmetic for `grep`: it stops retaining after 250 matches, so traversal order decides which
matches survive the cap.

### Finding a declaration by name

`symbol` answers from a declaration table built over the same walk. A tolerant line scanner extracts
types, methods, and functions from C#, Java, Python, TypeScript, JavaScript, Go, Rust, C, C++, Ruby,
PHP, Kotlin, Swift, and Scala. It is not a parser: it cannot disambiguate overloads or resolve a
receiver, and it takes at most one declaration per line. What it has instead is that it always works —
no build, no project file, no toolchain, and it answers on code that does not compile, which is
frequently why an agent is reading the code at all. Files that `grep` would skip are skipped here too,
so a vendored dependency tree stays out of the table.

Matching runs from an exact name down to a subsequence, so one call replaces a run of guessed glob
patterns and cannot miss on capitalisation. Qualify the query as `Type.Member` to narrow by the
enclosing type. Rescanning is per file: only files whose size or write time moved are read again.

Use `symbol` when a request names something; use `grep` for other text, and `lsp` when the answer has
to be exact.

## Workspace `.suharness`

Each workspace can keep SuHarness-specific instructions and reusable skills under `.suharness/`:

- `instructions.md` is the workspace's highest-priority repository guidance. It is composed with
  `AGENTS.md`, `CLAUDE.md`, and supported `.github` instruction files, and reaches both the standard
  agent and Team workflow roles.
- `skills/` contains flat Markdown skills or `<name>/SKILL.md` bundles.
- `index.json`, `architecture.md`, and `knowledge.json` are bounded, regenerable navigation and
  reviewed-knowledge projections.
- `sessions/<id>/` contains regenerable task, working-memory, finding, role-summary, and attachment
  projections for that workspace session.

SuHarness creates `.suharness/.gitignore` so generated metadata, private session state, cache, and
temporary files stay out of Git and the Modified Files panel. Instructions and skills remain
trackable. General glob, grep, and symbol discovery excludes private `.suharness` state; the harness
loads known instruction, skill, and context files directly. Settings includes a `.suharness` page for
status, metadata regeneration, and an explicit one-way import from an existing `.suagent` folder.

## Skills

Reusable instructions are discovered from `.suharness/skills` and `.agents/skills` beneath the
workspace, followed by `%USERPROFILE%\.suharness\skills` (or `SUHARNESS_HOME\skills`). A skill may
be a `<name>/SKILL.md` bundle or a flat `<name>.md` file. Optional YAML front matter supplies `name`,
`description`, and `disable-model-invocation`. Only names and descriptions enter the tool catalog;
the full body is loaded when the agent calls `skill`.

## Image attachments

The composer accepts up to four PNG, JPEG, WebP, or GIF images per message, with a 10 MB limit per
image. Bytes are validated and stored immutably under the SuHarness home directory. Session events
contain content-addressed references rather than base64, and replay verifies each object before it
is sent to a multimodal model.

## Language servers

Semantic navigation works out of the box for .NET, Java, and Python when the corresponding server is
installed. SuHarness ships launch recipes and uses the first one whose command resolves on `PATH`.
Nothing is downloaded or installed on your behalf, and a recipe whose command is absent simply does
not appear, so the catalog degrades to nothing on a machine with no toolchains rather than producing
servers that fail on first use.

| Language | Preset ids, in preference order | Install |
| --- | --- | --- |
| .NET (`.cs`) | `dotnet-csharp-ls`, `dotnet-roslyn`, `dotnet-omnisharp` | `dotnet tool install --global csharp-ls` |
| Java (`.java`) | `java-jdtls` | Eclipse JDT Language Server, with `jdtls` on `PATH` |
| Python (`.py`, `.pyi`) | `python-pyright`, `python-pylsp`, `python-jedi` | `npm i -g pyright`, or `pip install python-lsp-server` |

Only one preset per language is used, so a machine with both `pyright` and `pylsp` gets one Python
server rather than two competing ones. Eclipse JDT is given a harness-owned data directory under
`%USERPROFILE%\.suharness\lsp\`, never the project directory, because it fills whatever it is given
with derived state.

### Adding any other language

Every other language is added by declaring a server in
`%USERPROFILE%\.suharness\configuration\lsp.json` (or the equivalent beneath `SUHARNESS_HOME`). There
is no separate mechanism for the built-ins — they resolve to exactly this record, so a hand-written
server behaves identically to a shipped one:

```json
{
  "servers": [
    {
      "id": "rust",
      "command": "rust-analyzer",
      "args": [],
      "extensions": { ".rs": "rust" },
      "timeoutMs": 20000,
      "initializeTimeoutMs": 90000,
      "idleShutdownMs": 600000,
      "warmupRetries": 4,
      "enabled": true
    }
  ]
}
```

Declared servers always win. One with the same `id` as a preset replaces it outright — including with
`"enabled": false`, which is how a shipped server is turned off — and one covering a language a preset
also covers suppresses that preset. `"useBuiltIns": false` at the top level disables the whole shipped
set, leaving only what the file declares. Each extension is owned by the first enabled server in
stable id order.

### How sessions are run

Servers are pooled per workspace and language and stay alive between queries. The protocol splits work
into an expensive handshake — during which a server reads the project, restores packages, and builds
its own index — and cheap queries afterwards, so a process started per query pays the project load
every time and, for any real project, exceeds its own query budget before it can answer. The first
query therefore pays `initializeTimeoutMs`; every later one pays `timeoutMs`. A session is closed
after `idleShutdownMs` without use, and a server that has crashed is replaced by the next query.

Open documents are kept in step with the workspace: a file is opened once and re-sent when it has
changed on disk, so a query after an edit answers against the current text rather than the text the
server first read. `workspace/symbol` is retried up to `warmupRetries` times while a server that
indexes in the background is still returning nothing, because an empty result is otherwise
indistinguishable from "no such symbol".

Model-facing positions are normalized to one-based coordinates and workspace-relative paths, with at
most 100 locations and 200 symbols returned. Both protocol symbol shapes — the flat
`SymbolInformation` and the nested `DocumentSymbol` tree — are flattened into one list carrying their
containers.

`lsp` accepts a `symbol` name in place of `line` and `column`, resolving the position through the
declaration index. Without that, reaching any semantic operation required first knowing a file, a
line, and a column — which could only be obtained by searching, the step the semantic query was meant
to replace.

## MCP servers

External [Model Context Protocol](https://modelcontextprotocol.io/) servers contribute their tools to
the agent. Add them under **Settings → MCP servers**,
which carries ready-made entries for the two this harness is most often pointed at:

- **Exa web search** — `web_search_exa` and `web_fetch_exa` over HTTP. It answers without a key at a
  lower rate limit, so it works the moment you add it; paste a key to raise that. Optional keys are
  sent in Exa's documented `x-api-key` header.
- **Unity Editor** — the `unity mcp` CLI, which attaches over stdio to whichever editor already has
  the named project open. Point it at your project path and the agent reads and drives the live
  editor, not files on disk.

The model sees stable, server-qualified names such as `mcp__exa__web_search_exa` and
`mcp__unity__create_gameobject`, so prompts can identify a tool unambiguously. A public name is a
pure function of `(server name, raw tool name)`: connection order, a
re-sync, and other servers never rename a tool. A name that does not fit the provider's
64-character `[A-Za-z0-9_-]` budget is sanitized and truncated with a deterministic hash appended, so
two raw names can never collapse into one.

### Configuration and keys

The server list lives in `%USERPROFILE%\.suharness\configuration\mcp.json`, using the portable
`mcpServers` shape. Existing MCP configurations work here unchanged, including their
`args`/`arguments` and `enabled`/`disabled` spellings:

```json
{
  "mcpServers": {
    "exa": {
      "url": "https://mcp.exa.ai/mcp",
      "headers": { "x-api-key": "${apiKey}" }
    },
    "unity": {
      "transport": "stdio",
      "command": "unity",
      "arguments": ["mcp", "--project-path", "C:\\Projects\\Example Unity Project"],
      "requiresApproval": true
    }
  }
}
```

**No secret is ever written to that file.** An API key entered in settings is stored protected
alongside the model provider credentials and substituted at connect time wherever the configuration
places `${apiKey}` — in the URL, an argument, an environment variable, or a header. A stored key the
configuration never places explicitly is sent as an `Authorization: Bearer` header, which is what
most HTTP MCP servers expect. `${env:NAME}` expands from the environment, for a key you would rather
keep in your shell. Keys are write-only: the settings view reports whether one is stored and never
shows it back, and saving with the field blank keeps the stored key rather than clearing it.

Editing a server reconnects it, so a corrected endpoint or a newly pasted key takes effect on the
next turn rather than at the next restart.

| Field | Transport | Description |
| --- | --- | --- |
| `transport` | both | `stdio` or `http`. Inferred from the presence of `url` when omitted |
| `command`, `args`, `env`, `cwd` | stdio | The child process to launch |
| `url`, `headers` | http | The endpoint to connect to |
| `requiresApproval` | both | Put every one of this server's tool calls to you first |
| `toolCallTimeoutMs` | both | Deadline per call, default 60000 |
| `enabled` / `disabled` | both | Configure a server without connecting it |
| `failOnStartupError` | both | Fail harness startup when this server cannot be reached, default false |
| `reconnect` | both | `{ enabled, initialDelayMs, maxDelayMs, maxAttempts }`, default `{ true, 500, 30000, 10 }` |

### Approval

A server marked `requiresApproval` escalates each of its tool calls through the ordinary pre-tool
gate, so you approve or refuse it in the composer before it runs. The Unity preset ships with this on:
its tools change a live project, and the harness has no way to inspect what a remote tool will do.

### Reliability

A dropped connection is retried with exponential backoff under a per-outage attempt budget; the last
good tool set stays published while that happens, and is withdrawn once the budget is spent. A
connection that survives longer than `maxDelayMs` resets the budget, so an occasionally-crashing
server recovers indefinitely while a crash-looping one still stops. Server failures never end a turn:
an MCP call that fails or times out comes back to the model as a readable error result.

Startup waits up to 30 seconds for servers to connect and then continues without the ones that have
not; they keep connecting in the background. MCP tools are withheld in **Read Only** sessions, which
promise a fixed read-only catalog the harness cannot verify a remote tool against.

## Large tool catalogs

Unity's MCP server publishes **142 tools**, whose schemas cost roughly 20,000 tokens — paid on every
request of every turn, before the task itself is described. A small local model has spent most of its
context before it reads what you asked for.

A server publishing more than 40 tools therefore has its catalog narrowed to the capabilities the
turn plausibly needs. Tools are sorted into groups
(`terrain`, `rendering`, `hierarchy`, `scripting`, `runtime`, `testing`, …) by the vocabulary in
their names and descriptions, never by a hard-coded list, so a different server version regroups
itself instead of falling off a whitelist. The turn's own wording then selects groups, ranked by how
much of each group's vocabulary it uses, keeping the strongest few. "Add some trees to the terrain
and bake the navmesh" keeps 51 of Unity's 142 tools; "change the snow material shader colour" keeps
62 — different sets, both including the ones the task needs.

Every rule fails towards keeping tools rather than removing them, because each one is a keyword
heuristic and the cost of a wrong guess should fall on tokens rather than on capability:

- `core`, `hierarchy`, `scene`, and unclassified tools are kept for **every** task. Checking your own
  work and reaching the objects you are changing are not optional, whatever the wording says.
- Wording that matches nothing **widens** to a broad safe subset rather than narrowing, and says so.
- A filter that would empty a catalog is discarded, because that means the classifier is wrong about
  this server, not that the task needs no tools.
- A catalog at or below the threshold is never touched. Exa's two tools are never filtered because
  Unity's hundred-odd happen to be connected too — the decision is made per server.

Narrowing is only safe with a way out, so a narrowed turn also gets a **`request_capability`** tool.
The agent names the group it is missing and says what it could not do; the group is added on the next
turn in that session. It cannot apply to the turn that asked — the tool catalog handed to a model is
fixed for the duration of its request — so the tool says exactly that rather than implying the tools
just appeared.

## The tool policy pipeline

Every tool call passes through a
`tools/pre-execute` → dispatch → `tools/post-execute` → `tools/result` pipeline. Subscribers reach it
through `IAgentLifecycle`:

- **Before the call**, handlers return allow, deny, or ask. Decisions combine by strictness rather
  than by registration order, so a refusal cannot be undone by a handler that happened to register
  after it — registration order is never a policy priority mechanism.
- **Ask** puts the call to the human through the same question broker the `ask_user_question` tool
  uses, so approval appears in the composer's existing prompt rather than a second parallel surface.
  With no interactive human on the session it fails closed: an approval that cannot be presented is
  never a silent yes.
- **Around the call**, a tool with a declared deadline is stopped when it overruns and reports that
  as its result. Only tools whose work is bounded by the machine declare one; `ask_user_question`,
  `subagent`, `workflow`, and `job_output` legitimately wait, and `bash` enforces its own.
- **After the call**, handlers may accept the result, replace what the model is shown, or withhold it
  entirely and substitute feedback. Transformations compose, each handler seeing what the previous one
  produced. A block is final.
- **`ToolResult`** then observes the exact text the model was given, after every transformation. It is
  the audit point and cannot change anything.

A refused, escalated, or timed-out call always returns text rather than throwing. The model needs a
result for every call it made, and one it can read is what lets it choose a different approach.

## Turn budget and step boundaries

The function-calling loop is composed by the harness rather than left to framework defaults, so its
two policies are explicit. One turn spends at most the provider's configured number of model
round-trips on tool calls (**100 by default**). On the final round-trip the framework withholds the
tool definitions, which forces the model to answer in prose —
an answer that looks like any other. A turn that reaches the limit therefore records a notice saying
the last response was produced without tools, because otherwise a task abandoned at the budget is
indistinguishable from one that finished.

Step numbers count model round-trips and come from the loop itself rather than being inferred from
the shape of the response stream. A step is opened by each dispatched tool call — which guarantees
another request — so the final answer-producing request gets a step of its own even though no tool
runs during it.

## Recovering from a failed model request

A failed model request is classified into a provider-neutral code and routed through the recovery
loop inside the same turn:

- A **context overflow** is repaired. The transcript is condensed into a checkpoint without consulting
  the pressure threshold, and the request is sent again. Without this a session is stuck: every later
  turn replays the same oversized transcript and fails the same way. One repair is attempted per turn.
- A **transient failure** — connection, timeout, 5xx, rate limit, or an empty completion — is retried
  under the provider's policy with bounded exponential backoff and jitter. A provider's own
  `Retry-After` replaces local backoff when it fits under the ceiling.
- Anything else ends the turn with a message naming the setting that fixes it.

Nothing from a discarded attempt is persisted or shown, so a retry is a genuine repeat of the same
request rather than a continuation of a half-finished one. The client library's own hidden retries are
switched off, so the configured policy is the single retry authority and every retry is visible in the
transcript.

Each provider route carries its own policy, set on its entry in
`%USERPROFILE%\.suharness\configuration\models.json`:

```json
"retryPolicy": {
  "mode": "Normal",
  "maxRetries": 2,
  "retryableCodes": ["EmptyResponse", "RateLimit", "Server", "Timeout", "Transport"],
  "initialDelayMs": 500,
  "maxDelayMs": 10000,
  "jitterRatio": 0.1
}
```

Omitting it uses the defaults shown above. `"mode": "Always"` retries every
failure without an attempt limit until the turn succeeds or is stopped — the useful behaviour for a
local server whose refusals while it loads a model into VRAM are indistinguishable from permanent
ones.

## Stopping a turn

**Stop** cancels the turn in flight and persists what it actually did, including the tool calls it had
already made. A tool call changed the workspace whether or not the turn finished, so dropping it would
leave a transcript that disagrees with the files on disk and a model that redoes the work on the next
turn. A call the interrupt left unanswered is closed with an explicit aborted result, because a
provider rejects a conversation carrying a tool call with no matching result.

## Undoing a turn

The observation policy stops a blind overwrite, but it cannot help once a call has run. **Undo from
here**, on any of your messages, rolls the workspace back to the state it was in immediately before
that message and hides that message and everything after it from the conversation. **Redo** puts both
back.

Each turn is snapshotted twice — once before the first model call, once when it finishes or is
interrupted — into a Git object database the harness owns under `%USERPROFILE%\.suharness\snapshots`.
It is addressed with an explicit `--git-dir`/`--work-tree` pair, so **nothing is ever written inside
your workspace**: your index, branches, reflog, stash, and hooks are untouched, and a workspace that
is not a Git repository at all is covered just the same. Snapshots store bytes exactly as they sit on
disk — line-ending translation, clean/smudge filters, and any `.gitattributes` the workspace carries
are all overridden — so a restored file is the file you had.

A restore writes only the paths that actually differ. Rewinding three files in a 24,000-file
workspace touches three files, rather than resetting every modification time and making every open
editor prompt to reload.

The transcript is not rewritten. A revert appends an event naming the span it hides, and a redo
appends one cancelling that span, so the append-only log stays a complete record of what happened and
the next message you send lands outside every hidden span rather than disappearing into the open one.
The state being replaced is captured *before* the rollback and recorded as the redo point, so an undo
is never a one-way loss of the work it rolled back.

Snapshots follow the same rules as the rest of the tooling: `.gitignore` is honoured at every level,
so an ignored `secrets.env` or `build/` directory is never copied into the store and never touched by
a restore. Files over 2 MB are skipped when they first appear, and the turn's snapshot record says
which. Capture is best-effort: a workspace that cannot be captured leaves a turn uncovered and says so
in the transcript, rather than delaying or failing work you asked for in order to protect it.

**What undo does not cover.** Anything a shell command did beyond the filesystem — a package install,
a database migration, a request that already left the machine — is not reversible and the confirmation
says so. An ignored or oversized file is not captured, so it is not restored. And a turn interrupted
before its first snapshot landed cannot be rolled back.

### Indexing

The first capture in a workspace reads and stores every file in it. On a large tree that is minutes of
work where every later capture is a delta costing a few hundred milliseconds, so it runs in the
background from the moment a workspace is opened rather than inside the first turn you run. A
workspace small enough to index within five seconds is covered from its first turn; a larger one
reports the first turn or two as not yet coverable and is covered from then on. Measured against a
24,000-file workspace on Windows: 121 s to index once, then 180 ms to capture a turn and 570 ms to
roll one back.

### Configuration

Optional, at `%USERPROFILE%\.suharness\configuration\snapshots.json`. The defaults shown are what
applies without it, and unreadable configuration falls back to them rather than to *disabled* — a
safety feature should not be what a typo switches off.

```json
{
  "enabled": true,
  "maxFileBytes": 2097152,
  "captureTimeoutMs": 30000,
  "primeTimeoutMs": 900000,
  "exclude": ["node_modules/"]
}
```

`exclude` takes gitignore syntax and applies on top of the workspace's own `.gitignore` files.

## Breaking a tool loop

A model that calls the same tool with identical arguments over and over is not making progress. After
3, 5, and 8 consecutive identical calls the harness appends an escalating reminder to the tool result,
telling the model to read the last result and either change approach or conclude. It is advisory: no
call is ever delayed, refused, or rewritten, and the decision stays with the model. Calls are compared
on a canonical form of their arguments, so a reordered argument object is the same call;
`todo_write` is transparent to the chain so bookkeeping between repeats cannot launder a loop; and a
new turn starts a fresh chain. SuHarness has no post-result injection channel, so the reminder rides
the tool result inside a `<harness-reminder>` block.

## Extending SuHarness

SuHarness has no plugin SDK, and most extensions do not need one. Because SuHarness is open source
and already knows how to work against a source repository, the simplest way to extend it is to use
SuHarness itself: open this repository as the workspace and describe the change you want.

```
Add a new parameter to prompt execution called `priority`, supporting Low, Normal,
High and Critical. Update the scheduler, persistence model, UI and tests. First
inspect the existing architecture and produce a plan before modifying anything.
```

The agent decides where the functionality belongs, which modules change, whether a new abstraction is
warranted, and which tests and docs need updating. It can then build and validate its own work.

A few things make this practical rather than aspirational:

- **Plan mode** — start in plan mode so the architecture is inspected and a plan is agreed before any
  file is written. `exit_plan_mode` is the only way out.
- **Access modes** — `ReadOnly`, `WorkspaceWrite`, and `FullAccess` bound what a self-modifying
  session may do.
- **The observation policy** — a file must be read by the session before it can be edited or
  overwritten, and a file that changed since that read must be re-read. This is what stops the agent
  from discarding your concurrent edits.
- **Turn snapshots** — file changes are committed to a Git object database the harness owns, stored
  under the SuHarness home rather than in the workspace, so working on this repository never writes
  to its `.git`. Any turn can be undone.
- **`bash`** — runs `dotnet build` and `dotnet test`, so compilation failures and test regressions
  come back as ordinary tool results the agent can act on.

> **Rebuilding while running.** On Windows the running `Suharness.Web` process holds its own output
> assemblies open, so `dotnet build` fails with `MSB3027 … being used by another process`. Stop the
> running instance before rebuilding, or build a copy of the tree elsewhere.

This replaces extension *ceremony*, not engineering discipline: changes are still expected to respect
separation of concerns, testability, and backwards compatibility, and to add an interface only where
one earns its place.

### Where genuine extension points still exist

Some boundaries are legitimately external, and remain so:

- **MCP servers** — third-party capabilities configured and connected at runtime.
- **Skills** — reusable instructions discovered from `.suharness/skills` and `.agents/skills`.
- **Model providers** — OpenAI-compatible routes declared at runtime in the Web UI.
- **`IAgentFactory`** — to replace the agent implementation wholesale, reference
  `Suharness.Abstractions`, implement the interface, and register it in dependency injection in place
  of `ModelConfigurationService`.

## Configure models

Open the **No model configured** button or the composer model picker, then choose **Manage models**.

Each provider requires a stable lowercase ID, display name, OpenAI-compatible base URL, and at least one model. **Fetch available models** calls the draft endpoint's `GET /models` route using the key currently typed in the form. Discovered models, their advertised context lengths, and any pricing the endpoint publishes are added unchecked for review; **Select all** toggles the complete catalog, and only checked models are saved. Nothing is persisted until **Create provider** or **Apply** is selected.

### Model metadata and pricing

The model catalog carries `Context`, `Input $/1M`, `Output $/1M`, and `Cached in $/1M` alongside each
model ID. Every one of them is optional, and every one is editable. A model that reports nothing but
an ID — which is all a local server usually reports — is a valid model and can be selected and run;
only its cost estimates are unavailable.

Rates are stored as USD per million tokens whatever the upstream publishes. OpenRouter reports a
per-token rate as a decimal string, so `"0.0000025"` is imported as `2.50`; a listing that names its
own unit is believed instead of converted. Cached-input rates are read from `input_cache_read` and
the other names providers use for it. Where prices came from, and when they were fetched, is recorded
with them and shown on each field.

A blank field means unknown, and stays unknown: it is never treated as free. Only a rate someone
actually entered — including `0` — asserts a price. **Fetch available models** fills blanks and
nothing else, so re-fetching a catalog can add metadata that was missing but can never overwrite a
context window or rate that was configured by hand.

### Reasoning effort

A reasoning model is asked how hard to think through an **Effort** column on its catalog row —
`Minimal`, `Low`, `Medium`, or `High` — and the composer's model picker carries the same list under
**Reasoning effort**, where a choice overrides the catalog for the current session only. Picking a
different model drops the override, since a level chosen for a reasoning model means nothing to one
that does not reason.

The default in both places is **Default**, and it does not mean "medium": it sends no reasoning
parameter at all. That distinction is the reason the setting is per-model rather than per-route. One
provider — OpenRouter especially — serves reasoning and non-reasoning models side by side, and an
endpoint that has never heard of the parameter rejects the whole request rather than ignoring the
field, so "no opinion" and "medium" must not look the same on the wire.

The chosen level is sent as `reasoning_effort` on the OpenAI-compatible request. What the model then
spends is visible without any further configuration: reasoning tokens are already broken out in the
cost breakdown wherever the provider reports them.

A remote provider requires an API key: selecting a model on a non-loopback endpoint without one
fails with a message naming the provider rather than an opaque HTTP 401. Loopback providers such as
KoboldCpp are allowed to run unauthenticated.

API keys are write-only. Provider profiles are stored in `%USERPROFILE%\.suharness\configuration\models.json`; credentials are protected separately with ASP.NET Core Data Protection and never appear in model snapshots or the JSON settings file. Selecting a model updates the current session and the default for new sessions.

### KoboldCpp

For a local KoboldCpp server on port `5001`, add a custom provider with:

- Provider ID: `koboldcpp`
- Display name: `KoboldCpp Local`
- Base URL: `http://127.0.0.1:5001/v1`
- API protocol: `OpenAI Chat Completions`
- API key: leave blank for an unprotected local server

Choose **Fetch available models**, check KoboldCpp's exact loaded model ID, then create the provider and select that model in the composer. This was verified against KoboldCpp `1.117.1` and `koboldcpp/Qwen3.6-35B-A3B-MTP-UD-Q4_K_XL`.

Provider and transport failures are appended to the session as red error events. They do not escape the component event handler or disconnect the Blazor circuit, so credentials/endpoints can be corrected and the session retried without reloading the application.

## Token usage and cost

Every model request the harness makes is counted against the session that made it — the turn's own
requests, each round-trip of a tool loop, a retried attempt the provider still served, the auxiliary
request compaction sends, and a subagent's requests against the subagent's own session. Counting
happens beneath the function-calling loop, so a turn that called tools eleven times is recorded as
eleven requests rather than one, and a response that is reprocessed is never counted twice.

Usage is normalized into one internal record whatever the provider reports. Input, output, and total
are stored when all three come back; a missing total is derived from input and output; a response
carrying only a total leaves the split it never claimed unknown. Reasoning counts, cached-read
counts, cache-write counts, and a provider-reported request cost are captured when they are there and
left unset when they are not. **An unreported count is never stored as zero.** The harness has no
tokenizer it can trust for an arbitrary endpoint, so a provider that reports no usage yields
`Tokens: —` and "usage not reported by provider" rather than a guess presented as a measurement.

### Three levels of accounting

The harness counts at three levels, and keeps them apart because they answer different questions.

**Session** is the whole conversation. **Turn** starts at a user prompt and ends when the agent has
finished answering it. **Model call** is one request sent to the provider — and one turn contains many,
because the model asks for a tool, gets the result, reasons again, asks for another, and eventually
answers:

```
User turn → LLM call → tool → LLM call → tool → LLM call → final response
```

That is **one turn containing three model calls**, never three turns. The turn boundary comes from the
runtime, which is the only thing that knows where one turn ends and the next begins — nothing about
the request stream itself distinguishes a turn's last request from the next turn's first.

Every model call records what the provider reported, plus the turn it belongs to, its place in that
turn, the agent loop's step number, its own id, the provider's response id, the finish reason, and
the context window and output cap the model was configured with. The configuration is stored against
the call rather than looked up later, so a session that changed model mid-conversation does not have
its earlier requests measured against a window that never held them.

### Current context versus tokens processed

These are different numbers and the difference is the point:

- **Current context** is the prompt of the **latest** model call — everything actually sent for that
  request: system prompt, harness instructions, tool schemas, conversation history, previous assistant
  messages, tool calls and results, injected file context, checkpoints, and the user's message. It is
  what fills the window, and it is shown against it: `72.6K / 262K (27.7%)`, with
  `remaining = window − input` and `safe remaining = window − input − max output`.
- **Tokens processed** is the sum across calls. Every request replays the conversation, so a turn of
  four calls reprocesses most of its history four times. A session can process millions of tokens
  through a 262K window without ever coming close to filling it.

Cumulative input is never used to compute window utilisation. Cached input is likewise a **subset** of
the input count, not context on top of it: a request reported as 20K input of which 15K was cached
carried 20K, and 5K of that was new.

A compact indicator sits beside the composer — `◉ 72.6K / 262K` — and opens a breakdown with exact
figures for the current context, the current turn, and the session. Compact `K`/`M` formatting is used
in the control; the breakdown gives full counts.

### When the provider says nothing

Missing usage fields never fail a turn, and an unreported count is never stored as zero. Where a
provider reports no prompt count at all, the context indicator falls back to measuring the request
the harness actually sent — messages, instructions, and tool schemas — using the character-ratio
estimator compaction already uses. No tokenizer dependency was added for this. That figure is labelled
**estimated**, and it is deliberately held outside the usage record: it is never added to a turn or
session total and never priced. An estimate can tell a reader why a small local model has started
forgetting things; it must never masquerade as a measurement.

KoboldCpp's OpenAI-compatible API returns `prompt_tokens`, `completion_tokens`, and `total_tokens` but
no `prompt_tokens_details`, so its sessions show real input and output counts and cached usage as
unavailable — a dash rather than a zero, because the server never claimed zero.

A `$` control sits beside the token indicator in the composer. Cost is calculated only when that
control is hovered, focused, or opened, so nothing is priced until a reader asks. The popup shows one
of three things:

- **Actual provider cost**, when the provider returned what it billed. That figure is preferred over
  any estimate and is labelled as provider-reported.
- **Estimated session cost**, derived from the rates configured for the models the session used, with
  the per-component arithmetic shown and a disclaimer that provider routing, caching, service tiers,
  and pricing changes can move the real charge. Cached prompt tokens are priced once, at the cached
  rate, since the provider's input count already includes them.
- **Cost unavailable**, naming the model whose pricing is missing. `$0.00` is never shown to mean
  "unknown", and a local model is never assumed to be free.

Totals are kept as counters in memory and written to the session record at the end of each turn, so
streaming never writes per token and nothing re-reads a transcript to work out what a session spent.
Usage is tracked per model within a session, so a session that used a planner and a worker model, or
retried onto a second provider, prices each against its own rates. Each completed request also leaves
one `usage` line on the append-only transcript for audit, carrying its session, turn, call, timestamp,
model, provider, usage fields, and window configuration — the accounting, never a copy of the prompt.
Those lines are bookkeeping, so neither replay nor the transcript view shows them. The latest call is
also kept on the session record, which is what lets the context reading survive a restart; turn totals
are not, and correctly begin again at the next prompt. Sessions and usage lines recorded before any of
this existed load unchanged, with the fields they never had reading as unknown.

## Workspaces and sessions

Click the workspace row in the sidebar to choose a registered project, add a directory, or select **No workspace**. The last selection is restored when the app loads. No-workspace sessions behave as ordinary chat: they receive no local folder context or filesystem/shell tools, while connected remote MCP tools remain available. Canonical paths are registered idempotently and the newest workspace appears first. Removing a workspace unregisters it only; the directory and its session transcripts remain untouched.

Sessions are scoped to the selected workspace or to the separate no-workspace chat context. **New session** reuses that context's blank session until its first prompt starts a turn, then creates another blank session. Use the actions menu beside a session in the sidebar to rename or archive it. Archive hides the session from workspace lists but preserves its metadata and append-only transcript on disk.

The composer access menu is stored per workspace session. **Read** exposes only read, glob, and grep;
**Write** adds workspace-scoped write, edit, and bash tools; **Execute** also permits
absolute tool paths outside the selected workspace. Changing modes takes effect on the next request.

Responses stream into the conversation as they arrive. While a turn is running, **Send** becomes
**Stop**; stopping preserves any text already received and records the interrupted turn. A session
runs only one turn at a time, and its complete model-visible history, including assistant tool calls
and tool results, is reconstructed from the append-only log on later turns.

In the composer, **Enter** submits, **Shift+Enter** inserts a newline, and **Arrow Up/Down** navigates
the current session's earlier prompts while preserving an unsent draft.

Workspace records and display order are separate atomic documents under `%USERPROFILE%\.suharness\configuration`. A write-ahead mutation journal makes cross-document changes recoverable: incomplete creates roll back, pending deletes finish, fully committed operations with an uncleared marker are finalized, and stale temporary files are discarded at startup.

## Contributing

Build, test, and formatting instructions, the project layout, and a worked example of adding a
tool are in [CONTRIBUTING.md](CONTRIBUTING.md). Please read [SECURITY.md](SECURITY.md) first —
several decisions in the code only make sense against the threat model it describes. Participation
is governed by the [Code of Conduct](CODE_OF_CONDUCT.md), and notable changes are recorded in
[CHANGELOG.md](CHANGELOG.md).

Security vulnerabilities should never be filed as public issues; report them privately through
[GitHub's advisory form](https://github.com/suhasaraos/suharness-public/security/advisories/new).

## License

SuHarness is licensed under the [Apache License, Version 2.0](LICENSE).

Third-party dependency licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Every package in the dependency closure is
permissive — MIT, Apache-2.0, BSD-2-Clause, or BSD-3-Clause — and no copyleft or source-available
code is included. No third-party source is vendored into this repository.

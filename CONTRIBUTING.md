# Contributing to SuHarness

Thanks for taking an interest. This file covers what you need to build the
project, what the review will look for, and where things live.

Before anything else, please read [SECURITY.md](SECURITY.md). SuHarness runs a
shell tool with your privileges and has no authentication in front of it. That
shapes what counts as an acceptable change, and it is the reason some choices in
the code look more paranoid than the size of the project would suggest.

## Prerequisites

- **.NET 10 SDK.** [`global.json`](global.json) pins the feature band; run
  `dotnet --version` and install a matching SDK if the build complains.
- **Git.**
- **`bash`** on `PATH`. On Windows, Git for Windows supplies it. Without it the
  `bash` tool is unavailable and some tests are skipped.

No model, API key, or provider is needed to build or to run the test suite. The
repository deliberately ships no endpoint or credential.

## Build, test, format

```bash
dotnet restore Suharness.slnx
dotnet build   Suharness.slnx
dotnet test    tests/Suharness.Core.Tests/Suharness.Core.Tests.csproj
```

Run the UI:

```bash
dotnet run --project src/Suharness.Cli -- web
```

It serves `http://127.0.0.1:3090`. `--port` changes the port; `--host` accepts
only loopback names, on purpose.

### Formatting is enforced

Formatting comes from [`.editorconfig`](.editorconfig). **C# and Razor are
indented with tabs.** Before pushing:

```bash
dotnet format Suharness.slnx
```

CI runs `dotnet format --verify-no-changes` and fails the build on any
difference, so a formatting-only disagreement is caught before review rather
than during it.

Line endings are normalised by [`.gitattributes`](.gitattributes): LF in the
repository for everything except `.bat`/`.cmd`, which need CRLF. You should
never see a pull request that is mostly line-ending churn; if you do, something
is misconfigured — say so rather than committing it.

### Warnings

The tree builds with **zero compiler warnings**. Please keep it that way. If a
warning is genuinely wrong, suppress it narrowly with a comment explaining why,
rather than widening `NoWarn` for a whole project.

## Project layout

Dependencies point in one direction only. Please do not add an edge that
reverses this.

```
Suharness.Abstractions   contracts: sessions, tools, persistence, orchestration
        ▲
        ├── Suharness.Core            the model-neutral session loop and turn orchestration
        ├── Suharness.Tools           the workspace-scoped tools the agent calls
        │        ▲
        │        └── Suharness.Infrastructure   JSONL persistence, snapshots, MCP, model config
        │                     ▲
        │                     └── Suharness.Web          the Blazor Server UI
        │                                  ▲
        │                                  └── Suharness.Cli   `suharness web`, loopback policy
        └── tests/Suharness.Core.Tests
```

- **Abstractions** holds contracts and nothing else. No I/O, no policy.
- **Core** owns the turn loop. It must stay model-neutral: nothing here should
  know which provider is in use.
- **Tools** implements what the model can call. Every path goes through
  `WorkspaceScope`; do not reach the filesystem around it.
- **Infrastructure** is where I/O lives — files, processes, HTTP, MCP.
- **Web** is presentation. Business rules do not belong in a `.razor` file.
- **Cli** owns the binding policy and nothing else.

## Adding a tool

This is the most common contribution, so here it is end to end. A tool is an
ordinary method; `Microsoft.Extensions.AI` turns it into a model-callable
function from its signature and attributes.

1. **Write the method** in `src/Suharness.Tools/`, taking `WorkspaceScope` for
   any path argument:

   ```csharp
   public static async Task<string> ExecuteAsync(
       WorkspaceScope scope,
       string path,
       CancellationToken cancellationToken)
   {
       // Resolve refuses anything that escapes the workspace.
       var resolved = scope.Resolve(path, nameof(path));
       ...
   }
   ```

2. **Register it** in `SessionToolset.BuildTools`, next to the tools it belongs
   with, and give it a description written for the model rather than for a
   developer. Say what the tool does, what the result looks like, and when not
   to use it.

3. **Respect the access mode.** A tool that writes must appear only when the
   session's `HarnessAccessMode` allows writing.

4. **Return text the model can act on.** Failures should come back as a
   `ToolInputException` with a message aimed at the model, not as an exception
   that escapes the pipeline.

5. **Test it** in `tests/Suharness.Core.Tests/ToolsTests.cs`, including at least
   one case where the path escapes the workspace.

Tool names, parameter names, descriptions, and result formatting are a
**model-facing contract**. Changing them changes behaviour for every existing
session, so treat a rename as a breaking change and say so in the pull request.

## Tests

`tests/Suharness.Core.Tests` holds the suite. Tests are xUnit and run without
network access or an API key; anything needing either is marked `Skip` (see
`McpLiveTests`) and is not part of CI.

New behaviour needs a test. Bug fixes need a test that fails before the fix. For
anything touching sanitisation, workspace scoping, credential storage, or the
approval gate, please include the adversarial case, not just the happy path —
those four are the places where a plausible-looking change quietly removes a
protection.

## Commits and pull requests

- Branch off `main`.
- Keep a pull request to one concern. A refactor bundled with a fix is hard to
  review and hard to revert.
- Write commit subjects in the imperative — "Add spill preview to grep results"
  — and use the body to explain *why*, since the diff already shows *what*.
- Note behaviour changes explicitly, especially anything model-facing.
- Update [`CHANGELOG.md`](CHANGELOG.md) under `## [Unreleased]` for anything a
  user would notice.
- CI must be green: build, tests, and formatting.

## Code style beyond formatting

The existing code has a house style worth matching:

- **Comments explain why, not what.** The tree is unusually well commented in
  this respect. A comment restating the code will be asked about in review; one
  explaining a non-obvious constraint is welcome.
- **XML doc comments on public types and members**, particularly anything that
  is part of a contract.
- **File-scoped namespaces**, `var` where the type is apparent, expression
  bodies for one-liners, pattern matching over type checks.
- **Nullable reference types are enabled everywhere.** Do not silence a nullable
  warning with `!` unless you can explain why it is safe in a comment.
- Prefer a small named method over a comment introducing a block.

## Reporting bugs

Open an issue with what you expected, what happened, the OS and .NET version,
and — if a model was involved — the provider and model id. A transcript excerpt
helps enormously, but **scrub it first**: transcripts can contain file contents
and paths from your workspace.

Security problems do not go in issues. See [SECURITY.md](SECURITY.md).

## Licence

SuHarness is Apache 2.0. Contributions are accepted under the same licence, per
section 5 of [LICENSE](LICENSE) — no separate CLA. If you add a third-party
dependency, update [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) in the same
pull request and confirm the licence is permissive; the project takes no
copyleft or source-available dependencies.

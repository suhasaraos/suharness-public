# Security Policy

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Report it privately through GitHub's
[private vulnerability reporting](https://github.com/suhasaraos/suharness-public/security/advisories/new)
form, which opens a draft advisory visible only to the maintainers.

Please include:

- what an attacker can do, not only what the code does;
- the shortest reproduction you have — a markdown snippet, a request, a tool call;
- the commit or release you tested against;
- your platform, since parts of the credential store behave differently on
  Windows than on Linux and macOS.

You should get an acknowledgement within **7 days** and an assessment within
**30 days**. SuHarness is maintained by one person in their spare time, so fixes
are best-effort rather than contractual. If a report is accepted you will be
credited in the advisory and the changelog unless you ask otherwise.

## Supported versions

SuHarness is pre-1.0. Only the tip of `main` receives fixes. There are no
backports to earlier tags.

---

## The threat model

Read this before deploying SuHarness anywhere. Several deliberate design
decisions are safe *only* under the assumptions below, and are dangerous
outside them.

### SuHarness is a single-user tool bound to loopback

**SuHarness has no authentication, no authorization, and no notion of users.**
Anyone who can open its port has the full capability of the running process.

That is intentional, and it is only defensible because of how it is meant to
run: as a local developer tool on a workstation, bound to the loopback
interface, driven by the one person who started it. It occupies the same
position as an editor or a shell — programs that also do not authenticate you
before running your code.

`suharness web` enforces this. `--host` accepts only `127.0.0.1` or `localhost`
and refuses anything else, and the accepted `Host` header is restricted to those
names so a hostname pointed at loopback by someone else's DNS cannot reach the
UI from a page you happen to be visiting.

**The dangerous part is that this is easy to undo.** `Suharness.Web` is an
ordinary ASP.NET Core application, and running it directly with a different
`--urls` bypasses the CLI's binding policy entirely. Do not do this. In
particular, never:

- bind SuHarness to `0.0.0.0`, a LAN address, or a public interface;
- put it behind a reverse proxy or tunnel that is reachable by anyone else;
- run it on a shared or multi-user machine where another account can reach the
  port;
- expose it to a network you do not fully control, even briefly, even "just to
  test something".

If you need any of that, you need authentication in front of it first. Adding
it is a real project, not a configuration flag.

### The agent runs code with your privileges

The `bash` tool runs arbitrary commands through `bash -c` as the user who
started the process. There is no container, no jail, no syscall filter, and no
allow list of permitted commands. Anything you can do from a terminal, the
model can do.

The workspace access mode narrows *which paths the file tools accept*, and the
approval gate can put individual calls to you before they run. Neither is a
sandbox. In particular, `bash` receives a working directory but not a
confinement: a command is free to leave it. Treat "the agent has a workspace" as
an organising convention, not a security boundary.

If you need real isolation, run SuHarness inside a VM or container that you are
willing to lose, and give it credentials scoped to what that throwaway
environment should reach.

### Anything the agent reads can try to instruct it

SuHarness loads `AGENTS.md` and `CLAUDE.md` from the workspace, skills from
`.suharness/skills` and `.agents/skills`, and tool results from MCP servers.
All of that becomes model context. A repository you cloned, a file a tool
returned, or a server you connected can therefore contain text written to
redirect the agent — the class of problem usually called prompt injection.

There is no general defence against this, here or anywhere else. What SuHarness
provides is the ability to see and interrupt what happens:

- an MCP server can be marked **requires approval**, which routes every one of
  its tool calls to you first;
- a turn's file changes are snapshotted so a turn can be reverted;
- transcripts are append-only, so what a tool actually returned stays inspectable.

**Opening an untrusted repository as a workspace is equivalent to running its
code.** Decide accordingly.

### Rendered model output is treated as hostile

Assistant text, tool results, and MCP output are rendered as Markdown in the
browser UI. Because none of it is authored by SuHarness, it is sanitised on the
way out:

- raw HTML is escaped rather than rendered, so injected markup becomes text;
- link and image destinations are restricted to an allow list of schemes, so a
  `javascript:` URL cannot become a live link;
- a strict Content Security Policy backs both, with a per-response nonce for the
  single inline script the host page needs, so `script-src` never falls back to
  `'unsafe-inline'`.

See `src/Suharness.Web/Components/SafeMarkdown.cs` and
`src/Suharness.Web/SecurityHeaders.cs`.

### Where credentials live, and how well they are protected

Model provider API keys and MCP server secrets are stored under
`%USERPROFILE%\.suharness\configuration\credentials.bin` (or `$SUHARNESS_HOME`),
encrypted with ASP.NET Core Data Protection. They are write-only in the UI: once
saved, a key is never sent back to the browser, and configuration files never
contain it in plaintext.

**How strongly that file is protected depends on your operating system, and the
difference is significant:**

| Platform | Key ring protection | What that means |
| --- | --- | --- |
| **Windows** | DPAPI, tied to your Windows account | The key ring is encrypted at rest. Copying `.suharness` to another machine or another account does not yield the credentials. |
| **Linux, macOS** | **None — the key ring is stored in plaintext** | `~/.suharness/data-protection/*.xml` holds the unwrapped key beside the ciphertext it decrypts. Anyone who can read those files can recover every stored API key. |

This is a consequence of `ProtectKeysWithDpapi()` having no cross-platform
equivalent; ASP.NET Core does not ship one, and SuHarness does not yet integrate
a platform keychain.

**On Linux and macOS, therefore:**

- treat `~/.suharness` as being as sensitive as `~/.ssh`;
- confirm it is not world-readable — `chmod 700 ~/.suharness`;
- never copy it to shared storage, and never include it in a backup you would
  not trust with plaintext keys;
- prefer provider keys that are scoped and cheap to rotate;
- exclude it from any dotfile repository.

Contributions integrating `libsecret` or macOS Keychain are welcome.

### Never commit a runtime home

`$SUHARNESS_HOME` (default `~/.suharness`) contains credentials, the Data
Protection key ring, and complete session transcripts. `.gitignore` excludes
`.suharness/` and `.e2e-*/` for this reason. If you point `SUHARNESS_HOME`
somewhere else, exclude that path too.

## What is out of scope

The following are known and intended, and reports about them will be closed as
working-as-designed:

- **No authentication on the web UI.** See above; this is the design.
- **The agent can run arbitrary commands.** That is the product.
- **The agent can be steered by workspace content.** Inherent to the category.
- **Plaintext Data Protection keys on Linux and macOS.** Documented above; a
  fix requires platform keychain integration, and a pull request is more useful
  than a report.

Reports that *are* in scope include anything that breaks an assumption stated
above: a way to reach the UI from another origin, a sanitiser bypass that lands
executable content in the page, a path that escapes the workspace scope in the
file tools, a credential leaking into a transcript or log, or an approval gate
that can be bypassed.

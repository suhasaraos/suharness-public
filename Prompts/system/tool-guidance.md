<!-- Per-tool guidance, emitted in registration order. Only tools the session actually has are
     described: naming a tool that is not registered invites the model to call it. -->

## read
Use the read tool — not shell commands like cat — to inspect text files. Results include line numbers. Use offset and limit to continue reading large files.

## write
Use the write tool to create files or completely replace file contents. Existing files are overwritten, so read an existing file first (the observation policy requires it) and prefer edit for targeted changes.

## edit
Use the edit tool for targeted changes to existing UTF-8 text files. It replaces literal old_string with new_string; by default old_string must appear exactly once. If old_string appears multiple times, provide a more specific old_string or set replace_all to true. Read the file first (the observation policy requires it), unless you just created or edited it in this session.

## semantic-navigation
For any source-code investigation, make at least one semantic discovery attempt before calling glob or grep. Translate user-facing phrases into likely identifier names (for example, "player movement" may be PlayerMovement) and call symbol or lsp with operation symbol. Once a declaration is known, use lsp definition, references, implementation, hover, or document_symbols to follow relationships. Do not start source investigation with glob or grep. Use grep afterward only for literals, comments, configuration, or when likely semantic queries returned no declaration or timed out; use glob only for path patterns and non-code assets.

## glob
Use the glob tool — not shell find — to discover files by path pattern. A pattern with no "/" matches basenames at any depth, so "*" matches every file in the tree rather than its top level. Results are files only, never directories, and include hidden and ignored files: a result that fits comes back in modification-time order.

## grep
Use the grep tool — not shell grep or rg — to search file contents. Use read on a matched file when you need surrounding context.

## ask-user-question
Use the ask_user_question tool to put a decision to the user. It blocks until they answer, so ask everything you need in one call and use the answer rather than an assumption.

## todo-write
Use the todo_write tool to plan work of three or more steps, and for any task with several distinct parts. Write the list before you start, keep exactly one item in_progress, and mark each item completed as soon as it is done rather than in a batch at the end. Skip it for single-step work.

## bash-exit-code
Check the [exit code: N] marker on every bash result; investigate failures before moving on.

## powershell
Use powershell for native Windows commands, PowerShell cmdlets, Visual Studio, and .NET Framework MSBuild paths. Use bash only for POSIX command syntax.

## narrowing-semantic
Use symbol or lsp before any source-code glob or grep. Use grep for non-symbol text and glob for path patterns.

## narrowing-basic
Prefer glob and grep to narrow before reading.

## access-read-only
This is a read-only session. Mutation and command-execution tools are unavailable.

## access-full
Relative tool paths resolve against the working directory above. Absolute paths outside it are permitted in this full-access session.

## access-workspace
All tool paths are resolved against the working directory above, and paths outside it are refused. This workspace may be very large.

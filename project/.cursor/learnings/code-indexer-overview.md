---
description: What the code indexer (the `codeindex` CLI) is, when to use it instead of grep or opening files for .NET/C# SDK code, that it is an installed CLI tool to run (never to read or build), where its index should live, and how to keep it fresh. Read when asked about the code indexer or codeindex, or about finding where something is defined, used, called, or implemented in .NET code.
type: reference
---

# The code indexer (`codeindex`)

## What it is

`codeindex` is a command-line tool that reads a **.NET solution with Roslyn** (the C# compiler) and writes a local SQLite
index of it. You then ask it questions and it answers the way the compiler would, not the way a text search would:

- **Where is this defined?** `definition`, `at`
- **What uses it?** `refs` (every call, read, write, type use, `new`, `typeof`, `nameof`, attribute)
- **Who calls it / what does it call?** `callers`, `callees` (call hierarchy, with call sites)
- **What implements this interface or overrides this method?** `implementations`
- **What does this type derive from, and what derives from it?** `hierarchy`
- **What is in this file or type?** `outline`

It tells overloads apart, understands partial classes, and can follow a call made through an interface to the
implementations that may run (`--through-dispatch`). It also records calls into libraries (`HttpClient`, Google.Apis,
gRPC), which is exactly what Frank needs to list an SDK's remote operations.

## When to use it

- **Use it** for **C#/.NET** SDK code whenever the question is "where / who / what implements / what derives". It replaces
  grepping for a name and reading the hits one by one. It is exact where grep guesses, and a lot smaller to read.
- **Don't use it** for Python, Go, Java, or other languages (it only understands C#), for string literals, environment
  variables, config files, or JSON (it indexes symbols, not text), or for a solution that is not restored (see
  `code-indexer-troubleshooting.md`).
- It **finds the lines; you still read them.** Use it to know which files and lines matter, then open only those.

## It is an installed CLI tool

`codeindex` is installed on this machine as a .NET global tool (version 0.1.0 when checked on 2026-10-25), so **assume it
is there and just run it**; you don't need to check first, and you don't need to know or touch how it is built. Treat it as
a black box, like `git`. You never read or edit its source. Use it for .NET/C# code reviews even when the user didn't
mention it. If a command ever reports "not found", that is the one time to check:

```powershell
codeindex --version        # prints something like 0.1.0+<commit>
```

If that fails, say so plainly and offer the install below (don't silently fall back to grep without telling the user).
It needs the **.NET 10 SDK** to run, plus whatever SDK the solution being indexed pins in its `global.json` (the Google SDK
fork pins 8.0.426, which is installed on this machine).

```powershell
cd E:\Dev\demos\integration-demo\project\code-indexer
dotnet pack CodeIndexer.Cli -c Release                                   # writes artifacts\CodeIndexer.Cli.0.1.0.nupkg
dotnet tool install --global CodeIndexer.Cli --add-source .\artifacts    # update later with `dotnet tool update`
```

Ask before installing anything globally. (Only the user or a maintainer needs the rest of this section; the tool's source, README, plan, and JSON output contract are in this repo:
`project\code-indexer\README.md`, `project\docs\code-indexer-development-plan.md`,
`project\docs\code-indexer-output-contract.md`.)

## Where the index lives (do this, it matters)

Indexing **never modifies the SDK's source**, but by default it writes the index into a `.codeindex` folder *next to the
solution*, which would put an untracked folder inside the user's SDK checkout. Instead always give `--db` a path
**outside the SDK repo**, for example a `.codeindex` folder beside it:

```powershell
codeindex index E:\Dev\demos\g-api\google-cloud-dotnet\apis\Google.Cloud.Storage.V1\Google.Cloud.Storage.V1.sln `
  --db E:\Dev\demos\g-api\.codeindex\storage-v1.db
```

Any command run from a folder **under** `E:\Dev\demos\g-api\` then finds `E:\Dev\demos\g-api\.codeindex\catalog.db`
automatically, so you can drop `--db` and use `--solution Google.Cloud.Storage.V1` (or nothing at all when only one solution
is indexed). `codeindex list` shows what is indexed. Index files (`.codeindex\`, `*.db`) are git-ignored in this repo; never
commit one.

## Keep it fresh

- `codeindex index <solution> --db ...` is safe to run any time. If nothing changed it says so in about a quarter of a
  second; otherwise it redoes only the projects that changed and the ones that depend on them.
- **Every answer carries `"stale": true/false`.** If it is `true`, files changed since the index was built: re-run `index`
  before relying on the answer, and mention it to the user if you couldn't.
- Run `index` at the start of a task on a solution you have indexed before; it costs almost nothing when nothing changed.

## If the user's editor has it as an MCP server

`codeindex mcp` serves the same commands as MCP tools (`find_symbol`, `definition`, `refs`, `callers`, `callees`,
`implementations`, `hierarchy`, `outline`, `at`, `search`, `show`, `index_info`). If those tools are available in the session,
use them; the arguments mirror the CLI options (see `code-indexer-cli-reference.md`). Otherwise use the CLI. Don't set the MCP
server up unasked.

## Related learnings

- `code-indexer-cli-reference.md`: every command, with PowerShell examples, and how to read the output.
- `code-indexer-sdk-review-recipes.md`: how to find an SDK's remote operations and mock seams with it.
- `code-indexer-troubleshooting.md`: restore, stale, ambiguous, empty, or slow results.

Never put real secrets in this file.

---
description: `codeindex` command reference with PowerShell examples: index, find-symbol, search, definition, show, outline, at, refs, callers, callees, implementations, hierarchy; how to name a symbol (name, --id, --key, file:line:col), what "candidates" means, and how to read the JSON or table output. Read before running any codeindex command.
type: reference
---

# `codeindex` command reference

`codeindex <command> --help` is accurate and short; this is the working summary. Examples are PowerShell. Assume the index
lives outside the SDK repo (see `code-indexer-overview.md`); add `--db <path>` or `--solution <name>` unless exactly one
solution is indexed or you are under the folder that holds `.codeindex\`.

## Build or update the index

```powershell
codeindex index <solution.sln> --db <path>.db     # first time: builds it. Later: updates only what changed.
codeindex index <solution.sln> --db <path>.db --dry-run   # shows what it would redo and why, writes nothing
codeindex index <solution.sln> --db <path>.db --full      # rebuild from scratch
codeindex index <solution.sln> --db <path>.db --include "Google.Cloud.Storage.V1.Tests"   # tests are skipped by default
codeindex info --db <path>.db                     # projects, counts, stale?, and any indexing warnings/errors
codeindex list                                    # which solutions are indexed
```

- **Precondition: the solution must already be restored** (`dotnet restore`). The tool never restores or builds. If it
  prints `not restored` or reports compile errors, results are incomplete: see `code-indexer-troubleshooting.md`.
- By default it skips projects named `*.Tests`, `*.Snippets`, `*.GeneratedSnippets`, `*.Benchmarks`, `*.BenchmarkingTool`, `*.CleanTestData`.
- Typical time: 10 to 25 seconds to build an SDK package's index; a no-change re-run is about a quarter of a second.

## Name the symbol you mean

Every command about **one symbol** accepts the same selector. From least to most precise:

| Selector | Example |
|---|---|
| name | `Add`, `Calculator.Add`, `StorageClientImpl.UploadObject`, `Repository<T>` |
| position (the symbol *used* there) | `src\Foo.cs:84:21` (line and column are 1-based; the path may be any unique suffix) |
| documentation id | `M:Ns.Type.Method(System.Int32)` |
| `--key` / `--id` from an earlier result | `--id 651` |

**If a name matches several symbols you get `candidates`, not a guess.** The result has no `target`, `data` is empty, and
`candidates` lists each match with its `id`, `signature` and file. Pick one and repeat with `--id <n>`. `--kind class|method|
property|...` narrows a name. The usual flow is: `find-symbol` first (it returns ids), then use `--id` everywhere else.

## Find and look up

```powershell
codeindex find-symbol OrderService --kind class        # by name: exact, then prefix, then substring; returns ids
codeindex search "retry backoff"                       # full-text over names, signatures, XML docs (for things you can't name)
codeindex definition --id 651                          # every declaration (a partial class lists one per part)
codeindex show --id 651 --source                       # signature, docs, and the source of the declaration
codeindex outline src\Orders\Checkout.cs               # a file's symbols nested by container; or a type: outline OrderService
codeindex at src\Orders\Checkout.cs:84:21              # what identifier is at this position, and how it is used
```

Hidden unless you ask: generated code (`--include-generated`) and library symbols such as `HttpClient` (`--include-external`).
Page with `--limit` and `--offset`; the result says `total_matches` and `truncated`.

## What uses it, what calls it, what implements it

```powershell
codeindex refs --id 651                                # every use, with kind and the member that contains it
codeindex refs --id 651 --ref-kind call,create         # filter by kind: call, read, write, type-use, create, inherit,
                                                       #   implement, attribute, typeof, nameof, use
codeindex refs --id 651 --group-by member --context    # grouped, with the source line of each use
codeindex callers --id 651 --depth 2                   # who calls it, two levels up (--depth 0 = all the way up)
codeindex callers --id 651 --through-dispatch          # also calls made via an interface/abstract member it implements/overrides
codeindex callees --id 651 --include-external          # what it calls, including library methods (hidden by default)
codeindex implementations --id 42 --transitive        # implementers of an interface (or overriders of a method), and theirs
codeindex hierarchy --id 42 --direction down           # up = base types, down = derived/implementing types, both (default)
```

- `callers`/`callees` give **call sites** (`file_path`, `line`, `column`): cite these in your report.
- **Calls through an interface**: `callers X` finds calls that *name* `X`. A call made as `IShape.Area()` is not a call
  to `Circle.Area` unless you add `--through-dispatch`. For "what could end up running", use it.
- A property or field has **no callers**; its accesses are `refs` (`--ref-kind read,write`). To find who creates a type use
  `callers <type> --include-creates` or `refs <type> --ref-kind create`.
- Generated members are included in the call graph and flagged `is_generated`; add `--exclude-generated` to leave them out.

## Reading the output

Default output is compact **JSON**; add `--format table` for a readable summary (it still shows ids). Use the table when you are
scanning, JSON when you need an exact field. Fields every result has:

| Field | Meaning |
|---|---|
| `target` | The one symbol the selector resolved to |
| `candidates` | Present instead of `target` when the name was ambiguous |
| `data` | The answer (a list, or an object for `show`, `definition`, `hierarchy`, `at`, `refs`) |
| `stale` / `stale_files` | `true` means files changed since indexing: re-run `index` |
| `truncated`, `total_matches`, `limit` | Whether more exist than were returned |
| `warnings` | Things worth reading: ambiguity, generated references hidden (with a count), compile errors, library symbols with no source |

Symbols carry `id` (valid until that project is re-indexed), `key` (stable forever), `kind`, `full_name`, `signature`,
`file_path`, `start_line`, `end_line`, `is_generated`, `is_external` (declared in a library, so no location). Full field list:
`project\docs\code-indexer-output-contract.md`.

## Exit codes and errors

`0` ok (including "not found" and "ambiguous", which are normal answers with a warning). `2` no index database found.
`3` the index is from another version of the tool: re-run `index`. Problems go to stderr; stdout stays clean for parsing.

Never put real secrets in this file.

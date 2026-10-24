---
description: Fixes for `codeindex` problems: no index found, "several solutions", index from an older version, stale results, not restored or compile errors, ambiguous/candidates, empty results, missing library or generated symbols, a file or test project not indexed, SDK pin not found, slow indexing. Read when a codeindex command errors, returns nothing, or returns something surprising.
type: reference
---

# `codeindex` troubleshooting

| Symptom | Cause and fix |
|---|---|
| `No index database found` (exit 2) | Nothing indexed yet, or you are outside the folder tree that holds `.codeindex\`. Run `codeindex index <sln> --db <path>`, or pass `--db <path>`. |
| `Several solutions are indexed (...)` | Pass `--solution <name>` (names from `codeindex list`) or `--db <path>`. |
| `This index was built by an older version of the tool` (exit 3) | The tool was updated. Re-run `codeindex index <sln> --db <path>`; it rebuilds. |
| `"stale": true` in a result | Files changed (or were added, deleted, renamed) since indexing; `stale_files` names some. Re-run `index`: it is fast and only redoes what changed. Tell the user if you answered from a stale index. |
| `Project '...' not restored` / `has N compile error(s)` | The solution was not restored, or a reference or generated file is missing, so names could not be resolved and references through them are absent. Tell the user; **ask before running `dotnet restore`** in their SDK checkout (it writes `obj\` and may download packages). Then re-run `index`. Treat results for that project as incomplete until `info` shows no errors. A handful of errors in one file (the Storage package has 3) is minor; thousands means a missing restore. |
| `... requires .NET SDK x (rollForward ...) but the installed SDKs are ...` | The solution's `global.json` pins an SDK that is not installed. Say which one is needed and ask the user; don't edit their `global.json`. |
| Result has `candidates` and no `target` | The name is ambiguous (overloads, same name in several types). Pick one by `--id` and repeat. Narrow with `--kind` or `Type.Member`. |
| `Symbol not found: X` | Check spelling and try `find-symbol X` (substring match) or `search`. It may be generated (`--include-generated`), a library symbol (`--include-external`), in an excluded project, or under an `#if` for a target framework that was not indexed. |
| `callers` of a property or field is empty | Property/field accesses are references, not calls: use `refs --ref-kind read,write`. For "who creates this type" use `callers <type> --include-creates`. |
| `callers X` misses a caller you know exists | The caller probably calls an interface or base member. Add `--through-dispatch`. |
| `refs` says N references in generated files were excluded | Add `--include-generated` if you want them (for example, the generated gRPC stubs). |
| `File not indexed` from `at`/`outline` | The file's project is not in the index: it was excluded by default (tests, snippets, benchmarks) or is a different solution. Re-run `index` with `--include <ProjectName>`, or index the right solution. |
| A library method (`HttpClient.GetAsync`) can't be found | Library symbols exist only if the indexed code uses them. Add `--include-external`. If it still isn't there, the code doesn't call it. |
| Results look wrong for code under `#if NET462` | Only one target framework per project is indexed (for example `netstandard2.0`, or the newest `net8.0`). That code isn't in the index; read it directly. |
| A `.vb` or other-language project | Only C# is indexed. VB projects get a warning and are skipped. |
| Indexing takes 10 to 25 seconds | Normal for the first run (MSBuild loads the solution). Re-runs with no change take a quarter of a second; after an edit about 10 seconds. |
| Index files appear in the SDK repo | Someone omitted `--db`. Delete the `.codeindex\` folder it created in the SDK repo (ask first), and use `--db` outside the repo. |

## When to stop trusting the index

If `info` shows compile errors for the project you care about, the index is stale and you can't refresh it, or the answer
contradicts what you read in the source, **read the source and say so**. The tool locates code; the code is the authority.

## If the tool itself misbehaves

Say what you ran and what it printed, then fall back to reading the code, and tell the user it was a fallback. The tool's
tests and plan are in `project\code-indexer` and `project\docs\code-indexer-development-plan.md` (known limits are listed there).

Never put real secrets in this file.

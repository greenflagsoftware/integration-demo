# CodeIndexer

Semantic code navigation for .NET, built for AI agents and useful to people. It indexes a solution with Roslyn into a local
SQLite file, then answers the questions you would otherwise use `grep` for, but exactly:

- **Where is this defined?** (`definition`, `at`)
- **What uses it?** (`refs`: every read, write, call, type use, `new`, `typeof`, `nameof`, attribute)
- **What calls it, and what does it call?** (`callers`, `callees`: call hierarchy with call sites)
- **What implements this interface, or overrides this method?** (`implementations`)
- **What does this type derive from, and what derives from it?** (`hierarchy`)
- **What is in this file or type?** (`outline`)

Because it resolves names the way the compiler does, it does not confuse overloads or same-named members, it handles partial
classes, and it can follow a call made through an interface to the implementations that may run
(`callers --through-dispatch`). Nothing leaves your machine: no server, no network.

```text
> codeindex callers Circle.Area --through-dispatch --depth 0 --format table      (file paths trimmed)
callers - Semantics @ 815a282c
target: method Semantics.Core.Circle.Area #49
symbol                                      depth  via                                       call_sites  call_site_count
method Semantics.Core.ShapeBase.Describe    1      method Semantics.Core.ShapeBase.Area #44  [1]         1
method Semantics.Core.ShapeUser.SumAreas    1      method Semantics.Core.IShape.Area #41     [1]         1
method Semantics.Core.ShapeUser.CircleArea  1                                                [1]         1
method Semantics.Core.ShapeUser.Label       2                                                [1]         1
```

`SumAreas` calls `IShape.Area` and `Describe` calls the abstract `ShapeBase.Area`; neither names `Circle.Area`, so a text search for
`Circle.Area` finds only `CircleArea`. The `via` column shows which member each call actually named.

## Install

You need the **.NET 10 SDK** to run the tool. To index a solution you also need whatever SDK that solution requires (the tool
honors its `global.json`) and the solution must be **restored** (`dotnet restore`); the tool never restores or builds for you.

The package is not on nuget.org. Build it and install from the folder:

```powershell
dotnet pack CodeIndexer.Cli -c Release                       # writes artifacts\CodeIndexer.Cli.0.1.0.nupkg
dotnet tool install --global CodeIndexer.Cli --add-source .\artifacts
codeindex --help
```

To pin it to a repository instead (so everyone, and every agent, runs the same version):

```powershell
dotnet new tool-manifest
dotnet tool install CodeIndexer.Cli --add-source C:\path\to\artifacts
dotnet codeindex --help                                       # a local tool is run through `dotnet`
```

## Quick start

```powershell
dotnet restore MyApp.sln
codeindex index MyApp.sln                          # writes .codeindex\MyApp.db next to the solution
codeindex find-symbol OrderService --kind class    # locate it; every result carries an id
codeindex outline --id 118                         # what is in it
codeindex refs --id 118 --ref-kind create,type-use # where it is created or used
codeindex callers PlaceOrder --depth 2             # who calls it, two levels up
codeindex at src\Orders\Checkout.cs:84:21          # what is the identifier I am looking at?
```

When only one solution is indexed you never need `--db`. With several, add `--solution MyApp` (or `--db path`). `codeindex list`
shows what is indexed.

## Commands

| Command | Answers |
|---|---|
| `index <solution>` | Build or update the index. Redoes only what changed; does nothing if nothing did. `--full`, `--dry-run`, `--include`, `--exclude`. |
| `find-symbol <name>` | Symbols by name (exact, prefix, substring), with ids. Generated and library symbols hidden unless `--include-generated` / `--include-external`. |
| `search <text>` | Full-text search over names, signatures and XML docs, for things you cannot name yet. |
| `definition <symbol>` | Every declaration site (a partial class has one per part). |
| `show <symbol>` | Signature, docs, and with `--source` the code. |
| `outline <file or type>` | Symbols nested by container, with line numbers. |
| `at <file:line[:col]>` | The symbol at a position: what is used there, else the declaration containing it. |
| `refs <symbol>` | Every use, with kind and enclosing member. `--ref-kind`, `--file`, `--context`, `--group-by file/member/kind`. |
| `callers` / `callees <symbol>` | Call hierarchy with call sites. `--depth` (0 = unlimited), `--through-dispatch`, `--include-creates`, `--include-external`. |
| `implementations <symbol>` | Types implementing/deriving from a type, or members implementing/overriding a member. `--transitive`. |
| `hierarchy <symbol>` | Base types and derived types. `--direction up/down/both`. |
| `info`, `list`, `resolve` | What is indexed, how fresh, what went wrong while indexing. |
| `mcp` | Serve all of the above to an AI agent (below). |

Add `--format table` for human-readable output. The default is compact JSON (shapes in
[`docs/code-indexer-output-contract.md`](../docs/code-indexer-output-contract.md)).

### Naming a symbol

Every command that is about one symbol takes the same selector: a **name** (`Add`, `Calculator.Add`, `Repository<T>`), an
**`--id`** or **`--key`** from an earlier result, a **documentation id** (`M:Ns.Type.Method(System.Int32)`), or a **position**
(`src\Foo.cs:12:5`, meaning the symbol used there). If a name matches several symbols the answer is not a guess: there is no
`target`, and `candidates` lists them with ids so you can choose.

## Keeping the index fresh

`codeindex index` is safe to run as often as you like. It records everything the index depends on (source files, project files,
restore output, the list of `.cs` files in each project, `Directory.Build.props`, `global.json`, ...) and:

- if nothing changed, reports so in about a quarter of a second without loading MSBuild;
- otherwise redoes only the projects whose inputs changed plus the projects that reference them, in one transaction (a failure
  leaves the old index intact), and the result is identical to a full rebuild;
- rebuilds everything when a build-wide file, the project filter, the SDK, or the tool version changes.

Every query also checks the index against the disk and says `"stale": true` (and which files) if anything changed since it was
built, including files added, deleted or renamed. Treat results as possibly out of date until you re-run `index`.

## Using it with an AI agent

`codeindex mcp` is a read-only [Model Context Protocol](https://modelcontextprotocol.io) server over stdio. One warm process
answers a lookup in a few milliseconds, against roughly a quarter of a second for a fresh `codeindex` process. It offers the
query commands as tools (`find_symbol`, `search`, `definition`, `show`, `outline`, `at`, `refs`, `callers`, `callees`,
`implementations`, `hierarchy`, `index_info`) with descriptions written for a model. It never writes the index; the agent (or you)
runs `codeindex index` in a terminal to refresh it.

Register it with any MCP client, with the usual configuration:

```json
{
  "mcpServers": {
    "codeindex": { "command": "codeindex", "args": ["mcp", "--solution", "MyApp"] }
  }
}
```

For Claude Code: `claude mcp add codeindex -- codeindex mcp --solution MyApp`. The protocol handling is tested against a scripted
client and a real process over stdio; it has not been exercised from every client's UI.

Without MCP, an agent can simply run the CLI. Suggested instructions for its prompt:

> For C# code, prefer `codeindex` over grep or opening files blindly. Run `codeindex index <solution>` first (it is fast when
> nothing changed). Use `find-symbol` to locate a symbol, then `refs`, `callers`, `callees`, `implementations`, `hierarchy` or
> `outline` with `--id`. Only open files for the lines the index points to. If a result says `stale`, re-run `index`. If a name
> is ambiguous, pick a candidate by id.

## What to know before trusting a result

- **Compile errors are reported.** If a project does not compile cleanly (not restored, missing reference), names involved cannot
  be resolved and references through them are missing. `info` and `index` show the error count per project and the
  `dotnet restore` command to run.
- **One target framework per project.** A multi-targeted project is indexed once, on its newest modern TFM (a
  `netstandard2.0;net462` project on `netstandard2.0`). Code under `#if` for the other targets is not indexed.
- **Generated code is flagged**, hidden from searches by default, and included in the call graph (flagged) so chains stay
  connected.
- **Library calls are stored too** (`HttpClient.GetStringAsync`, `List<T>.Add`), so "who calls X" works for framework and NuGet
  APIs; they are hidden from listings unless asked for.
- **C# only.** VB projects get a warning and are skipped. Operators, implicit conversions, `foreach`/`await` pattern calls and
  deconstruction are not recorded as references yet.
- **Added files outside a project's directory** (a `Compile Include="..\Shared\*.cs"`) are noticed only when the project file
  changes.

## How fast

Measured on the Google Cloud .NET SDK fork (Release build):

| API | Full index | One file changed | Nothing changed | Peak memory |
|---|---|---|---|---|
| `Google.Cloud.Storage.V1` (6,240 symbols, 43k references) | 22 s | 10 to 20 s | 0.24 s | not measured |
| `Google.Cloud.PubSub.V1` (5,070 symbols, 37k references) | 19 s | not measured | not measured | 335 MB |

Most of an update is MSBuild loading the solution (about 7 s). Queries run in 1 to 12 ms inside the process.

## Development

```powershell
dotnet build CodeIndexer.slnx
dotnet test CodeIndexer.slnx            # about 2 minutes; restores its own fixtures on first use
dotnet pack CodeIndexer.Cli -c Release  # the tool package
```

| Path | |
|---|---|
| `CodeIndexer.Core` | Loader, extractors, writer, incremental runner, queries, MCP server. |
| `CodeIndexer.Cli` | The `codeindex` command (a thin layer over Core). |
| `CodeIndexer.Tests` | xUnit tests, including end-to-end tests of the executable and of the MCP protocol. |
| `tests\Fixtures` | `SimpleClassLib`, `MultiTarget`, `Semantics` (hard cases, with `// @marker` comments the tests use to find lines), `Incremental` (a dependency chain for update tests). |
| `..\docs` | The development plan and the JSON output contract. |

`PendingFact` marks a test for behavior not built yet; set `CODEINDEX_RUN_PENDING=1` to run those.

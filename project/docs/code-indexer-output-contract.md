# CodeIndexer Output Contract

Every query command prints one JSON object (snake_case) to stdout. Add `--format table` for a plain-text rendering
of the same response. Errors that stop a command (no index, query failure) go to stderr with a non-zero exit code;
everything else, including "not found" and "ambiguous", is a normal response with `warnings`.

Exit codes: `0` ok (including not-found/ambiguous), `2` no index database found, `3` the query failed (for example
an index built by an older version: re-run `index`).

---

## 1. The envelope

| Field | Type | Present | Description |
|---|---|---|---|
| `command` | string | always | Which command produced this response |
| `solution_name` | string | always | Name of the indexed solution |
| `solution_path` | string | always | Solution path, relative to the repo root, `/` separators |
| `indexed_at` | string | always | ISO 8601 time the index was built |
| `indexed_commit` | string | always | Git commit at index time (`"unknown"` if not in a repository) |
| `stale` | boolean | always | `true` if an indexed file changed or disappeared since indexing |
| `stale_files` | string[] | when stale | Up to 20 changed or missing files |
| `target` | Symbol | selector commands, when resolved | The symbol the selector resolved to |
| `candidates` | Symbol[] | selector commands, when ambiguous | The symbols the selector matched (at most 10) |
| `data` | object or array | always | Command-specific payload (sections 4-5) |
| `total_count` | integer | always | Items in `data` (for tree results: nodes) |
| `total_matches` | integer | when known | Matches before `limit`/`offset` were applied |
| `limit` | integer | when a limit applied | The limit in effect |
| `truncated` | boolean | always | `true` if the result was cut by `limit` |
| `warnings` | string[] | when any | Non-fatal notes. If stale, the first one says so. |

Staleness is checked on every query against the inputs recorded when the index was built: the source files of the indexed
projects, their project files and restore output (`obj/project.assets.json`), a fingerprint of the `*.cs` files under
each project directory (so a file that was **added, deleted, or renamed** is noticed), and the solution and build-wide
files (`Directory.Build.props`, `global.json`, ...). A file whose size and modification time are unchanged costs one stat;
only a file whose stat differs is hashed, and it counts as stale only if its content differs. Projects the filter excluded
are not checked. `stale_files` lists paths; a directory whose file list changed appears as
`path/ (files added, deleted, or renamed)`. Re-run `index` to refresh.

### Example

```json
{
  "command": "callers",
  "solution_name": "Semantics",
  "solution_path": "project/code-indexer/tests/Fixtures/Semantics/Semantics.sln",
  "indexed_at": "2026-10-24T14:13:31.5370396Z",
  "indexed_commit": "a2d13cb4...",
  "stale": false,
  "target": { "id": 22, "key": "Semantics.Core|M:Semantics.Core.Calculator.Add(System.Int32,System.Int32)", "name": "Add", "kind": "method", "...": "..." },
  "data": [ { "symbol": { "...": "..." }, "depth": 1, "call_sites": [ { "file_path": "...", "line": 16, "column": 19 } ], "call_site_count": 1 } ],
  "total_count": 4,
  "limit": 50,
  "truncated": false
}
```

---

## 2. Naming a symbol (selectors)

Every command that is about one symbol (`definition`, `show`, `outline`, `refs`, `callers`, `callees`,
`implementations`, `hierarchy`) takes a selector. In order of precision:

| Selector | Form | Notes |
|---|---|---|
| id | `--id 22` | Row id from a previous result. Valid until the next `index`. |
| key | `--key "Semantics.Core\|M:Semantics.Core.Calculator.Add(System.Int32,System.Int32)"` | Stable across re-indexing. The part after `\|` (the documentation id) is enough, and may also be passed as the positional argument. |
| position | `src/File.cs:12` or `src/File.cs:12:5` | The symbol used at that position, else the declaration containing it (see `at`). The file may be given by any unique path suffix. |
| name | `Add`, `Calculator.Add`, `global::Ns.Type`, `Repository<T>` | Matches, in order: the full name; the simple name; `Container.Member` where Container is the declaring type or namespace; any trailing dotted path. Generic argument lists are ignored. |

`--kind <kind>` narrows a name to one symbol kind. Name matching prefers indexed symbols over external ones
(framework/NuGet stubs are tried only when nothing indexed matches) and hand-written over generated code unless
`--include-generated` is given.

**Ambiguity.** A name that matches several symbols never resolves to an arbitrary one. The response has no
`target`, `data` is empty, `candidates` lists the matches (with `id` and `key`), and `warnings[0]` says how many
matched and how to pick one. **Not found** is the same shape with no candidates and a `Symbol not found` warning.

---

## 3. The Symbol payload

Used in `target`, `candidates`, `find-symbol`, `callers`/`callees` (`symbol`), `implementations`, `hierarchy`, `at`.

| Field | Type | Description |
|---|---|---|
| `id` | integer | Row id within this database |
| `key` | string | `<assembly>\|<documentation id>`, for example `Semantics.Core\|T:Semantics.Core.Calculator`. Namespaces have an empty assembly. |
| `name` | string | Simple name (an explicit interface implementation's name includes the interface) |
| `full_name` | string | Fully qualified, `global::` prefixed, includes the containing type |
| `kind` | string | `namespace`, `class` (also records), `struct`, `interface`, `enum`, `delegate`, `method` (also constructors, named `.ctor`), `property`, `field`, `event` |
| `accessibility` | string | `public`, `internal`, `protected`, `private`, ... |
| `signature` | string | Minimally qualified display signature |
| `start_line`, `end_line` | integer | 1-based range of the primary declaration; absent for namespaces and external symbols |
| `file_path` | string | Primary declaration's file, repo-relative; absent for namespaces and external symbols |
| `doc_summary` | string | First `<summary>` paragraph, when documentation is generated |
| `is_generated` | boolean | The primary declaration is in a generated file |
| `is_external` | boolean | Declared outside the indexed projects (framework, NuGet, excluded projects). Such symbols exist because indexed code uses them; they have no location and only members that are used are listed. |
| `project_name` | string | Project of the primary declaration |

A `SymbolRef` (used where a full payload would be noise) has `id`, `key`, `name`, `full_name`, `kind`.

---

## 4. Search and lookup commands

### `find-symbol <name> [--kind K] [--project P] [--include-generated] [--include-external] [--limit N] [--offset N]`
Symbols whose name matches, exact first, then prefix, then substring; input with other characters is a full-text
query. Generated and external symbols are hidden by default. `data`: Symbol[]. `total_matches` counts all matches.
A malformed full-text query is a warning, not an error.

### `search <text> [--include-generated] [--include-external] [--limit N]`
Full-text search (FTS5 syntax) over names, signatures, and documentation. `data`: SearchHit[] = Symbol fields
(`id`, `key`, `name`, `full_name`, `kind`, `signature`, `file_path`, `doc_summary`, `start_line`, `is_generated`,
`is_external`, `project_name`) plus `rank` (more negative is more relevant).

### `definition <selector>`
Where a symbol is declared. `data`: `{ symbol, locations }`. Each location: `file_path`, `start_line`,
`start_column`, `end_line`, `end_column`, `is_primary`, `is_generated`. A partial type has one location per part
(hand-written first). An external symbol has no locations and a warning saying so.

### `show <selector> [--source] [--max-lines N]`
`data`: `{ symbol, locations, source_code?, source_truncated }`. `source_code` is the primary declaration, only with
`--source`, cut to `--max-lines` (default 200) with `source_truncated` set and a warning.

### `outline <file-or-type>`
If the argument names an indexed file (any unique path suffix), the file's structure; otherwise the argument is
resolved as a symbol and its members are shown. `data`: `{ file_path?, line_count?, is_generated, symbols }` where
`symbols` is a tree: each node is a Symbol plus `children` (nodes). A file outline places symbols by where they are
declared in *that* file, so a partial type lists only its members from that part; ranges are that file's. A name
shared by several files returns a warning listing them.

### `at <file:line[:col]>`
The symbol at a source position. `data`:

| Field | Description |
|---|---|
| `match` | `reference`: on a use of `symbol`. `declaration`: on the first line of `symbol`'s declaration. `containing`: inside `symbol`'s declaration. `none` when nothing matched. |
| `symbol` | The symbol (also in `target`) |
| `reference_kind` | For a reference: the kind of use (section 5, `refs`) |
| `enclosing` | For a reference: the member that contains it (SymbolRef) |
| `file_path`, `line`, `column` | The position asked about |

With a column, the reference whose token covers it wins. Without a column, a line with several uses is ambiguous:
`candidates` lists them and the warning asks for a column. Positions with no use fall back to the innermost
declaration containing the line.

---

## 5. Relationship commands

### `refs <selector> [--ref-kind a,b] [--file F] [--project P] [--include-generated] [--context] [--group-by none|file|member|kind] [--limit N] [--offset N]`
Every recorded use of the symbol. `data`: `{ references, groups? }`. Each reference:

| Field | Description |
|---|---|
| `file_path`, `line`, `column` | Where (1-based; column is the start of the referenced name) |
| `kind` | `call`, `use` (method group), `read`, `write` (compound assignment and `++` give both a `write` and a `read`), `type-use`, `create` (a `new` with a declared constructor, or target-typed `new()`), `inherit`, `implement`, `attribute`, `typeof`, `nameof` |
| `is_generated` | The reference is in a generated file |
| `enclosing` | The member or type that contains it (SymbolRef); lambdas and local functions belong to their containing member, property accessors and initializers to the property |
| `context` | The source line, only with `--context` |

References in generated files are excluded by default and the response says how many were hidden. With
`--group-by`, `references` is empty and `groups` holds `{ group, count, references }` (file path, enclosing member
full name, or kind). `total_matches` counts references before paging.

### `callers <selector>` / `callees <selector>` `[--depth N] [--limit N] [--through-dispatch] [--include-creates] [--include-external] [--exclude-generated]`
Incoming and outgoing calls, breadth first. `--depth 0` is unlimited (default 1). `data`: CallNode[]:

| Field | Description |
|---|---|
| `symbol` | The calling (callers) or called (callees) member |
| `depth` | Distance from the target |
| `via` | With `--through-dispatch`: the interface/abstract/base member actually named at the call site, when it differs from the symbol asked about (callers) or from this node (callees). Absent for direct calls. |
| `call_sites` | Up to 10 `{ file_path, line, column }` where the call is made, in the calling member |
| `call_site_count` | Total call sites |

Each member appears once, at the shallowest depth it is reached; call sites are for that first path.
`--through-dispatch` (callers): a call to an interface, abstract, or base member that the symbol implements or
overrides counts as a call to the symbol. (callees): a called interface/abstract member also lists the members
that implement or override it, with `via` set. `--include-creates` also follows object creation, so
`callers <type> --include-creates` lists who creates it. External callees (framework/NuGet) are hidden unless
`--include-external`; callers of an external symbol are always indexed members. Generated members are included and
flagged (`is_generated`) so chains stay connected; `--exclude-generated` leaves them out. Property, field and event
accesses are references, not calls: `callers` on them returns a warning pointing at `refs`.

### `implementations <selector> [--transitive] [--limit N]`
For a type: the types that implement or inherit from it. For a method, property or event: the members that
implement or override it. Direct by default; `--transitive` follows the chain (for example an interface member's
implementation, then that implementation's overrides). `data`: `{ symbol, relation, depth }[]` with `relation` one of
`implements`, `inherits`, `overrides`.

### `hierarchy <selector> [--direction up|down|both] [--depth N] [--limit N]`
Supertypes (base class and interfaces; external ones included and flagged) and subtypes (derived types and
implementers) of a type. `data`: `{ supertypes, subtypes }`, each a tree of `{ symbol, relation, children }` where
`relation` (`inherits` or `implements`) is relative to the node it hangs under. Default depth 3.

---

## 6. Non-query commands

`index <solution> [--db P] [--include pattern] [--exclude pattern] [--full] [--dry-run]` brings the index up to date and
prints what it did (plain text):

- **Up to date:** nothing the index depends on changed, so nothing is loaded or written (milliseconds).
- **Incremental:** only the projects a change affects are redone, with the reason for each (`changed: ...`,
  `redo: <project> (<why>)`, `keep: <project>`). A project is redone if one of its inputs changed, if it was added, or if a
  project it references is redone.
- **Full:** the whole index is rebuilt, with the reason (first index, `--full`, a build-wide file or the project filter or
  the SDK or the tool version changed, an unreadable or older index, or every project affected).

`--dry-run` prints the plan and changes nothing. An update is one transaction: a failure leaves the previous index intact.
After an incremental update, symbol `id`s of the projects that were kept are unchanged; the ids of redone projects are new,
and `key`s are always stable.

`info` prints statistics, staleness, and diagnostics. `list` and `resolve` read the catalog.

Database selection for queries: `--db <path>`, else `--solution <name>` via the catalog, else the only indexed
solution if there is exactly one.

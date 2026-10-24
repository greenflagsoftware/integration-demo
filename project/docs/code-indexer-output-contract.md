# CodeIndexer JSON Output Contract

Every query command returns a `QueryResponse<T>` envelope in JSON (snake_case). The envelope carries
solution context, staleness info, truncation flags — everything Frank needs to trust or question the result.

---

## 1. Envelope fields (present on every response)

| Field | Type | Always | Description |
|---|---|---|---|
| `command` | `string` | yes | Which command produced this response |
| `solution_name` | `string` | yes | Name of the indexed solution |
| `solution_path` | `string` | yes | Solution path relative to repo root |
| `indexed_at` | `string` | yes | ISO 8601 timestamp when the index was built |
| `indexed_commit` | `string` | yes | Git commit SHA at index time (or `"unknown"`) |
| `stale` | `boolean` | yes | `true` if files have changed since indexing |
| `data` | `object or array` | yes | Command-specific payload (see sections below) |
| `total_count` | `integer` | yes | How many items are in `data` |
| `limit` | `int or null` | no | Limit value that was applied, or `null` if unbounded |
| `truncated` | `boolean` | yes | `true` if the result was truncated due to `limit` |
| `warnings` | `string[] or null` | no | Non-fatal warnings |

### Example envelope

```json
{
  "command": "find-symbol",
  "solution_name": "SimpleClassLib",
  "solution_path": "project/code-indexer/tests/Fixtures/SimpleClassLib/SimpleClassLib.sln",
  "indexed_at": "2026-10-24T12:57:21.9459617Z",
  "indexed_commit": "ff6279f759a7228d637a262999bd062c83bd3a1b",
  "stale": false,
  "data": [ ... ],
  "total_count": 2,
  "limit": 20,
  "truncated": false
}
```

---

## 2. `find-symbol`

`codeindex find-symbol <name> [--kind KIND] [--project PROJ] [--include-generated] [--limit N] [--offset N]`

Matching: exact name -> prefix -> LIKE -> FTS (falls back automatically).
Generated code excluded by default.

### Symbol payload

| Field | Type | Always | Description |
|---|---|---|---|
| `name` | `string` | yes | Short symbol name |
| `full_name` | `string or null` | no | Fully qualified name |
| `kind` | `string` | yes | `namespace`, `class`, `struct`, `interface`, `enum`, `method`, `property`, `field`, `event`, `other` |
| `accessibility` | `string or null` | no | `public`, `internal`, `protected`, `private` |
| `signature` | `string or null` | no | Minimal signature |
| `start_line` | `int or null` | no | 1-based start line |
| `end_line` | `int or null` | no | 1-based end line |
| `file_path` | `string or null` | no | Path relative to repo root |
| `doc_summary` | `string or null` | no | First `<summary>` paragraph from XML doc |
| `is_generated` | `boolean` | yes | Whether the enclosing file is auto-generated |
| `project_name` | `string or null` | no | Name of the project containing this symbol |

---

## 3. `outline`

`codeindex outline <file> [--solution NAME] [--db PATH]`

Returns symbols in a file, ordered by `start_line`.

### Outline payload

| Field | Type | Always | Description |
|---|---|---|---|
| `file_path` | `string` | yes | Resolved file path relative to repo root |
| `line_count` | `int` | yes | Total lines in the file |
| `is_generated` | `boolean` | yes | Whether the file is auto-generated |
| `symbols` | `array` | yes | Array of symbol objects (same shape as `find-symbol`) |

---

## 4. `show`

`codeindex show <symbol> [--source] [--solution NAME] [--db PATH]`

### Show payload

| Field | Type | Always | Description |
|---|---|---|---|
| `symbol` | `object` | yes | Symbol object (same shape as `find-symbol`) |
| `source_code` | `string or null` | no | Source lines for the symbol definition (only with `--source`) |

When the symbol is not found, `symbol.kind` is `"unknown"` and `total_count` is `0`.

#### Example with `--source`

```json
{
  "command": "show",
  "data": {
    "symbol": {
      "name": "Calculator",
      "full_name": "global::SimpleLib.Calculator",
      "kind": "class",
      "start_line": 12,
      "end_line": 23
    },
    "source_code": "public class Calculator : ICalculator\n{\n    public int Add(int a, int b) => a + b;\n    ..."
  },
  "total_count": 1
}
```

---

## 5. `search`

`codeindex search <text> [--include-generated] [--limit N] [--solution NAME] [--db PATH]`

Full-text search via FTS5 across names, signatures, and docs. Supports FTS5 query syntax.

### Search hit payload

| Field | Type | Always | Description |
|---|---|---|---|
| `name` | `string` | yes | Short symbol name |
| `full_name` | `string or null` | no | Fully qualified name |
| `kind` | `string` | yes | Symbol kind |
| `signature` | `string or null` | no | Minimal signature |
| `file_path` | `string or null` | no | Path relative to repo root |
| `doc_summary` | `string or null` | no | First `<summary>` paragraph |
| `start_line` | `int or null` | no | 1-based start line |
| `is_generated` | `boolean` | yes | Whether the file is auto-generated |
| `project_name` | `string or null` | no | Project name |
| `rank` | `double` | yes | FTS5 relevance score (negative = more relevant) |

---

## 6. `index` and `info` (non-query commands)

These return plain text, not JSON. Designed for human reading.

### `index` output

```
Solution: .../SimpleClassLib.sln
Projects: 2 total, 2 loaded, 1 excluded

  [include] SimpleLib (loaded, net10.0, 4 documents)
  [excluded] SimpleLib.Tests (loaded, net10.0, 4 documents)

Indexing to: .../SimpleClassLib.db
Indexed: 1 projects, 4 files, 14 symbols
```

### `info` output

```
Database: .../SimpleClassLib.db
Projects: 1
Files: 4
Symbols: 14
Tool version: 0.1.0
Indexed at: 2026-10-24T12:57:21.9429562Z
Repo root: E:/Dev/demos/integration-demo
Git commit: ff6279f759a7228d637a262999bd062c83bd3a1b
```

---

## 7. Error responses

Errors are printed to stderr as plain text, not wrapped in JSON:

```
No index database found. Run 'codeindex index' first.
```

Future commands (Phase 3+) may return partial results with errors embedded in the `warnings` array.

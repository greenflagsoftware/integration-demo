---
description: n8n data tables as the fake datastore for mocks: column types, the Data table node's table and row operations, creating and seeding a table from a workflow with no UI or API key, an idempotent seed pattern, a tested stateful create and list mock, and what cannot be done from the CLI. Read when a mock must remember state, or when asked to create a dataset, table, or seed data in n8n.
type: reference
---

# n8n data tables (Frank's "datasets")

A data table is a small database table that lives inside n8n, owned by a project (here: the owner's personal project). Mocks use one when the fake service must remember something: objects a client created, issued tokens, counters, lists to page through. Everything below was tested on n8n **2.44.6** unless marked "docs only".

## What a table is

- Columns have a **name** (`^[a-zA-Z][a-zA-Z0-9_]*$`, up to 63 chars) and a **type**: `string`, `number`, `boolean`, or `date`. (Docs say at least one column is required and a table name must be unique within its project.)
- Every table also gets automatic columns: **`id`** (an auto-incrementing integer, starts at 1), `createdAt`, `updatedAt`. Don't define them.
- Row values are strings, numbers, booleans, or null. No nested objects or arrays: store JSON as a string column and parse it in a Code node.
- Data persists across restarts (it is in the instance database, in the `n8n_data` volume).

## The Data table node (`n8n-nodes-base.dataTable`, typeVersion 1.1)

**`resource: "table"`** operations: `create`, `delete`, `list`, `update` (rename), `clear` (delete all rows, keep the table).
- `create`: `"tableName": "demo_items"`, `"columns": { "column": [ { "name": "price", "type": "number" } ] }`, `"options": { "createIfNotExists": true }` (default true, so creating an existing table is harmless).

**`resource: "row"`** operations: `insert`, `get`, `update`, `upsert`, `deleteRows`, `rowExists`, `rowNotExists`.
- Pick the table with a resource locator. **By name is the easy one for generated JSON:** `"dataTableId": { "__rl": true, "mode": "name", "value": "demo_items" }`. (`mode: "id"` and `"list"` also exist.)
- `insert`: `"columns": { "mappingMode": "autoMapInputData", "value": null }` maps the incoming item's fields to columns **by exact name**. Shape the item in a Code node first.
- `get`: `"returnAll": true` (or `limit`), optional `filters` (`{ "conditions": [ { "keyName", "condition": "eq", "keyValue" } ] }`), `matchType` (`anyCondition|allConditions`), `orderBy`/`orderByColumn`/`orderByDirection`.
- `upsert` and `update` take `filters` + `columns`; both support `options.dryRun`. The condition operators are Equals, Not Equals, Greater/Less Than (or equal), Is Empty, Is Not Empty.
- Each returned row includes `id`, `createdAt`, `updatedAt`.

## Create and seed a table from a workflow (tested)

**No UI and no API key needed.** A workflow can create the table and fill it. The catch: run it **through its own webhook on the running server**. Running it with `n8n execute` fails with *"Attempted to use Data table node but the module is disabled"* (the one-off CLI process doesn't load the data table module).

### Idempotent seed: create if missing, clear, insert (run it as often as you like)

Nodes in order, each connected to the next (every link is needed): `Seed webhook` (POST `seed/<sdk>`, `responseNode`) → `Create table` (`createIfNotExists: true`) → `Clear table` (`resource table`, `operation clear`, `dataTableId` by name) → `Rows` (a Code node returning the seed rows as `[{ json: {...} }, ...]`) → `Insert rows` → `Respond` (`={{ { seeded: $input.all().length } }}`).

Tested: running it twice leaves exactly the same rows (ids restart at 1 after `clear`). A plain insert-only seed **duplicates rows on every run**, so don't.

```json
{"name": "Create table", "type": "n8n-nodes-base.dataTable", "typeVersion": 1.1, "parameters": {"resource": "table", "operation": "create", "tableName": "demo_items", "columns": {"column": [{"name": "name", "type": "string"}, {"name": "price", "type": "number"}, {"name": "inStock", "type": "boolean"}]}, "options": {"createIfNotExists": true}}}
{"name": "Clear table", "type": "n8n-nodes-base.dataTable", "typeVersion": 1.1, "parameters": {"resource": "table", "operation": "clear", "dataTableId": {"__rl": true, "mode": "name", "value": "demo_items"}}}
{"name": "Insert rows", "type": "n8n-nodes-base.dataTable", "typeVersion": 1.1, "parameters": {"resource": "row", "operation": "insert", "dataTableId": {"__rl": true, "mode": "name", "value": "demo_items"}, "columns": {"mappingMode": "autoMapInputData", "value": null}, "options": {}}}
```

(Add `id`, `position`, `webhookId`, and the `connections` as in `n8n-workflow-json.md`. These three lines are the node objects, not a whole workflow.)

## A stateful mock: create an item, list items (tested)

`POST /webhook/demo/store/items` stores a row and answers `201` with it; `GET /webhook/demo/store/items` returns every row. State survives between calls and restarts.

Create (Webhook → Code → Insert → Respond):

```json
{
  "name": "mock-demo-store-create",
  "nodes": [
    {"id": "cf5f389c-b888-41c1-92fd-53bc233e6712", "name": "Webhook", "type": "n8n-nodes-base.webhook", "typeVersion": 2.2, "position": [0, 0], "parameters": {"httpMethod": "POST", "path": "demo/store/items", "responseMode": "responseNode", "options": {}}, "webhookId": "bdf41842-dbe5-446f-9979-d158e69c1397"},
    {"id": "0404b883-a2ad-4d79-97da-7f0510162687", "name": "Shape row", "type": "n8n-nodes-base.code", "typeVersion": 2, "position": [220, 0], "parameters": {"mode": "runOnceForAllItems", "language": "javaScript", "jsCode": "const b = $input.first().json.body ?? {};\nreturn [{ json: { name: String(b.name ?? ''), price: Number(b.price ?? 0), inStock: b.inStock ?? true } }];"}},
    {"id": "70aa9dbc-b91b-4b0e-b531-966a7f0d5d03", "name": "Insert row", "type": "n8n-nodes-base.dataTable", "typeVersion": 1.1, "position": [440, 0], "parameters": {"resource": "row", "operation": "insert", "dataTableId": {"__rl": true, "mode": "name", "value": "demo_items"}, "columns": {"mappingMode": "autoMapInputData", "value": null}, "options": {}}},
    {"id": "7144e9b3-249a-46d8-bdfe-fdc09948760e", "name": "Respond", "type": "n8n-nodes-base.respondToWebhook", "typeVersion": 1.5, "position": [660, 0], "parameters": {"respondWith": "json", "responseBody": "={{ $json }}", "options": {"responseCode": 201}}}
  ],
  "connections": {"Webhook": {"main": [[{"node": "Shape row", "type": "main", "index": 0}]]}, "Shape row": {"main": [[{"node": "Insert row", "type": "main", "index": 0}]]}, "Insert row": {"main": [[{"node": "Respond", "type": "main", "index": 0}]]}},
  "settings": {"executionOrder": "v1"}
}
```

List: Webhook (`GET`, `responseMode: lastNode`, `responseData: allEntries`) → Data table `get` with `"returnAll": true`, `"matchType": "anyCondition"`, `"filters": {}`, `"orderBy": false`. With `lastNode`/`allEntries` the response is the JSON array of rows.

## Ways to create or manage tables besides a seed workflow

- **The editor** (Data tables tab): fine for the user to look at what the mock stored.
- **n8n's public API / the instance MCP server** (docs only, untested here): the MCP tool reference lists `create_data_table`, `add_data_table_rows` (up to 1000 rows per call) from n8n 2.16.0. These need an owner account and an API key or token that Frank doesn't have until the user sets one up.
- **The container CLI cannot** create tables or run Data table nodes (module disabled in CLI executions). Use the seed-workflow route above.

## Gotchas

- `autoMapInputData` maps fields to columns by name, so the item's field names must match the table's column names. Shape the item in a Code node first (as the create example does) rather than passing the raw webhook item. **Extra, unmatched fields cause the insert to fail** with `"unknown column name '<field>'"` (tested 2026-10-25). Strip out any fields that are not table columns before passing to a Data table `insert` node.
- **Each inserted row gets auto-generated `id`, `createdAt`, `updatedAt` columns** (tested). These are not in your schema — the Data table adds them. When you need a timestamp in your mock API response, read from `row.createdAt` or `row.updatedAt` in a subsequent Code node. **Do not store `createTime`, `updateTime`, or similar names in the Code node that feeds the insert** unless they are explicit columns in your table schema.
- Example of the pattern (Code node before insert outputs only matching fields; response Code node uses `row.createdAt`):
```javascript
// Pre-insert Code node — ONLY table column fields
return [{ json: { name, uid, etag, displayName, parent, accessPolicyId } }];

// Response formatting Code node (after insert) — uses auto-generated createdAt
const ts = row.createdAt || new Date().toISOString();
return [{ json: { name: row.name, createTime: ts, updateTime: ts, /* ... */ } }];
```
- **`createIfNotExists: true` will NOT add new columns to an existing table** (tested 2026-10-25). If the table already exists with fewer columns, the seed workflow's `Create table` node does nothing. To update the schema: delete the table first (n8n editor > Data tables tab) then re-run the seed, or use a different table name.
- A data table is shared by every workflow that names it: two mocks writing `demo_items` see each other's rows. Use one table per SDK or per resource, prefixed (`gcs_objects`).
- Looking a table up by name relies on the name being unique within the project (docs only: table names must be unique per project).
- Changing a table's columns was not tested. The simple route is a new table name (or, in the sandbox, ask the user before deleting and re-creating one: deleting is not covered by the overwrite permission).

Never put real secrets in this file.

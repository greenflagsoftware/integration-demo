---
description: The n8n workflow JSON format and the exact parameters of the nodes mocks use (Webhook, Respond to Webhook, Code, Wait, IF/Switch/Set, Data table) for n8n 2.44.6, with two complete tested workflows, the shape of the Webhook output item, expressions, and common JSON mistakes. Read when writing, editing, or debugging a workflow's JSON.
type: reference
---

# Writing n8n workflow JSON

Everything here was checked against n8n **2.44.6** (the node list and parameters come from the image itself; see "How to check a node" below).

## A workflow file

```json
{ "name": "mock-<sdk>-<operation>",
  "nodes": [ ... ],
  "connections": { "<from node name>": { "main": [ [ { "node": "<to node name>", "type": "main", "index": 0 } ] ] } },
  "settings": { "executionOrder": "v1" } }
```

- Required: `name`, `nodes`, `connections`, `settings`. No `id` for a **new** workflow (n8n makes one). **A file that has an `id` overwrites the workflow with that id** on import: that is how you update in the sandbox, and a trap if you re-import an exported file by accident.
- A node: `{ "id": "<unique string>", "name": "<unique name>", "type": "n8n-nodes-base.webhook", "typeVersion": 2.2, "position": [x, y], "parameters": { ... } }`. Webhook nodes also carry `"webhookId": "<string>"` (see `n8n-mock-api-recipes.md` for why it matters).
- **`connections` is keyed by node *name*, and a node with no entry is a dead end.** Each node's output lists the nodes it feeds. Forgetting a link does not error: the workflow just stops there. (This cost a test run during research: the response was the output of the node before the missing link.)
- An exported workflow has many more fields (`id`, `versionId`, `active`, `tags`, `shared`, ...). Strip `id` and `versionId` when you want a copy rather than an overwrite.

## Node types and the newest `typeVersion` in 2.44.6

| Node | `type` | typeVersion |
|---|---|---|
| Webhook | `n8n-nodes-base.webhook` | 2.2 |
| Respond to Webhook | `n8n-nodes-base.respondToWebhook` | 1.5 |
| Code | `n8n-nodes-base.code` | 2 |
| IF | `n8n-nodes-base.if` | 2.4 |
| Switch | `n8n-nodes-base.switch` | 3.5 |
| Set (Edit Fields) | `n8n-nodes-base.set` | 3.5 |
| Wait | `n8n-nodes-base.wait` | 1.1 |
| Data table | `n8n-nodes-base.dataTable` | 1.1 |
| Filter / Merge / Aggregate | `...filter` 2.4 / `...merge` 3.2 / `...aggregate` 1 | |
| HTTP Request | `n8n-nodes-base.httpRequest` | 4.5 |
| Execute Workflow | `n8n-nodes-base.executeWorkflow` | 1.4 |
| Stop and Error | `n8n-nodes-base.stopAndError` | 1 |
| Manual Trigger | `n8n-nodes-base.manualTrigger` | 1 |

Use the newest unless there's a reason; older versions have different parameters.

## The nodes mocks use (tested)

**Webhook (2.2)**: `httpMethod` (`GET|POST|PUT|PATCH|DELETE|HEAD`), `path`, `responseMode` (`onReceived` | `lastNode` | `responseNode` | `streaming`), `authentication` (`none|basicAuth|headerAuth|jwtAuth|n8nOAuth2`, the last three need a credential), `options` (`allowedOrigins`, `ipWhitelist`, `responseHeaders`, `rawBody`, `binaryPropertyName`, `ignoreBots`, `onlyRunIf`, ...).
- For mocks use `"responseMode": "responseNode"` and send the answer with a Respond to Webhook node. `lastNode` returns the last node's output with `"responseData": "allEntries"` or `"firstEntryJson"`. With `lastNode` or `onReceived`, the status code is `options.responseCode` as `{ "values": { "responseCode": 200 } }`.
- **The item it outputs** (tested): `{ headers, params, query, body, webhookUrl, executionMode }`. `headers` keys are lower-case (`headers.authorization`). Path parameters are in `params`, the query string in `query`, the parsed JSON body in `body`.

**Respond to Webhook (1.5)**: `respondWith` = `json` | `text` | `allIncomingItems` | `firstIncomingItem` | `noData` | `binary` | `redirect` | `jwt`.
- JSON body from the previous node: `"responseBody": "={{ $json.body }}"`; a literal body: `"responseBody": "={\"error\":{\"code\":429}}"` (a string starting with `=` is an expression; plain JSON after the `=` works).
- `options.responseCode`: a number, or an expression such as `"={{ $json.statusCode }}"` (tested, gives per-request status codes).
- `options.responseHeaders`: `{ "entries": [ { "name": "Retry-After", "value": "3" } ] }` (tested).

**Code (2)**: `"mode": "runOnceForAllItems"`, `"language": "javaScript"`, `"jsCode": "..."`. Read input with `$input.first().json` or `$input.all()`. **Return an array of `{ json: {...} }`.** JavaScript only here.

**Wait (1.1)**: `{ "resume": "timeInterval", "amount": 2, "unit": "seconds" }` delays the flow, so a mock can simulate latency (tested: 2 s).

**Data table (1.1)**: see `n8n-data-tables.md`.

**IF / Switch / Set** have versioned condition and assignment structures that are fiddly to write by hand. For a mock, a **Code node that returns `{ statusCode, body }` followed by one Respond to Webhook** is simpler and was tested. If you need IF/Switch/Set, build one in the editor (or look at a docs example via Context7) and export it, rather than guessing the JSON.

## Expressions

`={{ ... }}` inside a parameter string is JavaScript evaluated per item. Useful: `$json` (current item), `$('Node Name').item.json` (another node's output), `$input.all()`, `$now`, `$execution.id`. Strings that start with `=` are expressions; strings that don't are literals.

## Complete tested example 1: validate, then answer 201 or 400

Paste into `n8n\workflows\mock-demo-items.json`. `POST /webhook/demo/items` with `{"name":"x"}` gives `201`, without `name` gives `400`.

```json
{
  "name": "mock-demo-items",
  "nodes": [
    {"id": "023b84ba-8d7b-4515-aab8-bf27e3bee7fb", "name": "Webhook", "type": "n8n-nodes-base.webhook", "typeVersion": 2.2, "position": [0, 0], "webhookId": "e99b8f77-b6c4-4b9d-ac5c-a47aaef6c672", "parameters": {"httpMethod": "POST", "path": "demo/items", "responseMode": "responseNode", "options": {}}},
    {"id": "39a598c0-b41e-4834-bf15-399c3cc0bf75", "name": "Validate", "type": "n8n-nodes-base.code", "typeVersion": 2, "position": [220, 0], "parameters": {"mode": "runOnceForAllItems", "language": "javaScript", "jsCode": "const body = $input.first().json.body ?? {};\nif (!body.name) {\n  return [{ json: { statusCode: 400, body: { error: { code: 400, message: 'name is required' } } } }];\n}\nreturn [{ json: { statusCode: 201, body: { id: 'item-1', name: body.name, createdTime: new Date().toISOString() } } }];\n"}},
    {"id": "92f2e0ed-b8da-4343-9a20-dc502e4a51e9", "name": "Respond", "type": "n8n-nodes-base.respondToWebhook", "typeVersion": 1.5, "position": [440, 0], "parameters": {"respondWith": "json", "responseBody": "={{ $json.body }}", "options": {"responseCode": "={{ $json.statusCode }}"}}}
  ],
  "connections": {"Webhook": {"main": [[{"node": "Validate", "type": "main", "index": 0}]]}, "Validate": {"main": [[{"node": "Respond", "type": "main", "index": 0}]]}},
  "settings": {"executionOrder": "v1"}
}
```

## Complete tested example 2: a throttled `429` with headers and a delay

Static path, so the URL is `GET /webhook/demo/throttled`. Answers after 2 s with `429`, `Retry-After: 3`, and a JSON error body.

```json
{
  "name": "mock-demo-throttle",
  "nodes": [
    {"id": "a1000000-0000-4000-8000-000000000001", "name": "Webhook", "type": "n8n-nodes-base.webhook", "typeVersion": 2.2, "position": [0, 0], "webhookId": "thr", "parameters": {"httpMethod": "GET", "path": "demo/throttled", "responseMode": "responseNode", "options": {}}},
    {"id": "a1000000-0000-4000-8000-000000000002", "name": "Delay", "type": "n8n-nodes-base.wait", "typeVersion": 1.1, "position": [220, 0], "parameters": {"resume": "timeInterval", "amount": 2, "unit": "seconds"}},
    {"id": "a1000000-0000-4000-8000-000000000003", "name": "Respond", "type": "n8n-nodes-base.respondToWebhook", "typeVersion": 1.5, "position": [440, 0], "parameters": {"respondWith": "json", "responseBody": "={\"error\":{\"code\":429,\"message\":\"rate limit exceeded\"}}", "options": {"responseCode": 429, "responseHeaders": {"entries": [{"name": "Retry-After", "value": "3"}, {"name": "X-Mock", "value": "n8n"}]}}}}
  ],
  "connections": {"Webhook": {"main": [[{"node": "Delay", "type": "main", "index": 0}]]}, "Delay": {"main": [[{"node": "Respond", "type": "main", "index": 0}]]}},
  "settings": {"executionOrder": "v1"}
}
```

A stateful example (a data table behind the mock) is in `n8n-data-tables.md`.

## How to check a node's parameters yourself

The image knows every node's real parameters. Dump them once and read the node you need:

```powershell
docker exec n8n n8n export:nodes --output=/tmp/nodes.json     # ~18 MB of node descriptions, inside the container
docker exec n8n sh -c "node -e \"const n=require('/tmp/nodes.json'); const l=Array.isArray(n)?n:Object.values(n); console.log(JSON.stringify(l.filter(x=>x.name==='n8n-nodes-base.webhook').pop().properties.map(p=>p.name+':'+p.type)))\""
```

For documentation and examples use Context7 (`context7-lookup.md`). Docs describe features by n8n release ("available from 2.16.0"); the image is the authority on what *this* instance supports.

## Common mistakes

- A missing `connections` link (the flow silently stops early).
- Re-importing an exported file that still has its `id` (overwrites).
- `typeVersion` older than the parameters you wrote (parameters get ignored or misread).
- Returning an object instead of an array of `{ json }` from a Code node.
- Using `responseMode: lastNode` and expecting a custom status code from a Respond node.
- Expecting the test URL to work from an SDK (it doesn't; use the production URL of a published workflow).

Never put real secrets in this file.

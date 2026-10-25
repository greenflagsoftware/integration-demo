---
description: How to shape an n8n mock to look like a real vendor API: webhook URL rules (static vs parameterised paths and the webhookId prefix), the slash-in-parameter limit, setting the SDK's base URL, auth and token endpoints, error and retry responses, latency, pagination, stateful mocks, and what n8n webhooks cannot do. Read when designing mock endpoints for an SDK or when an SDK call to a mock returns 404 or the wrong shape.
type: rule
---

# Making n8n look like the vendor's API

**Rule:** build each mock endpoint so the SDK's own request lands on it unchanged (same method, path, headers) and the response has the shape the SDK parses. Don't bend the SDK to the mock unless there is no other way.

**Why:** the point of a mock is to exercise real SDK code. Most surprises came from URL routing rules that are not obvious (below); they were all found by testing on n8n 2.44.6.

**When to apply:** step 3 of Frank's process, whenever you design endpoints, and when a call to a mock 404s.

## 1. URL rules (tested)

| Webhook `path` | Production URL |
|---|---|
| static: `demo/items` | `http://localhost:5678/webhook/demo/items` |
| with a parameter: `demo/things/:id` | `http://localhost:5678/webhook/<webhookId>/demo/things/42` |

- The **`webhookId`** is a field on the Webhook node in the JSON. It can be **any string**: a short stable one like `gcs` works. Choose it yourself so the URL doesn't change when the workflow is re-created. **For parameterised paths the webhookId prefix is mandatory** (without it: 404 `not registered`); for static paths it must **not** be there.
- Parameters arrive in the item as `params` (`$input.first().json.params.id`), the query string as `query`, headers as `headers` (lower-case keys), the parsed JSON body as `body`. Several parameters work: `storage/v1/b/:bucket/o/:object`.
- Methods are matched exactly (a `GET` to a `POST` webhook is a 404). Use `multipleMethods` or separate Webhook nodes for the same path with different methods.
- The **test URL** (`/webhook-test/...`) is 404 unless the editor is listening. SDK tests must use the production URL of a published workflow.

### The slash limit (important for Google-style APIs)

**A path parameter cannot contain a `/`, even when the client sends it as `%2F`**: n8n decodes it before matching, so `.../o/folder%2Fa.txt` is a 404 (tested), and wildcard syntax (`:name(*)`, `*`) is not supported (tested). Options:
1. Use object/file names **without slashes** in the SDK tests (simplest, and usually enough).
2. Add extra Webhook nodes to the same workflow with one more parameter each (`.../o/:p1/:p2`, `.../o/:p1/:p2/:p3`) feeding the same logic, and join the parameters in a Code node. (The mechanism is the same as the multi-parameter paths that were tested; this exact arrangement was not.)
3. Tell the user which operations can't be mocked faithfully, as Frank's voice rule says, and suggest option 1 or 2.

## 2. Pointing the SDK at the mock

Use the SDK's endpoint seam (a builder `Endpoint`/`BaseUri`/`BaseAddress`, an emulator setting, a custom `HttpMessageHandler`; the code indexer finds them: `n8n`-side facts here, SDK-side in `code-indexer-sdk-review-recipes.md`). Set it through config or an environment variable in the test, never hard-coded.

- SDK running on the Windows host: base URL `http://localhost:5678/webhook` (+ `/<webhookId>` when the path has parameters).
- SDK running inside another container: `http://host.docker.internal:5678/webhook/...` on Docker Desktop, or `http://n8n:5678/webhook/...` if it's on the same compose network (not tested).
- An SDK that insists on HTTPS or a fixed host name needs the Caddy proxy plus hosts-file entries the user adds: read `caddy-proxy.md`, propose it and **ask before changing anything** (Frank's rule).

## 3. Responses

- **Status code per request:** a Code node returns `{ statusCode, body }`; the Respond node uses `"responseBody": "={{ $json.body }}"` and `"options": { "responseCode": "={{ $json.statusCode }}" }` (tested; example 1 in `n8n-workflow-json.md`).
- **Headers:** `options.responseHeaders = { "entries": [ { "name": "Retry-After", "value": "3" } ] }` (tested). Put `Content-Type`, `ETag`, `Location` the same way if the SDK reads them.
- **Errors in the vendor's own shape:** copy the real error body (Google: `{"error":{"code":404,"message":"...","errors":[...]}}`). Cover the errors the SDK actually handles: 400/401/403/404/409/429/500/503, and for retry logic a failing-then-succeeding sequence (needs state: count calls in a data table row).
- **Latency:** a Wait node (`resume: timeInterval`, `amount`, `unit: seconds`) before the Respond node (tested: 2 s). Use it to exercise client timeouts.
- **Binary bodies, streaming, and chunked/resumable uploads:** the Webhook can receive binary (`options.binaryPropertyName`, POST/PUT/PATCH) and Respond can send a binary file; not tested here. Say so if an SDK operation depends on them, and look up current behaviour with Context7 first.

## 4. Auth and token endpoints

Fake auth is fine and expected in mocks. A token endpoint is just another mock: `POST /webhook/oauth2/token` → Respond with `{"access_token":"fake-token","token_type":"Bearer","expires_in":3600}`. To make protected endpoints check it, read `headers.authorization` in the Code node and return 401 when it isn't `Bearer fake-token` (tested: header present/absent paths). The Webhook node also has built-in header/basic/JWT auth (`authentication` parameter) which needs a credential created in n8n; the Code-node check needs none.

## 5. Pagination and lists

Return the vendor's paging fields (`nextPageToken`, `items`, `kind`) from a Code node. For stable fake data, keep the rows in a data table and slice them: read `query.pageToken`/`query.maxResults`, `get` rows (`returnAll`), and slice in the Code node. (Pattern; the data table list/create mock in `n8n-data-tables.md` is the tested building block.)

## 6. What webhooks can't do

- No wildcard or regex paths, no slashes inside parameters (above).
- A path+method can only be served once, so two published workflows with the same path would clash (the clash itself was not tested). In the sandbox, overwrite the first workflow rather than creating a second one with the same path. Note n8n lets two workflows share a *name* (tested), which is how duplicates sneak in.
- Binary/streaming/very large bodies: untested here.

## 7. Check list before telling the user a mock is ready

Published? n8n restarted after the CLI push? Production URL called with the SDK's real request (method, path, headers)? Success **and** each simulated error returned the right status and body? See `n8n-pushing-and-operating.md` for how to verify.

Never put real secrets in this file; use fake tokens like `fake-token` in mocks.

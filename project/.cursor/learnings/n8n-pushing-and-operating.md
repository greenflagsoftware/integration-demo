---
description: How Frank gets workflows into the sandbox n8n and keeps them working. API-first approach with Context7 lookups. How to activate/deactivate, avoid duplicate workflows, fix table schema mismatches, verify mocks, and export. Read BEFORE pushing, updating, or debugging a workflow.
type: rule
---

# Pushing workflows to n8n and operating them

**Rule:** Use the **n8n Public REST API** as the default push method (header `X-N8N-API-KEY`). It takes effect **immediately** — no restart needed. Use Context7 (`context7-lookup.md`) to look up the correct API endpoints **before** writing code. Fall back to the container CLI only when the API key is unavailable. In the **sandbox** you may overwrite; on **stable/production** never (see `n8n-overview.md`).

**Why (tested 2026-10-25):** The user has an owner account and the API key lives in `E:\Dev\demos\integration-demo\docker\.env` as `N8N_LLM_API_KEY`. The API works, is immediate, and avoids every CLI pitfall: "restart n8n", Git Bash path rewriting, duplicate names, and `n8n execute` not supporting Data table nodes.

**When to apply:** any time you create, update, publish, or test a workflow. Also read this file when you encounter "unknown column name", "workflow activation failed validation", "webhook not registered", or duplicate workflows.

## 1. Before doing anything — Context7 lookups

Always look up n8n API endpoints with Context7 before writing a `curl` command or modifying a workflow:

```powershell
# resolve the library ID
npx ctx7@latest library n8n "n8n REST API"

# then query for the specific endpoint
npx ctx7@latest docs /n8n-io/n8n-docs "REST API activate deactivate workflow endpoint "
# → /api/v1/workflows/{id}/activate and /api/v1/workflows/{id}/deactivate
```

Alternatively use the Context7 MCP tools in the session:
1. `resolve-library-id` with `libraryName: "n8n"` and `query: "n8n REST API"`
2. `query-docs` with that library ID and a specific query (e.g. "workflow endpoints activate deactivate")

## 2. API key

The key is in `E:\Dev\demos\integration-demo\docker\.env` under `N8N_LLM_API_KEY`. Never echo the key value into chat. Reference it from the `.env` file or a variable

Set it once in your shell session:

shell
export N8N_API_KEY="<value from .env>"
```

## 3. API endpoints (tested 2026-10-25 on n8n 2.44.6)

All endpoints use header `X-N8N-API-KEY: $N8N_API_KEY` and base URL `http://localhost:5678/api/v1`

| Action | Method | Endpoint | Notes |
|--------|--------|----------|-------|
| **List** | GET | `/workflows?limit=150` | Returns array in `data` key, each workflow has `id`, `name`, `active` |
| **Create** | POST | `/workflows` | Body: `{ "name": "...", "nodes": [...], "connections": {...}, "settings": {...} }` |
| **Get one** | GET | `/workflows/{id}` | Full workflow with all nodes/connections |
| **Update** | PUT | `/workflows/{id}` | Full replace of the draft version |
| **Activate** | POST | `/workflows/{id}/activate` | Publishes immediately — webhooks go live instantly |
| **Deactivate** | POST | `/workflows/{id}/deactivate` | Unpublishes immediately |
| **Delete** | DELETE | `/workflows/{id}` | Fails if workflow is active — must deactivate first |
| **Health** | GET | `http://localhost:5678/healthz` | Returns `{"status":"ok"}` |

### The activate/deactivate flow (correct procedure)

When updating a workflow, do **not** re-import a file without an `id` — that creates a **second** workflow (duplicate name). Instead:

1. **Deactivate** the old workflow: `POST /workflows/{old_id}/deactivate`
2. **Delete** the old workflow: `DELETE /workflows/{old_id}`
3. **Import** the new file (contain or omit `id` as desired)
4. **Find** the new workflow's `id` from the list
5. **Activate** the new workflow: `POST /workflows/{new_id}/activate`

Or, simpler for the sandbox — **put the `id` in the workflow JSON file** and re-import via the container CLI

### Quick walkthrough (bash)

```bash
# List all workflows
curl -s -H "X-N8N-API-KEY: $N8N_API_KEY" "http://localhost:5678/api/v1/workflows" \
  | python3 -c "import sys,json; [print(f'{w[\"id\"]}|{w[\"name\"]}|active={w[\"active\"]}') for w in json.load(sys.stdin)['data']]"

# Activate a workflow
curl -s -X POST -H "X-N8N-API-KEY: $N8N_API_KEY" "http://localhost:5678/api/v1/workflows/{id}/activate"

# Deactivate a workflow
curl -s -X POST -H "X-N8N-API-KEY: $N8N_API_KEY" "http://localhost:5678/api/v1/workflows/{id}/deactivate"

# Delete after deactivate
curl -s -X DELETE -H "X-N8N-API-KEY: $N8N_API_KEY" "http://localhost:5678/api/v1/workflows/{id}"
```

### Python helper

Save as `scripts/n8n-api.py` and reuse:

```python
#!/usr/bin/env python3
"""Reusable n8n API client. Source: https://docs.n8n.io/api/"""
import subprocess, json, os, sys

API_KEY = os.environ.get("N8N_API_KEY") or open(r"E:\Dev\demos\integration-demo\docker\.env").read().split("N8N_LLM_API_KEY=")[1].splitlines()[0].strip()
BASE = "http://localhost:5678/api/v1"
HEADERS = ["-H", f"X-N8N-API-KEY: {API_KEY}"]

def api(method, path, data=None):
    cmd = ["curl", "-s", "-X", method] + HEADERS + [BASE + path]
    if data:
        cmd += ["-H", "Content-Type: application/json", "-d", json.dumps(data)]
    r = subprocess.run(cmd, capture_output=True, text=True)
    return json.loads(r.stdout)

def list_workflows():
    return api("GET", "/workflows?limit=150")["data"]

def activate(id):
    return api("POST", f"/workflows/{id}/activate")

def deactivate(id):
    return api("POST", f"/workflows/{id}/deactivate")

def delete_workflow(id):
    return api("DELETE", f"/workflows/{id}")

def push_workflow_file(path):
    """Read a workflow JSON file, create it in n8n, activate it, and deactivate/delete any duplicate by name."""
    with open(path) as f:
        wf = json.load(f)
    # Deactivate and delete any existing workflow with the same name
    existing = [w for w in list_workflows() if w["name"] == wf["name"]]
    for w in existing:
        print(f"  Removing old '{wf['name']}' (id {w['id']})...")
        deactivate(w["id"])
        delete_workflow(w["id"])
    # Create the new workflow
    created = api("POST", "/workflows", data={
        "name": wf["name"], "nodes": wf["nodes"],
        "connections": wf["connections"], "settings": wf.get("settings", {"executionOrder": "v1"})
    })
    nid = created["id"]
    print(f"  Created {wf['name']} (id {nid})")
    activate(nid)
    print(f"  Activated.")
    return nid

if __name__ == "__main__":
    for path in sys.argv[1:]:
        print(f"Pushing {path}...")
        push_workflow_file(path)
```

Usage: `python scripts/n8n-api.py n8n/workflows/*.json`

## 4. Avoiding and fixing duplicates

A workflow JSON file **without an `id`** field creates a new workflow every time it's imported. If the name matches an existing workflow, you now have two: `list:workflow | grep "mock-iam-create"` will show two rows.

**How to avoid:** Add the `id` field to your workflow JSON after the first push. Export the workflow back (`n8n export:workflow --id=<id> --output=/tmp/wf.json`) and copy the `id` value into your source file. Then re-importing overwrites.

**How to fix duplicates (API way):**
1. List workflows: `GET /api/v1/workflows?limit=150`
2. For each duplicate, deactivate then delete

## 5. The table schema gotcha (the most common error)

**Error:** `"Validation error with data table request: unknown column name 'createTime'"`

**Root cause:** The Data table node uses `autoMapInputData`, which maps **every field** in the incoming JSON item to a table column by **exact name**. If the item has a field `createTime` but the table only has columns `[nam, uid, etag, ...]`, the insert fails.

**How to fix:**

1. **In the Code node before the insert:** shape the item so it only has fields that match the table's columns. Strip out `createTime`, `updateTime`, `details` — these are not table columns.
2. **In the response formatting Code node:** read from `row.createdAt` (auto-generated by the table) instead of `row.createTime`:

```javascript
// Code node before data table insert — strip non-table fields
const row = {
  name: computedName,
  uid: computedUid,
  etag: computedEtag,
  displayName: computedDisplayName,
  parent: computedParent,
  accessPolicyId: computedId
  // NO createTime, updateTime, details, rules
};
return [{ json: row }];

// Response formatting — use row.createdAt from the auto-generated columns
const ts = row.createdAt || new Date().toISOString();
const response = {
  name: row.name,
  createTime: ts,   // <-- added back for the API response only
  updateTime: ts,
  details: { rules: [] }
};
```

**Table column naming:** Columns accept `^[a-zA-Z][a-zA-Z0-9_]*$` (up to 63 chars). `createTime` is valid but must be defined in the schema. The seed workflow's `Create table` node must list all columns the mock will insert.

### Seed workflow pattern (createIfNotExists won't alter existing tables)

If you change a table's column in the seed workflow JSON, re-running the seed does **not** alter the table because `createIfNotExists: true` skips existing tables (tested 2026-10-25). To update a table schema:

1. Run the seed workflow to **clear** the table
2. **Manually delete the table** via the n8n editor (Data tables tab) before re-running the seed
3. Or, if the seed creates the table, **first delete it through the API** (not tested — alternative: give the new table a different name and update all mocks)

## 6. Git Bash path rewriting (the "ENOENT" trap)

**Error:** `ENOENT: no such file or directory, open 'C:/Users/User/AppData/Local/Temp/n8n-import/file.json'`

**Root cause:** Git Bash automaticly converts `/tmp/...` paths to Windows paths (`C:\Users\...`). This happens when the path appears as a command argument.

**How to fix:**
- **Use `sh -c "..."` wrapping around the full command** — wrap the entire `n8n import` or `ls` command inside `sh -c "..."` so the path is never exposed as a Git Bash argument
- **Use `export MSYS_NO_PATHCONV=1`** before the command (works for individual commands in the same shell)
- **Use `docker cp`** to copy files using host paths (the source path is a Windows path, which is correct for `docker cp`)
- **Prefer the REST API** — curl paths don't trigger path rewriting

Examples:

```bash
# DOES NOT WORK in Git Bash (path gets rewritten)
docker exec n8n n8n import:workflow --input=/tmp/n8n-import/wf.json

# WORKS — wrap in sh -c
docker exec n8n sh -c "n8n import:workflow --input=/tmp/n8n-import/wf.json"

# WORKS — set env var
export MSYS_NO_PATHCONV=1
docker exec n8n n8n import:workflow --input=/tmp/n8n-import/wf.json

# WORKS — use the API instead
curl -s -X POST -H "X-N8N-API-KEY: $KEY" -d @wf.json http://localhost:5678/api/v1/workflows
```

## 7. Verify a mock (always, and say what you saw)

```bash
# Health
curl -s http://localhost:5678/healthz                       # {"status":"ok"}

# List workflows (active)
curl -s -H "X-N8N-API-KEY: $N8N_API_KEY" "http://localhost:5678/api/v1/workflows?active=true&limit=50" \
  | python3 -c "import sys,json; [print(f'{w[\"name\"]} active={w[\"active\"]}') for w in json.load(sys.stdin)['data']]"

# Test the mock's production URL
curl -s -X POST "http://localhost:5678/webhook/mock-iam/accessPolicies?accessPolicyId=test-1" \
  -H "Content-Type: application/json" \
  -H "x-goog-request-params: parent=projects/my-project/locations/us-central1" \
  -d '{"displayName":"Test"}'

# Check error paths (400, 404, 429) too
# Check logs for errors
docker logs n8n --tail 30 2>&1 | grep -i "error\|fail\|invalid\|unknown column\|workflow\|webhook\"
```

A `404 ... is not registered` means: workflow is not active, or (for parameterised paths) the `webhookId` prefix is missing. See `n8n-mock-api-recipes.md`. Unknown-webhook hits appear in the logs.

## 8. Export for version control (CLI only)

```powershell
docker exec n8n n8n export:workflow --id=<id> --output=/tmp/wf.json
docker cp n8n:/tmp/wf.json .\8n\workflows\mock-<sd>-<operation>.json
```

An export carries `id`, `versionId`, `active`, `shared`, and more. If you keep the `id` in the committed file, it **overwrites** that workflow on import (good for sandbox updates, bad for prod). For committed files in this repo, consider keeping the `id` since this is the sandbox.

## 9. CLI facts and pitfalls (run into frequently)

- **API changes are immediate.** Container CLI (`import:workflow`) changes require publish + restart.
- **Names are not unique.** Importing the same name again creates a second workflow.
- **`createIfNotExists: true`** will not alter existing table columns (tested). To change columns, delete the table first (via editor) or change the table name.
- **`n8n execute --id=<id>`** cannot run Data table nodes ("module is disabled"). Run through a webhook.
- **Restarting n8n** interrupts the editor. Warn the user if you do it.

Never put real secrets in this file or in chat.
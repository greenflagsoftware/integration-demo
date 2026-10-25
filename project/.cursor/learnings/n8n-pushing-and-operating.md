---
description: How Frank gets workflows into the sandbox n8n and keeps them working: the options (container CLI, public API, editor), tested PowerShell helpers to push, overwrite by id, publish and restart, how to verify a mock, exporting for version control, and the CLI and Windows/Linux pitfalls (restart required, duplicate names, ids overwrite, execute limits, Git Bash path rewriting). Read before pushing, updating, or debugging a workflow in n8n.
type: rule
---

# Pushing workflows to n8n and operating them

**Rule:** ask the user how they want workflows pushed (Frank's process, step 4), recommend the container CLI below unless they have an API key ready, follow their answer, and then save what they decided here or in `n8n-overview.md`. In the **sandbox** you may overwrite; on **stable/production** never (see `n8n-overview.md`).

**Why:** the sandbox has no API key until the user creates an owner account, but the container CLI works without one. Its quirks (restart needed, silent duplicates, silent overwrite by id) are easy to trip on.

**When to apply:** any time you create, update, publish, or test a workflow on a real n8n instance.

## Options for getting a workflow in

| Option | Needs | Live immediately? | Status |
|---|---|---|---|
| **Container CLI** (`docker exec n8n n8n import:workflow`, `publish:workflow`) | Docker only (no account, no key) | **No: restart n8n** | **Tested** on 2.44.6; recommended default |
| **Public REST API** (`POST /api/v1/workflows`, header `X-N8N-API-KEY`) | Owner account + API key (Settings > n8n API, created in the editor by the user) | Yes | Docs only |
| **Editor** (paste JSON / Import from file) | The user at the keyboard | Yes | Always works; the user does it |
| **Instance MCP server** (n8n 2.x exposes tools such as `publish_workflow`, `create_data_table`) | MCP enabled and a token | Yes | Docs only (from 2.12/2.16) |

Never ask the user to paste an API key into chat. If they create one, it goes in the git-ignored `.env` or an environment variable, and you don't echo it.

## Tested PowerShell helpers (CLI route)

These ran end to end against n8n 2.44.6: create, refuse a duplicate name, overwrite by name keeping the same id, publish, restart, call the webhook. Paste them into a script or the session.

```powershell
function Get-N8nWorkflows {
    # "id|name" lines from the container, minus the license/runner noise n8n prints first.
    param([string]$Container = 'n8n')
    docker exec $Container n8n list:workflow 2>&1 |
        Where-Object { $_ -match '^[A-Za-z0-9]{8,}\|' } |
        ForEach-Object { $id, $name = $_ -split '\|', 2; [pscustomobject]@{ Id = $id; Name = $name } }
}

function Push-N8nWorkflow {
    # Imports one workflow file. Does NOT make it live: use -Publish, then Restart-N8n.
    #  no "id" in the file -> creates (refuses if the name already exists, because n8n allows duplicate names)
    #  -ReplaceByName      -> overwrites the one existing workflow with that name (SANDBOX ONLY)
    #  "id" in the file    -> overwrites the workflow with that id
    param([Parameter(Mandatory)][string]$Path, [string]$Container = 'n8n', [switch]$ReplaceByName, [switch]$Publish)
    $wf = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    if ($wf -is [array]) { throw 'Give one workflow per file (not an array).' }
    $existing = @(Get-N8nWorkflows -Container $Container)
    $sameName = @($existing | Where-Object Name -eq $wf.name)
    if ($wf.ContainsKey('id') -and $wf.id) {
        $action = if ($existing.Id -contains $wf.id) { "overwrite id $($wf.id)" } else { 'create (id not present yet)' }
    }
    elseif ($sameName.Count -eq 0) { $action = 'create' }
    elseif ($ReplaceByName -and $sameName.Count -eq 1) { $wf.id = $sameName[0].Id; $action = "overwrite id $($wf.id) (same name)" }
    elseif ($ReplaceByName) { throw "Several workflows are named '$($wf.name)' ($($sameName.Id -join ', ')). Pass a file with the id you mean." }
    else { throw "A workflow named '$($wf.name)' already exists (id $($sameName[0].Id)). Use -ReplaceByName to overwrite it (sandbox only) or choose another name." }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) "n8n-import-$([guid]::NewGuid().ToString('N')).json"
    try {
        ($wf | ConvertTo-Json -Depth 100) | Set-Content -LiteralPath $tmp -Encoding utf8
        docker cp $tmp "${Container}:/tmp/wf-import.json" | Out-Null
        $out = docker exec $Container n8n import:workflow --input=/tmp/wf-import.json 2>&1
        if (-not ($out -match 'Successfully imported')) { throw "Import failed: $($out -join "`n")" }
    }
    finally { Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue }

    $id = (Get-N8nWorkflows -Container $Container | Where-Object Name -eq $wf.name | Select-Object -First 1).Id
    if ($Publish) { docker exec $Container n8n publish:workflow --id=$id 2>&1 | Where-Object { $_ -match 'Publishing' } | Out-Host }
    Write-Host "$action -> id $id"
    $id
}

function Restart-N8n {
    # Needed after CLI publish. Waits until /healthz answers. This restarts the user's sandbox n8n: mention it.
    param([string]$Container = 'n8n', [int]$Port = 5678, [int]$TimeoutSeconds = 90)
    docker restart $Container | Out-Null
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try { if ((Invoke-WebRequest "http://localhost:$Port/healthz" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { Start-Sleep 3; return } } catch { }
        Start-Sleep 2
    }
    throw "n8n did not become healthy within $TimeoutSeconds seconds."
}
```

Typical session (push everything in the repo folder, then go live once):

```powershell
Get-ChildItem .\n8n\workflows\mock-*.json | ForEach-Object { Push-N8nWorkflow -Path $_.FullName -ReplaceByName -Publish }
Restart-N8n
```

Run any setup/seed workflow after the restart (`Invoke-RestMethod -Method Post http://localhost:5678/webhook/seed/<sdk>`), because a seed workflow only answers once published and the server is back up.

## Verify a mock (always, and say what you saw)

```powershell
Get-N8nWorkflows | Format-Table                                    # exists, exactly once?
(Invoke-WebRequest http://localhost:5678/healthz -UseBasicParsing).Content   # {"status":"ok"}
$r = Invoke-WebRequest -Uri http://localhost:5678/webhook/demo/items -Method Post -ContentType 'application/json' -Body '{"name":"x"}' -SkipHttpErrorCheck
"$($r.StatusCode) $($r.Content)"                                   # the status and body the SDK will see
```

Call the **error** paths too (`-Body '{}'` for the 400 above). `-SkipHttpErrorCheck` stops PowerShell throwing on 4xx/5xx so you can read the body. A `404 ... is not registered` means: not published, n8n not restarted since publish, wrong method, or (for parameterised paths) the webhookId prefix is missing: `n8n-mock-api-recipes.md`. Unknown-webhook hits are logged: `docker logs n8n --tail 30`. Executions of production webhooks appear only in the editor's Executions list.

## Export for version control

```powershell
docker exec n8n n8n export:workflow --id=<id> --output=/tmp/wf.json     # Linux path inside the container
docker cp n8n:/tmp/wf.json .\n8n\workflows\mock-<sdk>-<operation>.json   # Windows path outside
```

An export carries `id`, `versionId`, `active`, `shared`, and more. It re-imports fine. **Re-importing a file that has an `id` overwrites that workflow**, which is what you want to update the sandbox and a hazard anywhere else. For files you commit, either keep the `id` (updates are exact) or remove it and rely on `-ReplaceByName`. Say which you chose.

## CLI facts and pitfalls (all tested on 2.44.6 unless noted)

- **Not live until published *and* n8n restarted** ("Changes will not take effect if n8n is running" is printed). Editor and API changes are immediate.
- `n8n list:workflow` prints `id|name`. **Names are not unique**: importing the same name again silently makes a second workflow.
- `n8n import:workflow --input=<file>`: no account needed, no `id` in the file means a new workflow with a new id; `--activeState=fromJson` respects an `active` field (default deactivates).
- `update:workflow --active` is **deprecated**: use `publish:workflow --id=<id>` and `unpublish:workflow --id=<id>`.
- **`n8n execute --id=<id>` conflicts with the running server** (task broker port 5679 in use). Give it another one: `docker exec -e N8N_RUNNERS_BROKER_PORT=5680 n8n n8n execute --id=<id>`. It can run Code and other plain nodes, but **it cannot run Data table nodes** ("module is disabled"). Run those through a webhook.
- Inside the container everything is Linux: forward slashes, `/tmp`, `/home/node`. `docker cp` and `docker exec` are called from Windows PowerShell with Windows paths on the host side only.
- **Git Bash rewrites container paths** (`/tmp/x` becomes `C:/Users/...`). Frank uses PowerShell, where this doesn't happen; if you must use Git Bash, set `MSYS_NO_PATHCONV=1`.
- `n8n export:nodes --output=/tmp/nodes.json` lists every node type and its parameters (see `n8n-workflow-json.md`).
- Restarting n8n interrupts anything the user is doing in the editor. Tell them you're about to.

## Public API (docs only, untested here)

Header `X-N8N-API-KEY: <key>` on every call. `POST /api/v1/workflows` creates (body needs `name`, `nodes`, `connections`, `settings`; optional `Idempotency-Key` header makes retries safe), `GET /api/v1/workflows?active=true&limit=150` lists. The endpoints to publish a workflow and to manage data tables differ between n8n versions: look them up with Context7 for the version in use (`context7-lookup.md`) before writing code against them.

Never put real secrets (API keys) in this file or in chat.

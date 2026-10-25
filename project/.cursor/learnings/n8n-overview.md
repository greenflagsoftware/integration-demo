---
description: How n8n fits Frank's work (workflows as mock APIs, data tables as the fake datastore), the sandbox instance (Docker container `n8n` at http://localhost:5678, version 2.44.6), the sandbox vs production rules, the workflow lifecycle (draft, publish, restart), and which n8n learning to read next. Read first for any n8n task, or when asked what n8n can do or how Frank's mocks work.
type: reference
---

# n8n for Frank: the big picture

## What Frank uses n8n for

An SDK talks to a vendor's HTTP or gRPC service. Frank replaces the service with **n8n workflows**:

- A **Webhook** node is the endpoint (method + path).
- Nodes in between (Code, IF/Switch, Set, Wait, Data table) decide what the fake service answers.
- A **Respond to Webhook** node sends the status code, headers, and JSON body the SDK expects.
- A **data table** holds state when the fake service needs to remember things (created objects, tokens, counters).

Then the SDK under test is pointed at `http://localhost:5678/webhook/...` and Frank's unit tests run against it.

## The sandbox instance (where Frank builds)

| | |
|---|---|
| What | The user's own local Docker n8n. This is the **sandbox**. |
| Address | `http://localhost:5678` (editor and webhooks). Health: `http://localhost:5678/healthz` returns `{"status":"ok"}`. |
| Container | `n8n` (Linux). Data lives in the `n8n_data` volume, so it survives `docker restart n8n` and `docker compose stop`. |
| Defined in | `E:\Dev\demos\integration-demo\docker\docker-compose.yml` (+ `E:\Dev\demos\integration-demo\docker\.env`, git-ignored). This is a **community edition** stack with Caddy on 80/443, SearXNG for AI search, and sandbox runners for AI code execution. |
| Start / stop | From that folder: `docker compose up -d n8n` and `docker compose stop`. Starting `n8n` also starts the sandbox services it depends on. `caddy` (a public-facing proxy), `searxng`, and `sandbox-runner-1` are separate and are not started with it. |
| Caddy proxy | Defined in `E:\Dev\demos\integration-demo\docker\Caddyfile`. Caddy listens on 80/443 and proxies only public paths (`/webhook/*`, `/webhook-waiting/*`, `/rest/oauth2-credential/callback`) to `n8n:5678`. Everything else gets 404. `$PUBLIC_DOMAIN` comes from the `.env` file (defaults to `localhost`). |
| `.env` vars | `GENERIC_TIMEZONE=UTC`, `N8N_SANDBOX_VERSION=1.8.1`, plus AI sandbox secrets and SearXNG config. |
| Version | 2.44.6 when this was written. Check: `docker exec n8n n8n --version`. n8n 2.x talks about **publishing** a workflow (older docs say "activating"). |
| Task runner | Code nodes run JavaScript. The image has **no Python**, so write Code nodes in JavaScript. |

The owner account **has been set up** and an n8n API key is available in `E:\Dev\demos\integration-demo\docker\.env` as `N8N_LLM_API_KEY` (tested 2026-10-25). Always prefer the **Public REST API** over the container CLI — it takes effect immediately and avoids all CLI pitfalls (see `n8n-pushing-and-operating.md`). Do not echo the key into chat.

## Sandbox vs stable/production (the rules)

- **Sandbox** (this instance): Frank may **overwrite** workflows and data tables freely. Updating a `mock-*` workflow in place is normal, because APIs take many iterations. Overwrite by workflow **id**, not name (names are not unique), and say what you replaced. Don't *delete* workflows or tables unless asked.
- **Stable / production** (any other n8n the user designates): never overwrite or delete there. Don't push at all unless the user expressly asks, then create new workflows only, after a name check.
- Not sure which instance something is? Ask before pushing.

## Lifecycle, in one paragraph

A workflow you import or save is a **draft**. Only a **published** workflow answers its **production webhook URL** (`/webhook/...`). The test URL (`/webhook-test/...`) works only while the editor is listening for a test event, so it is useless for SDK tests. When you change workflows through the **container CLI** (the way Frank pushes without an API key), publishing takes effect **only after n8n restarts**. Changes made in the editor or through the public API take effect immediately. Details and a tested PowerShell helper: `n8n-pushing-and-operating.md`.

## Where Frank's files go

- Workflow JSON (one file per workflow): `n8n\workflows\<name>.json`
- Data table definitions and seed data: `n8n\tables\`
- Name workflows `mock-<sdk>-<operation>`. Seed or setup workflows: `seed-<sdk>-<what>`.
- Create the folders if missing, say so, and commit only when the user asks. No real secrets in any of it; fake tokens and client IDs in mocks are fine.

## Which learning to read next

- Writing or editing a workflow's JSON, node parameters, expressions: `n8n-workflow-json.md`
- Fake datastore, seed data, table operations: `n8n-data-tables.md`
- Making an SDK-shaped API (paths, params, auth, errors, pagination, latency): `n8n-mock-api-recipes.md`
- Getting workflows into the instance, restarting, verifying, CLI pitfalls: `n8n-pushing-and-operating.md`
- Serving a mock at the SDK's real host name (Caddy proxy, hosts entries, trusting its CA): `caddy-proxy.md`
- Looking up anything else in n8n's documentation: `context7-lookup.md`

Most of what these learnings say was **tested on n8n 2.44.6** on 2026-10-25 and is marked as such; anything taken only from documentation is marked "docs only".

Never put real secrets in this file.

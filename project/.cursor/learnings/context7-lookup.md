---
description: How to look things up with Context7 (the CLI `npx ctx7@latest library|docs`, or the Context7 MCP tools when the session has them): the library IDs that work for n8n, how to phrase queries, limits, how to confirm an answer against the running n8n version, and the status of the CLI on this machine. Read before looking up documentation for n8n or any SDK, library, or framework.
type: rule
---

# Looking things up with Context7

**Rule:** for any library, SDK, framework, or n8n behaviour you are not certain about, look it up in Context7 before relying on memory, then **confirm against the thing you are actually running** when it matters. Save reusable results as learnings.

**Why:** n8n changes fast (the sandbox is 2.44.6; features arrive by release, for example data table tools "from n8n 2.16.0"), and SDK APIs differ by version. Documentation can describe a newer or older release than the one in use.

**When to apply:** API shapes, node parameters, auth flows, version-specific behaviour, anything that would otherwise be a guess.

## Two ways in

1. **The CLI** (Frank's tool). Per Context7's own documentation:
   ```powershell
   npx ctx7@latest library n8n "Webhook node response modes"     # find the library id (ranked by your query)
   npx ctx7@latest docs /n8n-io/n8n-docs "Respond to Webhook node options"   # fetch docs for that id
   ```
   `ctx7 login` (browser OAuth) raises rate limits and is optional; `ctx7 whoami` shows the login. **Status on this machine (checked 2026-10-25): `ctx7` is not installed globally, and these `npx` commands have not been run here.** Node 22 and npm are present. If `npx` fails (no network, blocked), say so plainly, then use option 2 or the n8n image itself (below). Don't install anything globally without asking.
2. **Context7 MCP tools**, when the session lists them (`resolve-library-id`, `query-docs`). They do the same two steps. This is how the n8n facts in these learnings were gathered.

If you work out a better invocation for this environment (a different command name, a flag that matters), update this file.

## Library ids that work for n8n

Skip the resolve step with these:

| Id | Use it for |
|---|---|
| `/n8n-io/n8n-docs` | **Default.** The official n8n docs source: node pages (Webhook, Respond to Webhook, Data table, Code), CLI and API reference. |
| `/websites/n8n_io` | The published docs site; more snippets, same content, useful when the default has a gap. |
| `/llmstxt/n8n_io_llms_txt` | The `llms.txt` index of the docs. |

For other libraries use `library <official name with proper punctuation> "<what you want to do>"` (for example `Next.js`, not `nextjs`) and prefer results with High reputation and many snippets.

## Asking well

- **One concept per query**, specific, with the node or class name: "Respond to Webhook node: response code and response headers options" beats "webhooks".
- Mention the version when it matters ("n8n 2.x").
- Roughly **three queries per question**; if three don't answer it, use the best you have and say what's uncertain.
- Never put secrets or proprietary code in a query: queries are sent to the Context7 service.
- Queries that worked well while building the n8n learnings: Webhook response modes and test vs production URLs; Respond to Webhook options; data tables (create, columns, row operations, upsert); public API authentication and workflow endpoints; the n8n CLI import/export commands.

## Confirm against what you are running

Docs are written for a release. Before you depend on a detail:
- **Node parameters and versions:** read them from the image (`docker exec n8n n8n export:nodes --output=/tmp/nodes.json`, then inspect; see `n8n-workflow-json.md`). That is authoritative for the running instance.
- **CLI commands:** `docker exec n8n n8n <command> --help`.
- **Behaviour:** try it on a throwaway container (`docker run --rm -p 5679:5678 n8nio/n8n:latest`), not on the user's sandbox, when it could change data.
- Things the docs implied but testing contradicted or refined (for example the webhookId prefix rule and the slash-in-parameter limit) are recorded in `n8n-mock-api-recipes.md`. Prefer a tested learning over the docs when they disagree, and note the version.

## Not what Context7 is for

Code in the user's SDK checkout (use the code indexer: `code-indexer-overview.md`), their data, or their instance's current state (ask n8n itself).

Never put real secrets in this file.

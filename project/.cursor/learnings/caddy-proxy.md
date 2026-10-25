---
description: What the Caddy proxy in the Docker deploy is for (making n8n webhooks answer at the SDK's real host names and paths for a high-fidelity mock), how to add a host or URL to the Caddyfile, validate and reload it, start it, and which one-time steps the USER must do (hosts-file entries, trusting Caddy's local CA, freeing ports 80/443). Read when an SDK can't be pointed at http://localhost:5678, insists on a fixed host or HTTPS, or the user mentions Caddy, a proxy, or hosts entries.
type: rule
---

# Caddy: making n8n look like the real SDK host

**Rule:** when an SDK can't be configured to call `http://localhost:5678/webhook/...`, don't change the SDK. Put the vendor's real host name in front of the mock instead: the user maps the host name to this machine (hosts file), Caddy answers on 443 for that host with its own certificate, rewrites the path onto the n8n webhook, and forwards to n8n. Use it only when needed; if the SDK has an endpoint setting, setting it is simpler (`n8n-mock-api-recipes.md`).

**Why:** SDKs often hard-code `https://storage.googleapis.com`, `https://oauth2.googleapis.com`, and so on, and call the vendor's paths (`/storage/v1/b/...`), while n8n only serves `/webhook/<path>` (plus `/<webhookId>/` for parameterised paths). The proxy bridges the gap, so the SDK's own code runs unmodified.

**When to apply:** the endpoint-seam review (Frank's step 2) finds no usable base-URL setting, or the SDK validates the host, or the user asks for a high-fidelity mock.

## How it fits together

```
SDK  ->  https://storage.googleapis.com/storage/v1/b/x   (hosts file: that name -> 127.0.0.1)
     ->  Caddy (container `caddy`, host ports 80/443)    (TLS from Caddy's local CA)
     ->  rewrite to /webhook/gcs/storage/v1/b/x           (gcs = the Webhook node's webhookId)
     ->  n8n (container `n8n`, service name `n8n:5678`)   -> the mock workflow
```

- Files: `docker\Caddyfile` (config, mounted read-only into the container) and `docker\docker-compose.yml` (service `caddy`, image `caddy:2`, ports 80, 443, 443/udp, volumes `caddy_data` and `caddy_config`). The compose file sets `PUBLIC_DOMAIN` (default `localhost`) for the first, public site block.
- **n8n stays directly reachable on `http://localhost:5678`** (the user wants that: not all work goes through the proxy). Never remove that port mapping.
- Caddy is **not started** by `docker compose up -d n8n`. Start it with `docker compose up -d caddy` from `E:\Dev\demos\integration-demo\docker`. Status on this machine when written: the `caddy` container had never been started, and the Caddyfile has only the generic `{$PUBLIC_DOMAIN}` block (it forwards `/webhook/*`, `/webhook-waiting/*` and the OAuth callback, and answers everything else 404).

## Add a host (a Caddyfile site block)

Add one block per vendor host name, below the existing block:

```caddyfile
storage.googleapis.com {
	tls internal
	rewrite * /webhook/gcs{uri}
	reverse_proxy n8n:5678
}
```

- `tls internal` makes Caddy issue the certificate from its own local CA. Without it Caddy tries to get a public certificate for a name it doesn't own and fails.
- `rewrite * /webhook/gcs{uri}` prepends the n8n prefix and keeps the original path and query string. **`gcs` must equal the `webhookId` on the Webhook nodes** of that SDK's mocks (the webhookId rule in `n8n-mock-api-recipes.md`). Pick one webhookId per vendor host and use it on every parameterised webhook for that host. For static paths (no `:param`) n8n wants no prefix, so a host with only static paths can use `rewrite * /webhook{uri}` instead; mixing both on one host needs a `handle` per path or all-parameterised webhooks, so say which you chose.
- One block per host name (`oauth2.googleapis.com`, `www.googleapis.com`, and so on). The URL's **path** must match the workflow's Webhook `path` exactly; the same slash and wildcard limits apply as in `n8n-mock-api-recipes.md`.
- Inside the Docker network the target is the service name `n8n:5678`, not `localhost`.
- Keep the existing `{$PUBLIC_DOMAIN}` block as it is.

## Apply the change

Edit `docker\Caddyfile`, then (PowerShell; the container paths are Linux paths):

```powershell
docker exec caddy caddy validate --config /etc/caddy/Caddyfile   # must say: Valid configuration
docker exec caddy caddy reload   --config /etc/caddy/Caddyfile   # applies without dropping connections
```

If the reload doesn't pick up your edit (some editors replace the file, and Windows single-file bind mounts then show the old contents), `docker exec caddy cat /etc/caddy/Caddyfile` to check, and run `docker restart caddy` (tell the user). Changing ports or `environment:` in the compose file needs `docker compose up -d caddy`, which recreates the container.

## Tested (2026-10-25, scratch containers, Caddy 2 and n8n 2.44.6, host port 8443)

- A site block with `tls internal` plus `rewrite * /webhook/gcs{uri}` routed `https://storage.googleapis.com/storage/v1/b/mybucket?fields=name` to a parameterised mock and returned its JSON with 200: path, parameter and query string all arrived.
- `caddy validate` and `caddy reload` worked, and a second host block added afterwards routed immediately (it reached n8n at the rewritten path).
- With Caddy's root certificate as the trusted CA, curl accepted the certificate. On Windows curl also needs `--ssl-no-revoke` (the local CA has no revocation endpoint); other clients may not care.

Not tested: gRPC and HTTP/2-only clients (n8n webhooks speak plain HTTP request/response, so gRPC services can't be mocked this way), binding real ports 80/443 on this machine, and a hosts-file change (those are the user's steps).

## What the USER has to do (Frank asks, doesn't do)

These touch system settings or need admin rights, so Frank tells the user exactly what to do and waits:

1. **Free ports 80 and 443** on the host (another web server or IIS would clash). Check: `Get-NetTCPConnection -LocalPort 80,443 -State Listen -ErrorAction SilentlyContinue`.
2. **Hosts-file entries**, in an elevated editor: `C:\Windows\System32\drivers\etc\hosts`, one line per vendor host:
   ```
   127.0.0.1  storage.googleapis.com
   127.0.0.1  oauth2.googleapis.com
   ```
   Give them the exact list, derived from the host names the SDK calls. **Warn them:** while an entry exists, *every* program on the machine reaching that host (including the real Google client libraries, gcloud, a browser) hits the mock. Offer to give them the removal steps when the work is done. The hosts file applies on the Windows host only: an SDK test running inside another container doesn't see it (use the container's `extra_hosts`/`--add-host` or the same compose network, and ask).
3. **Trust Caddy's local CA** so the SDK accepts the certificate. After Caddy has started once, Frank can export the public root certificate (it is not a secret, but the *private* key in the same volume is, so copy only `root.crt`):
   ```powershell
   docker cp caddy:/data/caddy/pki/authorities/local/root.crt .\caddy-root.crt
   ```
   Then the user imports it into their trusted root store (for example `Import-Certificate -FilePath .\caddy-root.crt -CertStoreLocation Cert:\CurrentUser\Root`, which shows a Windows confirmation). Trusting a root CA is a security decision, so Frank never does it silently: explain that this lets Caddy's CA vouch for any host name on this machine, and that deleting the `caddy_data` volume makes a new CA that must be trusted again. Different runtimes use different stores: .NET and Go on Windows use the Windows store; Python wants `SSL_CERT_FILE` or `REQUESTS_CA_BUNDLE`; Node wants `NODE_EXTRA_CA_CERTS`; Java needs the file imported with `keytool`. For a test that must not depend on machine trust, the cleanest option is for the test to trust only that one certificate file (ask before changing test code).

## Frank's checklist for a proxied mock

1. Confirm the SDK really has no endpoint setting (code indexer, `code-indexer-sdk-review-recipes.md`), and list every host name it calls.
2. Propose the plan to the user: Caddyfile blocks, webhookId per host, hosts lines, the trust step. Get an OK before editing `docker\Caddyfile`.
3. Build the mocks with the matching `webhookId` and exact paths; push and publish them (`n8n-pushing-and-operating.md`).
4. Edit the Caddyfile, validate, reload (start Caddy if it isn't running). Ask the user to do their steps.
5. Verify through the proxy with the real host name, and say what you saw:
   ```powershell
   Invoke-WebRequest https://storage.googleapis.com/storage/v1/b/mybucket -SkipHttpErrorCheck
   ```
   If this fails to connect, the hosts entry or ports are the cause; a certificate error means the CA isn't trusted yet; a 404 JSON from n8n means the rewrite reached n8n but the path, method, or webhookId doesn't match a published workflow; a plain "Not found" from Caddy means the request matched no `handle` (the original public block).
6. Save anything new about this setup here.

Never put real secrets in this file (the CA's private key and any real tokens stay out of chat and the repo).

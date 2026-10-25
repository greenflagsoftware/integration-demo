---
description: How to mock a gRPC-based Google Cloud .NET SDK client with n8n webhooks by switching the client's transport to REST via RestGrpcAdapter.Default. Covers builder setup, endpoint, ChannelCredentials, URL patterns discovered experimentally for AccessPoliciesClient, and what the SDK sends on the wire. Read when mocking a gRPC-based Google.Cloud.* client.
type: rule
---

# Mocking gRPC clients via REST transport

## The pattern

Google Cloud .NET SDK clients that use gRPC (`transport: "grpc"` or `transport: "grpc+rest"` in `apis.json`) can be switched to HTTP/1.1 JSON REST by setting `GrpcAdapter = RestGrpcAdapter.Default` on the client builder. This lets you mock them with n8n webhooks through Caddy.

## Verified builder pattern (IAM V3)

```csharp
using Google.Api.Gax.Grpc;
using Google.Api.Gax.Grpc.Rest;
using Grpc.Core;

var client = new AccessPoliciesClientBuilder
{
    GrpcAdapter = RestGrpcAdapter.Default,
    Endpoint = "http://iam.googleapis.com",
    ChannelCredentials = ChannelCredentials.Insecure
}.Build();
```

**Key points:**
- `ChannelCredentials.Insecure` is **required** for plain HTTP (no TLS). Without it the client tries to load
  application default credentials and fails.
- `Endpoint` must include the scheme (`http://...`). Port is required when non-standard.
- `RestGrpcAdapter.Default` switches the transport from gRPC protobuf to REST JSON. It uses the HTTP
  annotations from the proto definition to determine URL patterns.

## Verified URL patterns (from AccessPoliciesClient experiment)

### CreateAccessPolicy
```
POST /v3/{parent}/accessPolicies?accessPolicyId={id}&$alt=json%3Benum-encoding%3Dint HTTP/1.1
Content-Type: application/json; charset=utf-8
x-goog-request-params: parent={url-encoded-parent}

{ "displayName": "Test Policy" }
```
- `accessPolicyId` is a **query parameter**, not in the body
- The body is the resource message serialized as JSON (only populated fields)
- The `$alt=json;enum-encoding=int` query param is always appended by the SDK — n8n should ignore it

### ListAccessPolicies
```
GET /v3/{parent}/accessPolicies?pageSize={n}&pageToken={t}&$alt=json%3Benum-encoding%3Dint HTTP/1.1
x-goog-request-params: parent={url-encoded-parent}
```
- `pageSize` and `pageToken` are query parameters
- The call is **lazy** — the HTTP request fires only when you `foreach`/`.ToList()` the result

### Common to all
- `x-goog-request-params` header carries the resource name URL-encoded
- `x-goog-apiclient` header identifies the SDK version
- URL path follows the proto HTTP annotation: `/v3/{parent}/accessPolicies`

## When to use this pattern

- The SDK client is **gRPC-based** (most `Google.Cloud.*` packages)
- The client's builder inherits from `ClientBuilderBase<T>` (has `GrpcAdapter` property)
- You want to mock with **n8n webhooks** and a Caddy proxy

## What's still unknown

- **LRO polling path**: after `CreateAccessPolicy` returns an Operation, the SDK polls `GET /v3/{operationName}`.
  The operation name format hasn't been observed yet.
- Whether every gRPC service supports REST transport equally — some may not have HTTP annotations.

## Reference

- Experiment code: `E:\Dev\demos\g-api\experiments\iam-v3-rest-test\Program.cs`
- Full plan: `docs\mock-iam-v3-access-policies-plan.md`

Never put real secrets in this file.
---
description: How Frank uses `codeindex` to review a .NET SDK for n8n mocking: list the remote operations (REST via Google.Apis or HttpClient, gRPC), trace each to the public SDK method, find the endpoint/emulator seams, and what the index cannot see (string literals, env vars, config). Read when reviewing .NET SDK code, building the SDK call to mock workflow table, or looking for where an SDK can be pointed at a mock.
type: rule
---

# Reviewing a .NET SDK with `codeindex`

**Rule:** for a .NET SDK, enumerate remote operations and trace callers with `codeindex` instead of reading file by file or
grepping. Then open only the lines it cites, and read them before you report.

**Why:** an SDK like Google.Cloud.Storage has 25 partial-class files and hundreds of generated methods. Grep conflates
overloads and misses calls made through interfaces; the index resolves them exactly and gives `file:line` for each. It
finds the lines; it does not replace reading them.

**When to apply:** step 1 (Review the SDK code) and step 2 (Find how the SDK reaches the mock) of Frank's process, for C#
code. Not for other languages. Check `code-indexer-overview.md` for setup and `code-indexer-cli-reference.md` for syntax.

## 0. Set up (once per package, then refresh)

```powershell
codeindex index <path>\apis\<Pkg>\<Pkg>.sln --db E:\Dev\demos\g-api\.codeindex\<pkg>.db   # needs a restored solution
codeindex info --db E:\Dev\demos\g-api\.codeindex\<pkg>.db                                 # read the warnings
```

Read `info` first. `not restored` or compile-error warnings mean parts of the code were not understood; **ask the user before
running `dotnet restore`** in their SDK checkout (it writes `obj\` folders and may download packages). Note the target
framework it chose per project: for a `netstandard2.0;net462` project it indexes `netstandard2.0`, so code under
`#if NET462` is not in the index.

## 1. Map the public surface

```powershell
codeindex find-symbol StorageClient --kind class --include-generated   # the client types (get ids)
codeindex outline StorageClient                                        # members of the hand-written/abstract client
codeindex implementations --id <client id>                             # the concrete implementation (e.g. StorageClientImpl)
```

For a partial class `definition` shows every part; `outline <file>` shows just that part.

## 2. Find the remote operations

The idea: every remote call goes through one or two low-level primitives. Find who calls them.

**REST wrappers over Google.Apis** (Storage, BigQuery, ...). Each library method that builds a request ends in
`ClientServiceRequest<T>.Execute` or `.ExecuteAsync`:

```powershell
codeindex callers ClientServiceRequest.Execute --db <db> --format table        # one row per method that performs a REST call
codeindex callers ClientServiceRequest.ExecuteAsync --db <db>                  # two overloads: it returns candidates, so repeat with each --id
```

Verified on Google.Cloud.Storage.V1: 28 methods call `Execute` and 27 call `ExecuteAsync`, all in `StorageClientImpl`; each result lists its call sites.
For the verb and path, open the request construction a few lines above the cited call site (`show --id <method> --source`).
The request classes are in the Google.Apis.Storage.v1 library (`Google.Apis.Storage.v1.ObjectsResource.Delete` and so on): search
the docs with Context7 for the REST shape rather than guessing.

**Anything built on `HttpClient`:**

```powershell
codeindex find-symbol HttpClient --include-external --kind class       # confirms the code uses it; note the id
codeindex callers HttpClient.SendAsync --db <db>                       # also GetAsync, PostAsync, GetStringAsync ... one at a time
```

Only library members the code actually calls exist in the index, so a method that returns nothing was not called.

**gRPC clients** (Speech, PubSub, most Google.Cloud.* packages):

```powershell
codeindex hierarchy ClientBase --direction down --db <db>              # "ClientBase" is ambiguous (generic and not): pick the <T> one by --id
codeindex outline Speech.SpeechClient --include-generated --db <db>    # the generated stub: every RPC method (Recognize, RecognizeAsync, ...)
```

On Speech.V1 the two subtypes of `Grpc.Core.ClientBase<T>` are the two services (`Speech.SpeechClient`,
`Adaptation.AdaptationClient`), and each method on the stub is one RPC. The hand-written layer (`SpeechClient`,
`SpeechClientImpl`) wraps them: `callees --id <SpeechClientImpl.Recognize id> --include-external`.

## 3. Trace each operation to the public SDK method

For the coverage table (**SDK call → remote operation → mock workflow**), walk up from the primitive:

```powershell
codeindex callers --id <operation method id> --depth 0 --through-dispatch --exclude-generated   # all the way to public entry points
```

`--through-dispatch` matters: users call the abstract `StorageClient.UploadObject`, not `StorageClientImpl.UploadObject`.
Cite the `call_sites` (file and line) for each row. Mark any public method you could not connect to a remote operation as a gap.

## 4. Find the seam (how to point the SDK at a mock)

```powershell
codeindex search emulator --include-generated --db <db> --format table      # Storage: StorageClientBuilder.EmulatorDetection
codeindex find-symbol Endpoint --kind property --include-generated          # DefaultEndpoint on each generated client
codeindex hierarchy SpeechClientBuilder --direction up --include-generated  # builder base class (ClientBuilderBase<T>): where Endpoint, credentials, CallInvoker live
codeindex show --id <DefaultEndpoint id> --source                           # the hard-coded host is a string literal in the source
```

Look for: `*ClientBuilder` classes, `Endpoint`/`BaseUri`/`BaseAddress` properties, `EmulatorDetection`, constructors that take an
`HttpClientFactory`, `HttpMessageHandler`, `CallInvoker` or `ChannelBase`. Those are the seams. If there is none, follow
Frank's rule: propose options and **ask before changing code**.

## 5. What the index cannot see (use grep or reading for these)

- **String literals and environment variables**: host names, URL templates, `Environment.GetEnvironmentVariable("...")`
  names (for example `STORAGE_EMULATOR_HOST`). Find the member with `search`/`find-symbol`, then read it with `show --source`,
  or grep the literal.
- Config files, JSON, `.proto` files, and any non-C# code.
- Code under `#if` for a target framework that was not indexed; projects excluded by default (tests, snippets).
- Calls made only by reflection or resolved at run time (DI containers, delegates stored in fields appear only as the
  assignment). Operators, implicit conversions, and `await`/`foreach` pattern calls are not recorded as references.
- Library members the code never uses (to learn what `ClientBuilderBase<T>` offers, use Context7, not the index).

## 6. Before you report

Check `info`/the result for `stale` and for compile-error warnings, and say so if either applies. State which projects and target
framework were indexed. Keep citations as `file:line` so the user can verify them.

Never put real secrets in this file.

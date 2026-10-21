# webfunction-csharp

A C# client library for the [Web Function](https://webfunction.org) protocol, in the same
reference-client family as `webfunction-go` and `webfunction-java`.

- **Target**: .NET 8 (current LTS). Zero external dependencies — uses only `System.Text.Json`
  and `System.Net.Http`, both part of the runtime.
- **Dynamic dispatch**: `Client` derives from `System.Dynamic.DynamicObject`, so a `dynamic`
  client exposes endpoints as methods directly — `client.ListItems(args)` dispatches to the
  `list-items` endpoint. This mirrors Ruby's `method_missing`, PHP's `__call`, and JS's `Proxy`
  (Go and Java use an explicit `Call(name, args)` only because those languages have no
  equivalent mechanism — C# does, so this client uses it).
- **Async-first**: every network operation returns a `Task`.
- **Nullable reference types** are enabled project-wide.

```csharp
dynamic client = await Client.FromPackageEndpointAsync("https://api.example.com/package");
var result = await client.ListItems(new Dictionary<string, object?> { ["a"] = "b" });
```

The explicit form is always available too, and is what dynamic dispatch calls under the hood:

```csharp
var result = await client.CallAsync("list-items", new Dictionary<string, object?> { ["a"] = "b" });
```

## Real-world example

`examples/AvailableApiVersions/` fetches the package at `https://api.reservepay.com/merchants`
and calls its `available-api-versions` endpoint — the same smoke-test pattern used for
webfunction-go and webfunction-java. Requires a bearer token, read from the
`RESERVEPAY_BEARER_TOKEN` env var (deliberately not hardcoded):

```
cd examples/AvailableApiVersions
RESERVEPAY_BEARER_TOKEN=... dotnet run
```

Exits early with a clear message if the env var isn't set. This hasn't been run against the
live API yet — this sandbox can't reach `api.reservepay.com` (only a fixed allowlist of domains
is reachable here), so it's only been verified to build and to fail cleanly without a token. Real
verification against the live API is the natural next step, on your machine.

## Layout

- `src/WebFunction/` — the library.
  - `Type/` — the type-union grammar (`WfnType`/`BaseType`/`ArrayOfType`/`UnionType`/`AnyType`)
    plus value-level refinement validators (email, uuid, u32/u64/i32/i64, ipv4/ipv6, etc).
  - `Argument`, `AttributeDef`, `DocumentedError`, `ObjectSchema`, `Endpoint`, `Package` — the
    parsed package model. `AttributeDef` (not `Attribute`) and `ObjectSchema` (not `Object`) are
    named to avoid clashing with `System.Attribute` and `System.Object`.
  - `Client` — dynamic-dispatch wrapper + the `FromPackageEndpointAsync`/`FromUrlAsync`/
    `FromPackage` builders.
  - `RequestExecutor` — low-level HTTP: headers, POST/GET, status/error mapping, manual gzip
    handling (see note below).
  - `Page` — pagination, wrapped when the invoking endpoint declares the `paginated` flag
    (**not** by sniffing the response shape — a deliberate deviation from the Ruby reference,
    already made the same way in webfunction-go and webfunction-java).
  - `Path`, `Promise`, `Pipeline` — call pipelining.
  - `Exceptions/` — `WebFunctionException` base + `BadRequestException`,
    `UnexpectedStatusCodeException`, `JsonParseException`, `UnresolvedPromiseException`.
- `test/WebFunction.Verify/` — a small standalone console app that exercises the library against
  a local `HttpListener`-based mock server. **Not** a real unit-test project — see below.

## Generics

A generic object (an object definition using the type parameter `T`) is applied to a type as
`{ "object.list": "object.user" }`. `WfnType.Parse` represents that as a `BaseType` of `object.list`
with `Arg` set, printing as `object.list<object.user>`. An array of a type can be written
`{ "array": "object.user" }`, which is the same as `[["object.user"]]` (an `ArrayOfType`, printing as
`array.of(object.user)`); a union of one entry is that entry. The library doesn't resolve generics
itself, and never fails on them: an unresolved `T` reads as `any` (`AnyType.Param` is true, for tooling that
wants to tell it from a literal `any`), and a malformed application becomes `any` with the reason in
`AnyType.Malformed` (`WfnType.MalformedKeyCount`, `MalformedKeyPrefix` or `MalformedArgument`).

## Why there's no xUnit/NUnit test project

The sandbox this was built in can't reach `nuget.org` (the same class of restriction hit earlier
for Maven Central and Composer), so no test framework package could be restored. `WebFunction.Verify`
is a stand-in: a console app with a hand-rolled `HttpListener` mock server and manual assertions,
covering the same ground a real test suite would (see below). On your machine, with normal NuGet
access, converting this to a real `xunit`/`Microsoft.NET.Test.Sdk` project is a small, mechanical
change — the test logic itself doesn't need to change, just the harness around it.

Run it with:

```
cd test/WebFunction.Verify
dotnet run
```

All 8 checks currently pass:

1. `FromPackageEndpointAsync` → `CallAsync` round trip, including PascalCase → hyphenated name
   mapping (`ListItems` → `list-items`)
2. A bad-request error triple maps to a correctly-typed `BadRequestException`
3. Pagination via the `paginated` flag — `NextPageAsync`/`PreviousPageAsync` navigation
4. A full pipelining round trip: two chained calls, one promise referencing another's field,
   `ResolveAsync()`
5. `UnresolvedPromiseException` before pipeline execution
6. `WfnType.Valid()` refinement checks (email, u32 range)
7. `FromUrlAsync` (GET-based fetch, `api_version` query param)
8. `Package.ObjectInContext`
9. A gzip-compressed response is decoded correctly

## Real bugs found while building this (kept for the pattern — same verification methodology used for webfunction-go/webfunction-java)

- **`Json.ToClr`'s number decoding**: the original code was
  `element.TryGetInt64(out var l) ? l : element.GetDouble()`. In C#, the ternary operator unifies
  both branch types to their common type before evaluating — since `long` implicitly converts to
  `double` but not vice versa, the *entire* expression's static type became `double`, silently
  converting every successfully-parsed `long` to a `double` as well. Every integer in a decoded
  response was actually a `double` in disguise. Fixed by explicitly boxing one branch:
  `element.TryGetInt64(out var l) ? (object)l : element.GetDouble()`. Caught by the pagination
  test casting a page item to `long` and getting an `InvalidCastException`.
- **Method/type name shadowing**: `Package.Endpoint(string)`, `Endpoint.Argument(string)`, and
  `ObjectSchema.Argument(string)` each shared a name with a type referenced elsewhere in the same
  class (`Endpoint`, `Argument`) — C# doesn't allow this (`CS0119`), because the method shadows
  the type name within its own class body, breaking `Argument.FromArray(...)`-style static calls.
  Renamed to `GetEndpoint`/`GetArgument`/etc.
- **`Attribute` collides with `System.Attribute`**: renamed to `AttributeDef` before it ever
  caused a problem, the same fix Ruby/Java already made for `Object` → `ObjectSchema`.
- **Gzip handling**: deliberately *not* relying on `HttpClientHandler.AutomaticDecompression`,
  and instead manually gunzipping when `Content-Encoding: gzip` is present — informed directly by
  the identical bug already hit and fixed in the same spot in both webfunction-go and
  webfunction-java (setting your own `Accept-Encoding` header disables a runtime's automatic
  decompression in Go and doesn't enable it in Java either; the same caution was applied here
  proactively rather than waiting to hit it a third time).
- **Test-harness bug, not a library bug, but worth recording**: an early version of the mock
  server's pipelining test echoed a step's raw JSONPath reference string (`$[0].id`) straight
  back instead of resolving it against the first step's real result — exactly the same test-double
  gap already documented for webfunction-go's pipelining test. Fixed the same way: the mock now
  actually resolves references before responding.
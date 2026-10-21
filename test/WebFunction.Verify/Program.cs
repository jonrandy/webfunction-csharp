using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using WebFunction;
using WebFunction.Exceptions;

var failures = new List<string>();
var passed = 0;

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        passed++;
        Console.WriteLine($"  PASS  {name}");
    }
    catch (Exception e)
    {
        failures.Add(name);
        Console.WriteLine($"  FAIL  {name}\n        {e}");
    }
}

void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception($"assertion failed: {message}");
    }
}

var server = new MockServer();
server.Start();
Console.WriteLine($"Mock server listening on {server.BaseUrl}\n");

// --- 0. JoinUrl - regression test for a real bug (RFC 3986 resolution via `new Uri`) -----------

await RunAsync("Client.JoinUrl matches spec's plain normalization, not RFC 3986 resolution", async () =>
{
    (string baseUrl, string name, string expected)[] cases =
    [
        ("https://api.example.com", "list-people", "https://api.example.com/list-people"),
        ("https://api.example.com/", "list-people", "https://api.example.com/list-people"),
        ("https://api.example.com/v1", "list-people", "https://api.example.com/v1/list-people"),
        ("https://api.example.com/v1/", "list-people", "https://api.example.com/v1/list-people"),
        ("https://api.example.com/v1/merchants", "list-people", "https://api.example.com/v1/merchants/list-people"),
    ];
    foreach (var (baseUrl, name, expected) in cases)
    {
        Check(Client.JoinUrl(baseUrl, name) == expected, $"JoinUrl({baseUrl}, {name}) == {expected}, got {Client.JoinUrl(baseUrl, name)}");
    }
    await Task.CompletedTask;
});

await RunAsync("CallAsync preserves a base_url path segment when base_url has no trailing slash", async () =>
{
    var basePathServer = new MockServer();
    basePathServer.Start();
    try
    {
        var requestedPaths = new List<string>();
        Task<MockResponse> Ping(HttpListenerContext ctx)
        {
            requestedPaths.Add(ctx.Request.Url!.AbsolutePath);
            return Task.FromResult(JsonResponse(200, "true"));
        }
        basePathServer.Handlers["/v1/ping"] = Ping;
        basePathServer.Handlers["/v1/merchants/ping"] = Ping;

        foreach (var basePath in new[] { "/v1", "/v1/", "/v1/merchants" })
        {
            var pkgJson = $$"""
            {
              "base_url": "{{basePathServer.BaseUrl}}{{basePath}}",
              "name": "test-package",
              "endpoints": [ { "name": "ping", "returns": ["boolean"], "arguments": [], "attributes": [] } ]
            }
            """;
            using var doc = JsonDocument.Parse(pkgJson);
            var client = Client.FromPackage(Package.FromJson(doc.RootElement));
            await client.CallAsync("ping");
        }

        Check(requestedPaths.SequenceEqual(["/v1/ping", "/v1/ping", "/v1/merchants/ping"]),
            $"expected [/v1/ping, /v1/ping, /v1/merchants/ping], got [{string.Join(", ", requestedPaths)}]");
    }
    finally
    {
        basePathServer.Stop();
    }
});

// --- 1. FromPackageEndpointAsync -> CallAsync round trip, PascalCase name mapping ------------

await RunAsync("FromPackageEndpoint -> Call round trip + name mapping", async () =>
{
    server.Handlers["/package"] = _ => Task.FromResult(JsonResponse(200, PackageJson(server.BaseUrl)));
    server.Handlers["/list-items"] = ctx =>
    {
        Check(ctx.Request.Headers["Content-Type"]?.StartsWith("application/json") == true, "content type header");
        return Task.FromResult(JsonResponse(200, "{\"items\":[1,2,3]}"));
    };

    dynamic client = await Client.FromPackageEndpointAsync($"{server.BaseUrl}/package");
    object? result = await client.ListItems(new Dictionary<string, object?>());

    Check(result is Dictionary<string, object?> d && d.ContainsKey("items"), "decoded result has items key");
});

// --- 2. Bad request error triple -> BadRequestException --------------------------------------

await RunAsync("bad-request error triple -> BadRequestException", async () =>
{
    server.Handlers["/package"] = _ => Task.FromResult(JsonResponse(200, PackageJson(server.BaseUrl)));
    server.Handlers["/list-items"] = _ =>
        Task.FromResult(JsonResponse(400, "[\"WFN_NOT_FOUND\",\"Not found\",{\"id\":\"abc\"}]"));

    dynamic client = await Client.FromPackageEndpointAsync($"{server.BaseUrl}/package");

    var thrown = await CatchAsync<BadRequestException>(async () => await client.ListItems(new Dictionary<string, object?>()));
    Check(thrown is not null, "BadRequestException was thrown");
    Check(thrown!.Code == "WFN_NOT_FOUND", $"code should be WFN_NOT_FOUND, was {thrown.Code}");
    Check(thrown.Message == "Not found", $"message should be 'Not found', was '{thrown.Message}'");
});

// --- 3. Pagination via the paginated flag -----------------------------------------------------

await RunAsync("pagination (paginated flag) - NextPage/PreviousPage navigation", async () =>
{
    server.Handlers["/package"] = _ => Task.FromResult(JsonResponse(200, PackageJson(server.BaseUrl)));
    var call = 0;
    server.Handlers["/list-paginated"] = _ =>
    {
        call++;
        return Task.FromResult(call switch
        {
            1 => JsonResponse(200, "{\"page\":[1,2],\"next\":{\"cursor\":\"n1\"},\"previous\":null}"),
            2 => JsonResponse(200, "{\"page\":[3,4],\"next\":null,\"previous\":{\"cursor\":\"p1\"}}"),
            _ => JsonResponse(200, "{\"page\":[1,2],\"next\":{\"cursor\":\"n1\"},\"previous\":null}"),
        });
    };

    dynamic client = await Client.FromPackageEndpointAsync($"{server.BaseUrl}/package");
    var page1 = await client.ListPaginated(new Dictionary<string, object?>()) as Page;
    Check(page1 is not null, "first call returns a Page");
    Check(page1!.Items.Count == 2, "page 1 has 2 items");
    Check(page1.HasNext, "page 1 has a next page");
    Check(!page1.HasPrevious, "page 1 has no previous page");

    var page2 = await page1.NextPageAsync();
    Check(page2 is not null, "next page fetched");
    Check(page2!.Items.Count == 2 && (long)page2.Items[0]! == 3, "page 2 has the right items");
    Check(page2.HasPrevious, "page 2 has a previous page");
    Check(!page2.HasNext, "page 2 has no next page");
});

// --- 4. Pipelining round trip: chained calls, promise field reference, resolve --------------

await RunAsync("pipelining round trip (chained calls, promise field reference)", async () =>
{
    server.Handlers["/package"] = _ => Task.FromResult(JsonResponse(200, PackageJsonWithPipeline(server.BaseUrl)));
    server.Handlers["/pipe"] = async ctx =>
    {
        var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
        using var doc = JsonDocument.Parse(body);
        var steps = doc.RootElement.GetProperty("steps");

        // A real pipeline endpoint resolves "$[n].field"-style references against earlier
        // results in the same batch server-side. This mock must do the same, or it silently
        // masks whether Promise-based argument passing actually works (the same lesson already
        // learned the hard way in webfunction-go's own pipelining test).
        var step0Result = new Dictionary<string, object?> { ["id"] = "u1" };
        var userIdArg = steps[1].GetProperty("body").GetProperty("user_id").GetString();
        var resolvedUserId = userIdArg == "$[0].id" ? (string)step0Result["id"]! : userIdArg;

        var results = new List<object?>
        {
            step0Result,
            new Dictionary<string, object?> { ["profile_for"] = resolvedUserId },
        };
        return JsonResponse(200, JsonSerializer.Serialize(results));
    };

    dynamic client = await Client.FromPackageEndpointAsync($"{server.BaseUrl}/package", pipelined: true);

    var userPromise = await client.CreateUser(new Dictionary<string, object?>()) as Promise;
    Check(userPromise is not null, "CreateUser under a pipelined client returns a Promise");

    var thrown = await CatchAsync<UnresolvedPromiseException>(() =>
    {
        _ = userPromise!.Value;
        return Task.CompletedTask;
    });
    Check(thrown is not null, "accessing .Value before pipeline execution throws UnresolvedPromiseException");

    var userIdRef = userPromise!.Field("id");
    var profilePromise = await client.GetProfile(new Dictionary<string, object?> { ["user_id"] = userIdRef }) as Promise;
    Check(profilePromise is not null, "GetProfile also returns a Promise");

    var profile = await profilePromise!.ResolveAsync();
    Check(userPromise.IsResolved, "first promise resolved as a side effect of pipeline execution");
    Check(profile is Dictionary<string, object?> pd && (string)pd["profile_for"]! == "u1",
        "second step's server-resolved reference used the first step's real id");
});

// --- 5. Type value validators ------------------------------------------------------------------

await RunAsync("WfnType.Valid() refinement checks", () =>
{
    using var emailType = JsonDocument.Parse("\"string.email\"");
    var email = WebFunction.Type.WfnType.Parse(emailType.RootElement);
    Check(email.Valid("a@b.com"), "valid email passes");
    Check(!email.Valid("not-an-email"), "invalid email fails");

    using var u32Type = JsonDocument.Parse("\"number.u32\"");
    var u32 = WebFunction.Type.WfnType.Parse(u32Type.RootElement);
    Check(u32.Valid(42L), "in-range u32 passes");
    Check(!u32.Valid(-1L), "negative value fails u32");

    return Task.CompletedTask;
});

// --- 6. FromUrlAsync (GET-based fetch, api_version query param) ------------------------------

await RunAsync("FromUrl (GET-based fetch, api_version query param)", async () =>
{
    server.Handlers["/plain-package"] = ctx =>
    {
        Check(ctx.Request.HttpMethod == "GET", "FromUrl uses GET");
        Check(ctx.Request.Url!.Query.Contains("api_version=v2"), "api_version sent as query param");
        return Task.FromResult(JsonResponse(200, PackageJson(server.BaseUrl)));
    };

    var client = await Client.FromUrlAsync($"{server.BaseUrl}/plain-package", version: "v2");
    Check(client.Package.BaseUrl == server.BaseUrl + "/", "package parsed correctly via FromUrl");
});

// --- 7. Package.ObjectInContext ------------------------------------------------------------

await RunAsync("Package.ObjectInContext", () =>
{
    using var doc = JsonDocument.Parse(PackageJsonWithObjects(server.BaseUrl));
    var package = WebFunction.Package.FromJson(doc.RootElement);

    var argCtx = package.ObjectInContext("address", ObjectContext.Arguments);
    Check(argCtx is not null, "address has members in the arguments context");

    var attrCtx = package.ObjectInContext("address", ObjectContext.Attributes);
    Check(attrCtx is null, "address has no members in the attributes context, treated as absent");

    return Task.CompletedTask;
});

// --- 8. Gzip-compressed response regression test --------------------------------------------

await RunAsync("gzip-compressed response is decoded correctly", async () =>
{
    server.Handlers["/package"] = _ => Task.FromResult(JsonResponse(200, PackageJson(server.BaseUrl)));
    server.Handlers["/list-items"] = _ => Task.FromResult(GzipJsonResponse(200, "{\"items\":[\"gz1\",\"gz2\"]}"));

    dynamic client = await Client.FromPackageEndpointAsync($"{server.BaseUrl}/package");
    object? result = await client.ListItems(new Dictionary<string, object?>());

    Check(result is Dictionary<string, object?> d &&
          d["items"] is List<object?> items && items.Count == 2 && (string)items[0]! == "gz1",
        "gzip response decompressed and parsed correctly");
});

// --- 9. private flag on endpoints, arguments, and attributes -------------------------------

await RunAsync("Private flag on Endpoint, Argument, and AttributeDef", () =>
{
    var json = JsonSerializer.Serialize(new
    {
        base_url = "https://api.example.com/",
        endpoints = new object[]
        {
            new { name = "internal-sync", returns = "boolean", flags = new[] { "private" } },
            new
            {
                name = "find-account",
                returns = "object",
                arguments = new object[]
                {
                    new { name = "id", type = "string", flags = new[] { "required" } },
                    new { name = "debug", type = "boolean", flags = new[] { "private" } },
                },
                attributes = new object[]
                {
                    new { name = "email", type = "string" },
                    new { name = "audit_ref", type = "string", flags = new[] { "private" } },
                },
            },
        },
    });
    using var doc = JsonDocument.Parse(json);
    var pkg = Package.FromJson(doc.RootElement);
    var account = pkg.GetEndpoint("find-account")!;
    Check(pkg.GetEndpoint("internal-sync")!.Private && !account.Private, "Endpoint.Private");
    Check(account.GetArgument("debug")!.Private && !account.GetArgument("id")!.Private, "Argument.Private");
    Check(account.GetAttribute("audit_ref")!.Private && !account.GetAttribute("email")!.Private, "AttributeDef.Private");
    return Task.CompletedTask;
});

// --- Generics: applications of generic objects, and the type parameter T -----------------------

static WebFunction.Type.WfnType ParseType(string json)
{
    using var doc = JsonDocument.Parse(json);
    return WebFunction.Type.WfnType.Parse(doc.RootElement.Clone());
}

void CheckType(string json, string expected) =>
    Check(ParseType(json).ToString() == expected, $"{json} should parse to '{expected}', got '{ParseType(json)}'");

await RunAsync("generics: existing type forms are unchanged", () =>
{
    CheckType("\"string\"", "string");
    CheckType("\"string.email\"", "string.email");
    CheckType("\"object.user\"", "object.user");
    CheckType("[\"string\",\"null\"]", "string | null");
    CheckType("[[\"string\"]]", "array.of(string)");
    CheckType("[[\"object.person\",\"null\"]]", "array.of(object.person | null)");
    CheckType("[\"array\",[\"object.person\"]]", "array.of(any) | array.of(object.person)");
    CheckType("null", "any");
    return Task.CompletedTask;
});

await RunAsync("generics: a generic application parses with its argument", () =>
{
    var t = ParseType("""{ "object.list": "object.user" }""");
    Check(t.ToString() == "object.list<object.user>", $"got {t}");
    var bt = t as WebFunction.Type.BaseType;
    Check(bt is not null && bt.BaseTypeName == "object" && bt.Refinement == "list", "object type named list");
    Check(bt!.Arg?.ToString() == "object.user", "argument is object.user");
    Check(t.Valid(new Dictionary<string, object?>()) && t.Valid("anything"), "an applied object type does no value validation");
    return Task.CompletedTask;
});

await RunAsync("generics: union, nested and array arguments, and applications inside unions and arrays", () =>
{
    CheckType("""{ "object.list": ["object.user", "null"] }""", "object.list<object.user | null>");
    CheckType("""{ "object.page": { "object.page": "object.user" } }""", "object.page<object.page<object.user>>");
    CheckType("""{ "object.list": [["string"]] }""", "object.list<array.of(string)>");
    CheckType("""[{ "object.list": "object.user" }, "null"]""", "object.list<object.user> | null");
    CheckType("""[[{ "object.list": "object.user" }]]""", "array.of(object.list<object.user>)");
    CheckType("""[[{ "object.list": "object.user" }, "null"]]""", "array.of(object.list<object.user> | null)");
    CheckType("""{ "object.list": [{ "object.pair": "number" }, "null"] }""", "object.list<object.pair<number> | null>");
    return Task.CompletedTask;
});

await RunAsync("generics: unions keep distinct applications and dedupe identical ones", () =>
{
    CheckType("""[{ "object.list": "string" }, { "object.list": "number" }]""", "object.list<string> | object.list<number>");
    CheckType("""[{ "object.list": "string" }, { "object.list": "string" }]""", "object.list<string>");
    return Task.CompletedTask;
});

await RunAsync("generics: a malformed application resolves to any, with the reason recorded", () =>
{
    (string json, string reason)[] cases =
    [
        ("{}", WebFunction.Type.WfnType.MalformedKeyCount),
        ("""{ "object.a": "string", "object.b": "string" }""", WebFunction.Type.WfnType.MalformedKeyCount),
        ("""{ "string": "object.user" }""", WebFunction.Type.WfnType.MalformedKeyPrefix),
        ("""{ "list": "object.user" }""", WebFunction.Type.WfnType.MalformedKeyPrefix),
        ("""{ "object.": "string" }""", WebFunction.Type.WfnType.MalformedKeyPrefix),
        ("""{ "object.list": 5 }""", WebFunction.Type.WfnType.MalformedArgument),
        ("""{ "object.list": true }""", WebFunction.Type.WfnType.MalformedArgument),
    ];
    foreach (var (json, reason) in cases)
    {
        var t = ParseType(json);
        Check(t is WebFunction.Type.AnyType, $"{json} should be any, got {t}");
        Check(((WebFunction.Type.AnyType)t).Malformed == reason, $"{json} reason should be {reason}, got {((WebFunction.Type.AnyType)t).Malformed}");
        Check(t.Valid("anything"), $"{json} accepts anything");
    }

    // A malformed entry inside a union is kept as any, not dropped.
    var u = ParseType("""[{ "string": "x" }, "null"]""");
    Check(u.ToString() == "any | null", $"got {u}");
    Check(((WebFunction.Type.UnionType)u).Types[0] is WebFunction.Type.AnyType { Malformed: WebFunction.Type.WfnType.MalformedKeyPrefix }, "reason kept inside a union");
    Check(((WebFunction.Type.AnyType)ParseType("\"any\"")).Malformed is null, "a plain any is not malformed");
    return Task.CompletedTask;
});

await RunAsync("generics: the unresolved type parameter T is any, marked for tooling", () =>
{
    CheckType("\"T\"", "any");
    CheckType("[[\"T\"]]", "array.of(any)");
    CheckType("[\"T\",\"null\"]", "any | null");
    Check(ParseType("\"T\"").Valid("anything"), "T accepts anything");

    Check(ParseType("\"T\"") is WebFunction.Type.AnyType { Param: true }, "T is marked as a parameter");
    Check(((WebFunction.Type.ArrayOfType)ParseType("[[\"T\"]]")).Of is WebFunction.Type.AnyType { Param: true }, "mark survives inside an array");
    Check(((WebFunction.Type.UnionType)ParseType("[\"T\",\"null\"]")).Types[0] is WebFunction.Type.AnyType { Param: true }, "mark survives inside a union");

    // Only the bare token: a literal any and an object named T are not parameters.
    Check(ParseType("\"any\"") is WebFunction.Type.AnyType { Param: false }, "a literal any is not a parameter");
    Check(ParseType("\"object.T\"") is WebFunction.Type.BaseType { Refinement: "T" }, "object.T is an ordinary reference");
    return Task.CompletedTask;
});

await RunAsync("generics: a package that uses generics loads", () =>
{
    const string json = """
        {
          "base_url": "https://api.example.com/",
          "endpoints": [
            {
              "name": "list-users",
              "returns": { "object.list": "object.user" },
              "arguments": [{ "name": "filter", "type": [{ "object.pair": "string" }, "null"] }]
            },
            { "name": "broken", "returns": { "string": "x" }, "arguments": [] }
          ],
          "objects": [
            { "name": "list", "attributes": [
              { "name": "total", "type": "number" },
              { "name": "items", "type": [["T"]] }
            ] }
          ]
        }
        """;
    using var doc = JsonDocument.Parse(json);
    var pkg = Package.FromJson(doc.RootElement);
    var ep = pkg.GetEndpoint("list-users")!;
    Check(ep.Returns.ToString() == "object.list<object.user>", $"returns: {ep.Returns}");
    Check(ep.GetArgument("filter")!.ArgType.ToString() == "object.pair<string> | null", $"argument: {ep.GetArgument("filter")!.ArgType}");
    Check(pkg.GetEndpoint("broken")!.Returns.ToString() == "any", "a malformed return is any");
    var list = pkg.ObjectInContext("list", ObjectContext.Attributes)!;
    Check(list.GetAttribute("items")!.AttrType.ToString() == "array.of(any)", $"T attribute: {list.GetAttribute("items")!.AttrType}");
    return Task.CompletedTask;
});

await RunAsync("generics: an array application parses like the legacy nested-array form", () =>
{
    (string application, string legacy)[] pairs =
    [
        ("""{ "array": "string" }""", """[["string"]]"""),
        ("""{ "array": "object.user" }""", """[["object.user"]]"""),
        ("""{ "array": ["string", "null"] }""", """[["string", "null"]]"""),
        ("""{ "array": "T" }""", """[["T"]]"""),
        ("""{ "array": { "array": "string" } }""", """[[["string"]]]"""),
        ("""{ "array": { "object.page": "object.user" } }""", """[[{ "object.page": "object.user" }]]"""),
        ("""{ "object.page": { "array": "object.user" } }""", """{ "object.page": [["object.user"]] }"""),
        ("""[{ "array": "string" }, "null"]""", """[["string"], "null"]"""),
    ];
    foreach (var (application, legacy) in pairs)
    {
        var a = ParseType(application);
        var l = ParseType(legacy);
        Check(a.ToString() == l.ToString(), $"{application} should read like {legacy} ('{l}'), got '{a}'");
        Check(a.GetType() == l.GetType(), $"{application} should be a {l.GetType().Name}, got {a.GetType().Name}");
    }
    return Task.CompletedTask;
});

await RunAsync("generics: array applications format, and keep their element type", () =>
{
    CheckType("""{ "array": "string" }""", "array.of(string)");
    CheckType("""{ "array": "object.user" }""", "array.of(object.user)");
    CheckType("""{ "array": ["string", "number"] }""", "array.of(string | number)");
    CheckType("""{ "array": { "array": "string" } }""", "array.of(array.of(string))");
    CheckType("""{ "array": { "object.page": "object.user" } }""", "array.of(object.page<object.user>)");
    CheckType("""{ "object.page": { "array": "object.user" } }""", "object.page<array.of(object.user)>");
    CheckType("""{ "array": "T" }""", "array.of(any)");
    CheckType("""[{ "array": "string" }, "null"]""", "array.of(string) | null");
    CheckType("""["array", { "array": "string" }]""", "array.of(any) | array.of(string)");

    var t = ParseType("""{ "array": "object.user" }""") as WebFunction.Type.ArrayOfType;
    Check(t is not null && t.Of.ToString() == "object.user", "an array application is an ArrayOfType of its element");
    Check(((WebFunction.Type.ArrayOfType)ParseType("""{ "array": "T" }""")).Of is WebFunction.Type.AnyType { Param: true }, "T survives as the element of an array application");
    return Task.CompletedTask;
});

await RunAsync("generics: a malformed array application resolves to any, with the reason recorded", () =>
{
    (string json, string reason)[] cases =
    [
        ("""{ "array": "string", "object.a": "string" }""", WebFunction.Type.WfnType.MalformedKeyCount),
        ("""{ "array": 5 }""", WebFunction.Type.WfnType.MalformedArgument),
        ("""{ "array": true }""", WebFunction.Type.WfnType.MalformedArgument),
        ("""{ "arrays": "string" }""", WebFunction.Type.WfnType.MalformedKeyPrefix),
        ("""{ "array.x": "string" }""", WebFunction.Type.WfnType.MalformedKeyPrefix),
        ("""{ "Array": "string" }""", WebFunction.Type.WfnType.MalformedKeyPrefix),
    ];
    foreach (var (json, reason) in cases)
    {
        var t = ParseType(json);
        Check(t is WebFunction.Type.AnyType, $"{json} should be any, got {t}");
        Check(((WebFunction.Type.AnyType)t).Malformed == reason, $"{json} reason should be {reason}, got {((WebFunction.Type.AnyType)t).Malformed}");
    }
    return Task.CompletedTask;
});

// Spec: a union of one entry is that entry. The parser already collapses it; this pins it.
await RunAsync("generics: a union of one entry is that entry", () =>
{
    Check(ParseType("""["string"]""") is WebFunction.Type.BaseType { BaseTypeName: "string" }, "[\"string\"] is string");
    CheckType("""["string.email"]""", "string.email");
    Check(ParseType("""["T"]""") is WebFunction.Type.AnyType { Param: true }, "[\"T\"] is T");
    Check(ParseType("""[{ "array": "string" }]""") is WebFunction.Type.ArrayOfType, "a one-entry union holding an array application is that array");
    Check(ParseType("""[{ "object.list": "object.user" }]""") is WebFunction.Type.BaseType { Refinement: "list" }, "a one-entry union holding an application is that application");
    // The legacy array shorthand is not a one-entry union: [["x"]] is still an array of x.
    Check(ParseType("""[["string"]]""") is WebFunction.Type.ArrayOfType, "[[\"string\"]] is an array");
    Check(ParseType("""["string", "null"]""") is WebFunction.Type.UnionType, "two entries stay a union");
    return Task.CompletedTask;
});

await RunAsync("generics: a package that uses array applications loads", () =>
{
    const string json = """
        {
          "base_url": "https://api.example.com/",
          "endpoints": [
            { "name": "get-comments", "returns": { "array": "object.comment" }, "arguments": [] }
          ],
          "objects": [
            { "name": "comment", "attributes": [
              { "name": "id", "type": "string.uuid" },
              { "name": "message", "type": "string" },
              { "name": "replies", "type": { "array": "object.comment" } }
            ] }
          ]
        }
        """;
    using var doc = JsonDocument.Parse(json);
    var pkg = Package.FromJson(doc.RootElement);
    Check(pkg.GetEndpoint("get-comments")!.Returns.ToString() == "array.of(object.comment)", $"returns: {pkg.GetEndpoint("get-comments")!.Returns}");
    var comment = pkg.ObjectInContext("comment", ObjectContext.Attributes)!;
    Check(comment.GetAttribute("replies")!.AttrType.ToString() == "array.of(object.comment)", $"replies: {comment.GetAttribute("replies")!.AttrType}");
    return Task.CompletedTask;
});

await RunAsync("WfnType.Valid() only applies refinement validators to string and number, not to object names", () =>
{
    // An object that happens to share a name with a refinement (object.email, object.u32) is just an object reference.
    Check(ParseType("\"object.email\"").Valid("not-an-email"), "object.email must not be email-validated");
    Check(ParseType("\"object.u32\"").Valid("not-a-number"), "object.u32 must not be range-validated");
    Check(ParseType("""{ "object.uuid": "string" }""").Valid("nope"), "an applied object.uuid must not be uuid-validated");

    // ...while real refinements are still enforced.
    Check(!ParseType("\"string.email\"").Valid("not-an-email"), "string.email still validates");
    Check(!ParseType("\"number.u32\"").Valid(-1L), "number.u32 still validates");
    return Task.CompletedTask;
});

server.Stop();

Console.WriteLine($"\n{passed} passed, {failures.Count} failed.");
if (failures.Count > 0)
{
    Environment.Exit(1);
}

// --- helpers -----------------------------------------------------------------------------------

static async Task<T?> CatchAsync<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
        return null;
    }
    catch (T e)
    {
        return e;
    }
}

static MockResponse JsonResponse(int status, string body) =>
    new(status, Encoding.UTF8.GetBytes(body), gzip: false);

static MockResponse GzipJsonResponse(int status, string body)
{
    using var output = new MemoryStream();
    using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        gzip.Write(bytes, 0, bytes.Length);
    }

    return new MockResponse(status, output.ToArray(), gzip: true);
}

static string PackageJson(string baseUrl) => $$"""
{
  "base_url": "{{baseUrl}}/",
  "name": "test-package",
  "endpoints": [
    { "name": "list-items", "returns": ["object"], "arguments": [], "attributes": [] },
    { "name": "list-paginated", "returns": ["object"], "flags": ["paginated"], "arguments": [], "attributes": [] }
  ]
}
""";

static string PackageJsonWithPipeline(string baseUrl) => $$"""
{
  "base_url": "{{baseUrl}}/",
  "pipeline_url": "{{baseUrl}}/pipe",
  "name": "test-package",
  "endpoints": [
    { "name": "create-user", "returns": ["object"], "arguments": [], "attributes": [] },
    { "name": "get-profile", "returns": ["object"], "arguments": [], "attributes": [] }
  ]
}
""";

static string PackageJsonWithObjects(string baseUrl) => $$"""
{
  "base_url": "{{baseUrl}}/",
  "name": "test-package",
  "endpoints": [],
  "objects": [
    {
      "name": "address",
      "arguments": [ { "name": "street", "type": "string" } ],
      "attributes": []
    }
  ]
}
""";

internal sealed record MockResponse(int Status, byte[] Body, bool gzip);

internal sealed class MockServer
{
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;

    public Dictionary<string, Func<HttpListenerContext, Task<MockResponse>>> Handlers { get; } = new();

    public string BaseUrl { get; }

    public MockServer()
    {
        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{BaseUrl}/");
    }

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener.Stop();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(token);
            }
            catch
            {
                return;
            }

            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (!Handlers.TryGetValue(path, out var handler))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        MockResponse response;
        try
        {
            response = await handler(ctx);
        }
        catch (Exception e)
        {
            // A handler assertion failure must still produce a response — otherwise the client
            // just hangs until its own HTTP timeout, which masks the real failure as a timeout.
            Console.WriteLine($"      [mock server handler threw for {path}]: {e.Message}");
            response = new MockResponse(500, System.Text.Encoding.UTF8.GetBytes("{}"), gzip: false);
        }

        ctx.Response.StatusCode = response.Status;
        if (response.gzip)
        {
            ctx.Response.Headers.Add("Content-Encoding", "gzip");
        }

        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(response.Body);
        ctx.Response.Close();
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
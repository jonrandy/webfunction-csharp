using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebFunction.Type;

/// <summary>
/// The recursive type-union grammar used by argument/attribute/return types in a Web Function
/// package: a base type (optionally refined), an array of a type (<c>[["t"]]</c> or <c>{ "array": "t" }</c>),
/// a union of types (a union of one entry is that entry), a generic object applied to a type
/// (<c>{ "object.list": "object.user" }</c>), or "any".
/// Ported from the reference Ruby gem's <c>type.rb</c>, matching the model already used by
/// webfunction-go and webfunction-java.
/// </summary>
public abstract class WfnType
{
    public static readonly IReadOnlyDictionary<string, string[]> AllowedRefinements = new Dictionary<string, string[]>
    {
        ["number"] = new[] { "u32", "u64", "i32", "i64", "f32", "f64", "timestamp" },
        ["string"] = new[] { "date", "time", "datetime", "uuid", "base64", "email", "phone", "url", "uri", "ipv4", "ipv6", "hostname" },
    };

    /// <summary>
    /// Value-level validators for each refinement, keyed by refinement name. Refinement names are
    /// unique across base types, so a flat table is enough. Must stay in sync with
    /// <see cref="AllowedRefinements"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Func<object?, bool>> RefinementValidators =
        new Dictionary<string, Func<object?, bool>>
        {
            ["u32"] = v => IsIntegerInRange(v, 0, 0xFFFFFFFFL),
            ["u64"] = v => v is long l && l >= 0,
            ["i32"] = v => IsIntegerInRange(v, int.MinValue, int.MaxValue),
            ["i64"] = v => v is long or int,
            ["f32"] = v => IsFiniteNumber(v) && Math.Abs(ToDouble(v)) <= 3.4028235e38,
            ["f64"] = v => IsFiniteNumber(v),
            ["timestamp"] = v => IsIntegerInRange(v, 0, long.MaxValue),
            ["date"] = v => v is string s && Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}$"),
            ["time"] = v => v is string s && Regex.IsMatch(s, @"^\d{2}:\d{2}:\d{2}(\.\d+)?$"),
            ["datetime"] = v => v is string s &&
                Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?$"),
            ["uuid"] = v => v is string s && Regex.IsMatch(s, @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"),
            ["base64"] = v => v is string s && s.Length % 4 == 0 && Regex.IsMatch(s, @"^[A-Za-z0-9+/]*={0,2}$"),
            ["email"] = v => v is string s && Regex.IsMatch(s, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"),
            ["phone"] = v => v is string s && Regex.IsMatch(s, @"^\+[1-9]\d{1,14}$"),
            ["url"] = v => v is string s && Uri.TryCreate(s, UriKind.Absolute, out var u) &&
                (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps),
            ["uri"] = v => v is string s && Uri.TryCreate(s, UriKind.Absolute, out _),
            ["ipv4"] = v => v is string s && IPAddress.TryParse(s, out var a) &&
                a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork,
            ["ipv6"] = v => v is string s && IPAddress.TryParse(s, out var a) &&
                a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6,
            ["hostname"] = v => v is string s && s.Length is >= 1 and <= 253 &&
                Regex.IsMatch(s, @"^([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$"),
        };

    private static bool IsIntegerInRange(object? v, long min, long max) =>
        v is long l && l >= min && l <= max;

    private static bool IsFiniteNumber(object? v) =>
        v is double d ? !double.IsNaN(d) && !double.IsInfinity(d) : v is long or int;

    private static double ToDouble(object? v) => v switch
    {
        double d => d,
        long l => l,
        int i => i,
        _ => 0,
    };

    /// <summary><see cref="AnyType.Malformed"/> reason: a generic application object did not have exactly one key.</summary>
    public const string MalformedKeyCount = "key-count";

    /// <summary><see cref="AnyType.Malformed"/> reason: the application's key was neither <c>object.&lt;name&gt;</c> nor <c>array</c>.</summary>
    public const string MalformedKeyPrefix = "key-prefix";

    /// <summary><see cref="AnyType.Malformed"/> reason: the application's value was not a valid type.</summary>
    public const string MalformedArgument = "argument";

    /// <summary>
    /// Parses a raw <c>type</c>/<c>returns</c> JSON value (a string, an application object, or an
    /// array of strings/arrays/application objects).
    /// </summary>
    public static WfnType Parse(JsonElement? raw)
    {
        if (raw is null || raw.Value.ValueKind == JsonValueKind.Null || raw.Value.ValueKind == JsonValueKind.Undefined)
        {
            return new AnyType();
        }

        var el = raw.Value;
        var entries = el.ValueKind == JsonValueKind.Array ? el.EnumerateArray().ToList() : new List<JsonElement> { el };

        var types = entries.Select(Detect).Where(t => t is not null).Select(t => t!).ToList();

        return types.Count == 0 ? new AnyType() : Union(types);
    }

    private static WfnType? Detect(JsonElement raw)
    {
        switch (raw.ValueKind)
        {
            case JsonValueKind.String:
                return BaseFromString(raw.GetString()!);
            case JsonValueKind.Object:
                return Application(raw);
            case JsonValueKind.Array:
                var inner = raw.EnumerateArray().Select(Detect).Where(t => t is not null).Select(t => t!).ToList();
                return new ArrayOfType(inner.Count == 0 ? new AnyType() : Union(inner));
            default:
                return null;
        }
    }

    /// <summary>
    /// Parses a type application: a generic object applied to a type, <c>{ "object.&lt;name&gt;": &lt;type&gt; }</c>,
    /// or an array of a type, <c>{ "array": &lt;type&gt; }</c> (the same as <c>[[&lt;type&gt;]]</c>). Anything that is
    /// not exactly one of those shapes resolves to <see cref="AnyType"/> carrying the reason, rather than
    /// failing, so a package using generics (even incorrectly) still loads.
    /// </summary>
    private static WfnType Application(JsonElement raw)
    {
        var properties = raw.EnumerateObject().ToList();

        if (properties.Count != 1)
        {
            return new AnyType(malformed: MalformedKeyCount);
        }

        var key = properties[0].Name;

        if (key == "array")
        {
            var element = properties[0].Value;

            return element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                ? new AnyType(malformed: MalformedArgument)
                : new ArrayOfType(Parse(element));
        }

        if (!key.StartsWith("object.", StringComparison.Ordinal) || key == "object.")
        {
            return new AnyType(malformed: MalformedKeyPrefix);
        }

        var value = properties[0].Value;

        if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            return new AnyType(malformed: MalformedArgument);
        }

        return new BaseType("object", key["object.".Length..], Parse(value));
    }

    private static WfnType BaseFromString(string type)
    {
        if (type == "T")
        {
            return new AnyType(param: true);
        }

        var parts = type.Split('.', 2);
        var baseType = parts[0];
        string? refinement = parts.Length > 1 ? parts[1] : null;

        return baseType switch
        {
            "string" => new BaseType("string", ValidRefinement("string", refinement)),
            "number" => new BaseType("number", ValidRefinement("number", refinement)),
            "object" => new BaseType("object", refinement),
            "array" => new ArrayOfType(new AnyType()),
            "boolean" => new BaseType("boolean", null),
            "null" => new BaseType("null", null),
            "any" => new AnyType(),
            _ => new AnyType(),
        };
    }

    private static string? ValidRefinement(string baseType, string? refinement)
    {
        if (refinement is null)
        {
            return null;
        }

        return AllowedRefinements.TryGetValue(baseType, out var allowed) && allowed.Contains(refinement)
            ? refinement
            : null;
    }

    private static WfnType Union(List<WfnType> types)
    {
        var distinct = types.Distinct(WfnTypeEqualityComparer.Instance).ToList();
        return distinct.Count > 1 ? new UnionType(distinct) : distinct[0];
    }

    /// <summary>
    /// Value-level validation against this type's refinement, if any. Base types with no
    /// refinement (or non-<see cref="BaseType"/> types) always validate as true — this only
    /// checks the specific refinement constraints (email format, u32 range, etc), the same scope
    /// as the Ruby reference's per-refinement <c>valid?</c> checks.
    /// </summary>
    public virtual bool Valid(object? value)
    {
        if (this is not BaseType { BaseTypeName: "string" or "number" } bt || bt.Refinement is null)
        {
            return true;
        }

        return !RefinementValidators.TryGetValue(bt.Refinement, out var validator) || validator(value);
    }
}

internal sealed class WfnTypeEqualityComparer : IEqualityComparer<WfnType>
{
    public static readonly WfnTypeEqualityComparer Instance = new();

    public bool Equals(WfnType? x, WfnType? y)
    {
        if (x is BaseType bx && y is BaseType by)
        {
            return bx.BaseTypeName == by.BaseTypeName
                && bx.Refinement == by.Refinement
                && (bx.Arg is null ? by.Arg is null : by.Arg is not null && Equals(bx.Arg, by.Arg));
        }

        if (x is ArrayOfType ax && y is ArrayOfType ay)
        {
            return Equals(ax.Of, ay.Of);
        }

        return x is AnyType && y is AnyType;
    }

    public int GetHashCode(WfnType obj) => obj switch
    {
        BaseType b => HashCode.Combine(b.BaseTypeName, b.Refinement, b.Arg is null ? 0 : GetHashCode(b.Arg)),
        ArrayOfType a => HashCode.Combine("array", GetHashCode(a.Of)),
        _ => "any".GetHashCode(),
    };
}
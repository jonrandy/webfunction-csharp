namespace WebFunction.Type;

/// <summary>A base JSON type (<c>string</c>, <c>number</c>, <c>object</c>, <c>boolean</c>, <c>null</c>), optionally refined.</summary>
public sealed class BaseType : WfnType
{
    public BaseType(string baseTypeName, string? refinement, WfnType? arg = null)
    {
        BaseTypeName = baseTypeName;
        Refinement = refinement;
        Arg = arg;
    }

    /// <summary>One of <c>string</c>, <c>number</c>, <c>object</c>, <c>boolean</c>, <c>null</c>.</summary>
    public string BaseTypeName { get; }

    /// <summary>
    /// The refinement, if any (e.g. <c>email</c> for <c>string.email</c>, the referenced object
    /// name for <c>object.&lt;name&gt;</c>). <c>null</c> for an unrefined base type.
    /// </summary>
    public string? Refinement { get; }

    /// <summary>
    /// The type argument when this is a generic object applied to a type: <c>{ "object.list": "object.user" }</c>
    /// is <c>object.list</c> with the argument <c>object.user</c>. <c>null</c> for any other type.
    /// </summary>
    public WfnType? Arg { get; }

    public override string ToString()
    {
        var text = Refinement is null ? BaseTypeName : $"{BaseTypeName}.{Refinement}";
        return Arg is null ? text : $"{text}<{Arg}>";
    }
}
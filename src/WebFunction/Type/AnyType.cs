namespace WebFunction.Type;

/// <summary>An unconstrained type — matches any value.</summary>
public sealed class AnyType : WfnType
{
    /// <param name="param">
    /// Whether this is the generic type parameter <c>T</c>. An unresolved parameter is <c>any</c> to every
    /// consumer; the flag only lets tooling that expands generics tell it from a literal <c>any</c>.
    /// </param>
    /// <param name="malformed">
    /// One of the <c>WfnType.Malformed*</c> reasons when this stands in for an invalid generic application,
    /// so validation tooling can still report it.
    /// </param>
    public AnyType(bool param = false, string? malformed = null)
    {
        Param = param;
        Malformed = malformed;
    }

    /// <summary>True for the generic type parameter <c>T</c>.</summary>
    public bool Param { get; }

    /// <summary>The <c>WfnType.Malformed*</c> reason for an invalid generic application, otherwise <c>null</c>.</summary>
    public string? Malformed { get; }

    public override string ToString() => "any";
}
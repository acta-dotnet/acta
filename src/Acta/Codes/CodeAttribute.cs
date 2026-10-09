namespace Acta;

/// <summary>
/// Declares one persisted code on a member of a <see cref="CodeKindAttribute"/>-bearing enum. The
/// generator emits a <c>{EnumName}Extensions</c> companion exposing <c>CodeKind</c>, <c>Manifest</c>,
/// <c>FromId</c>, <c>IsKnownId</c>, <c>IsWritableId</c>, <c>Code</c>, and <c>Description</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class CodeAttribute(string code, string description) : Attribute
{
    /// <summary>Kebab string (most kinds) or dotted segments (<c>"event"</c>): <c>"done"</c>, <c>"job.cancelled"</c>, ...</summary>
    public string Code { get; } = code;

    /// <summary>
    /// Operator-readable description; populates <c>CodeValue.Description</c>.
    /// </summary>
    public string Description { get; } = description;

    /// <summary>Lifecycle flag. See <see cref="CodeLifecycle"/>.</summary>
    public CodeLifecycle Lifecycle { get; init; } = CodeLifecycle.Active;
}

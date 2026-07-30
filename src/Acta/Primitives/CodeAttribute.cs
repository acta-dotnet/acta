namespace Acta;

/// <summary>
/// Declares one persisted code on a member of a <see cref="CodeKindAttribute"/>-bearing enum. The
/// generator emits a <c>{EnumName}Extensions</c> companion exposing <c>CodeKind</c>, <c>Manifest</c>,
/// <c>FromId</c>, <c>IsKnownId</c>, <c>IsWritableId</c>, <c>Code</c>, and <c>Description</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class CodeAttribute : Attribute
{
    public CodeAttribute(string code, string description)
    {
        Code = code;
        Description = description;
    }

    /// <summary>Kebab string (most kinds) or dotted segments (<c>"event"</c>): <c>"done"</c>, <c>"job.cancelled"</c>, ...</summary>
    public string Code { get; }

    /// <summary>
    /// Operator-readable description; populates <c>CodeValue.Description</c>.
    /// </summary>
    public string Description { get; }

    /// <summary>Lifecycle flag. See <see cref="CodeLifecycleCode"/>.</summary>
    public CodeLifecycleCode Lifecycle { get; init; } = CodeLifecycleCode.Active;
}

namespace Acta;

/// <summary>
/// Declares the stable <c>code_kind</c> discriminator string of a code family for this
/// enum's code family. Required on every enum bearing <see cref="CodeAttribute"/> members; the
/// generator emits <c>ACTA0201</c> if absent. The key is explicit on the attribute, so renaming the
/// C# enum does not change the persisted catalog key.
/// </summary>
/// <remarks>
/// Value pattern: kebab segments, optionally dotted (mirrors the per-member <c>Code</c> rule used
/// by <c>JobEventCode</c>): <c>^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)*$</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class CodeKindAttribute : Attribute
{
    public CodeKindAttribute(string codeKind)
    {
        CodeKind = codeKind;
    }

    /// <summary>Kebab discriminator naming this family in docs, drift markers, and the generated manifests.</summary>
    public string CodeKind { get; }
}

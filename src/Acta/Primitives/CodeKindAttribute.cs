namespace Acta;

/// <summary>
/// Declares the stable <c>code_kind</c> discriminator string of a code family for this
/// enum's code family. Required on every enum bearing <see cref="CodeAttribute"/> members; the
/// generator emits <c>ACTA0201</c> if absent. The key is explicit on the attribute, so renaming the
/// C# enum does not change the persisted catalog key.
/// </summary>
/// <remarks>
/// Value pattern: kebab segments, optionally dotted (mirrors the per-member <c>Code</c> rule used
/// by <c>EventCode</c>): <c>^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)*$</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class CodeKindAttribute(string codeKind) : Attribute
{
    /// <summary>Kebab discriminator naming this family in docs, drift markers, and the generated manifests.</summary>
    public string CodeKind { get; } = codeKind;

    /// <summary>
    /// The family gains members between releases, so no migration should be needed to add one. Columns
    /// carrying an extensible family emit no <c>IN</c>-list CHECK, and reads of an id this build does not
    /// know return the family's <c>Unspecified = 0</c> member instead of throwing. Extensible families are
    /// therefore required to declare that member.
    /// </summary>
    /// <remarks>
    /// Reserve this for descriptive vocabularies (reasons, event kinds). Families that drive a routine,
    /// an index predicate, or an ORDER BY stay closed: an unrecognized status is a stuck row, and those
    /// vocabularies change rarely enough that a migration is the right cost.
    /// </remarks>
    public bool Extensible { get; init; }
}

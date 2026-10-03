namespace Acta;

/// <summary>
/// Caller-supplied per-tag wire row attached to an enqueued Job. Names are unique within a
/// request; <see cref="Value"/> is null for presence-only tags.
/// </summary>
public sealed record TagInput(string Name, string? Value = null)
{
    /// <summary>
    /// Normalize one tag: reject null, canonicalize the name as a user dotted-kebab identifier, and
    /// validate the preserved value. Shared by the request and operations enqueue paths so both accept
    /// exactly the same tags. <paramref name="paramName"/> is the caller's <c>Tags[i]</c> path.
    /// </summary>
    internal static TagInput Normalize(TagInput tag, string paramName)
    {
        if (tag is null)
        {
            throw new ArgumentException("Tag entries must not be null.", paramName);
        }

        var name = IdentifierSyntax.CanonicalizeUserDottedKebab(
            tag.Name,
            $"{paramName}.{nameof(Name)}",
            IdentifierSyntax.ExtendedMaxLength
        );
        if (tag.Value is { } value)
        {
            IdentifierSyntax.ValidateDisplayValue(value, $"{paramName}.{nameof(Value)}", IdentifierSyntax.ExtendedMaxLength);
        }

        return tag with
        {
            Name = name,
        };
    }
}

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

    /// <summary>
    /// Normalize a whole set: at most <see cref="TagLimits.MaxTagsPerTarget"/> tags, each as
    /// <see cref="Normalize"/> does it, no two with one name. <paramref name="setName"/> is the caller's
    /// path to the list, and <paramref name="owner"/> words the cap message ("job", "target").
    /// </summary>
    internal static TagInput[] NormalizeSet(IReadOnlyList<TagInput> tags, string setName, string owner)
    {
        if (tags.Count > TagLimits.MaxTagsPerTarget)
        {
            throw new ArgumentException($"A {owner} may carry at most {TagLimits.MaxTagsPerTarget} tags.", setName);
        }

        var normalized = new TagInput[tags.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tags.Count; i++)
        {
            normalized[i] = Normalize(tags[i], $"{setName}[{i}]");
            if (!names.Add(normalized[i].Name))
            {
                throw new ArgumentException($"Duplicate normalized tag name '{normalized[i].Name}'.", $"{setName}[{i}].{nameof(Name)}");
            }
        }

        return normalized;
    }
}

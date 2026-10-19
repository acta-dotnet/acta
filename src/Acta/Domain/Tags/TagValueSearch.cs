namespace Acta;

/// <summary>
/// Internal projection used for case-insensitive tag value lookup: the value trimmed and upper-cased. The original
/// tag value remains the only display/business value.
/// </summary>
internal static class TagValueSearch
{
    internal const int MaxLength = NameSyntax.ExtendedMaxLength;

    public static string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length > MaxLength
            ? throw new ArgumentException(
                $"Tag value search projection length {normalized.Length} exceeds the {MaxLength}-char limit.",
                nameof(value)
            )
            : normalized;
    }
}

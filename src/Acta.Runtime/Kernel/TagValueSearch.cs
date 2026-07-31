namespace Acta.Runtime.Kernel;

/// <summary>
/// Internal projection used for case-insensitive tag value lookup. The original tag value remains
/// the only display/business value.
/// </summary>
internal static class TagValueSearch
{
    internal const int MaxLength = IdentifierSyntax.ExtendedMaxLength;

    public static string? Normalize(string? value, string paramName = "value")
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.ToUpperInvariant();
        return normalized.Length > MaxLength
            ? throw new ArgumentException(
                $"Tag value search projection length {normalized.Length} exceeds the {MaxLength}-char limit.",
                paramName
            )
            : normalized;
    }
}

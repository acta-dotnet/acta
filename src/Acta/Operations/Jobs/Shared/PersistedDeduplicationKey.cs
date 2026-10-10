namespace Acta;

/// <summary>Normalizes keys read from storage without applying user-key reserved-prefix rules.</summary>
internal static class PersistedDeduplicationKey
{
    public static string? Normalize(string? value) => value is null ? null : IdentifierSyntax.NormalizeKeyLookup(value, nameof(value));
}

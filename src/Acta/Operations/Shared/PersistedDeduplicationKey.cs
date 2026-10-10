namespace Acta;

/// <summary>Normalizes keys read from storage without the enqueue-time <c>sys.</c> reservation, so system jobs' keys pass.</summary>
internal static class PersistedDeduplicationKey
{
    public static string? Normalize(string? value) => value is null ? null : KeySyntax.Canonicalize(value, nameof(value));
}

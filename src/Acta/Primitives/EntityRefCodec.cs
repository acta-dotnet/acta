namespace Acta;

/// <summary>
/// The shared render/parse core behind every minted entity ref (<c>job_</c> / <c>alr_</c> / <c>wrk_</c>): the
/// prefix plus the Guid's 32 lowercase hex digits, its <c>N</c> format. One implementation, so every ref type renders
/// and parses identically; the typed shells (<see cref="JobRef"/>, <see cref="AlertRef"/>, <see cref="WorkerRef"/>)
/// carry only their prefix and the Guid conversions.
/// </summary>
internal static class EntityRefCodec
{
    public static string Render(string prefix, Guid value) => prefix + value.ToString("N");

    /// <summary>
    /// Parse a ref: the exact prefix, then 32 hex digits in any casing. Another entity's prefix, the dashed Guid, and
    /// the bare Guid never parse.
    /// </summary>
    public static bool TryParse(string prefix, string? value, out Guid parsed)
    {
        parsed = default;

        // The exact length, since Guid parsing would otherwise forgive surrounding whitespace.
        return value is not null
            && value.Length == prefix.Length + 32
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(value.AsSpan(prefix.Length), "N", out parsed);
    }
}

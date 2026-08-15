namespace Acta;

/// <summary>
/// The shared render/parse core behind every minted entity ref (<c>job_</c> / <c>alr_</c> /
/// <c>wrk_</c>): a fixed three-letter prefix plus 26 lowercase Crockford Base32 characters
/// encoding the canonical big-endian UUID bytes. One implementation, so every ref type renders
/// and parses identically; the typed shells (<see cref="JobRef"/>, <see cref="AlertRef"/>,
/// <see cref="WorkerRef"/>) carry only their prefix and the Guid conversions.
/// </summary>
internal static class EntityRefCodec
{
    public static string Render(string prefix, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        return prefix + CrockfordBase32.EncodeLower(bytes);
    }

    /// <summary>
    /// Parse one ref form: exact length, case-insensitive prefix match, Crockford payload (with
    /// the o/i/l aliases). A value carrying another entity's prefix fails the prefix match, so
    /// each typed ref only ever parses values minted for its own entity.
    /// </summary>
    public static bool TryParse(string prefix, string? value, out Guid parsed)
    {
        parsed = default;
        if (value is null || value.Length != prefix.Length + CrockfordBase32.EncodedLength)
        {
            return false;
        }

        if (!value.AsSpan(0, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!CrockfordBase32.TryDecode(value.AsSpan(prefix.Length), bytes))
        {
            return false;
        }

        parsed = new Guid(bytes, bigEndian: true);
        return true;
    }
}

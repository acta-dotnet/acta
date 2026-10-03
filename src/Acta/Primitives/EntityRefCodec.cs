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
    /// Parse a ref: the rendered form (case-insensitive prefix plus a Crockford payload, with the o/i/l
    /// aliases), or the same Guid written plainly (<c>D</c> or <c>N</c> format), with the prefix or without
    /// it. A value carrying another entity's prefix never parses, so a typed ref only ever accepts values
    /// minted for its own entity or a bare Guid its caller has already typed.
    /// </summary>
    public static bool TryParse(string prefix, string? value, out Guid parsed)
    {
        parsed = default;
        if (value is null)
        {
            return false;
        }

        var body = value.AsSpan();
        if (body.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            body = body[prefix.Length..];
            if (body.Length == CrockfordBase32.EncodedLength)
            {
                Span<byte> bytes = stackalloc byte[16];
                if (!CrockfordBase32.TryDecode(body, bytes))
                {
                    return false;
                }

                parsed = new Guid(bytes, bigEndian: true);
                return true;
            }
        }

        // Exact lengths, since Guid parsing would otherwise forgive surrounding whitespace.
        return body.Length switch
        {
            36 => Guid.TryParseExact(body, "D", out parsed),
            32 => Guid.TryParseExact(body, "N", out parsed),
            _ => false,
        };
    }
}

namespace Acta.Runtime.Kernel;

/// <summary>
/// Caps free-form operator and diagnostic text at the application boundary so it fits the bounded
/// column it lands in (<c>reason_message</c>, alert message, step error). The single
/// truncator referenced by those entities' column docs; callers pass the column's declared length
/// (typically from <c>ActaSchema</c>) so the limit has one source of truth in the entity.
/// </summary>
internal static class MessageTruncator
{
    /// <summary>
    /// Returns text no provider refuses: unpaired surrogates and U+0000 are replaced with U+FFFD and
    /// a cut never splits a surrogate pair. Npgsql's UTF-8 encoder throws on a lone surrogate and
    /// PostgreSQL rejects NUL in text outright, while SqlClient stores both and SQLite replaces the
    /// surrogate - so without the replacement the same reason message succeeds, round-trips, or
    /// fails depending on the provider, and a raw cut at the cap manufactures a lone surrogate out
    /// of a valid emoji at the boundary. A failed write here is worst on the paths that carry reason
    /// text: the completion or alert recording a failure would itself fail. Text this returns can
    /// still differ per provider where a column is narrower than Unicode (a SQL Server varchar
    /// folds non-ASCII); that is the column's contract, not this cut's.
    /// </summary>
    public static string? Truncate(this string? value, int? maxLength)
    {
        if (value is null)
        {
            return null;
        }

        // Only the prefix that can survive the cut is sanitized: index max is never emitted, and the
        // pair check at max - 1 still sees its partner at max, so the result is identical to
        // sanitizing the whole value without scanning and copying a megabyte to keep 512 chars.
        var window = maxLength is { } cap && value.Length - 1 > cap ? value[..(cap + 1)] : value;
        var sanitized = ReplaceUnstorable(window);
        if (maxLength is not { } max || sanitized.Length <= max)
        {
            return sanitized;
        }

        // Sanitization left only well-formed pairs, so a bad cut can only land after a high surrogate.
        return max > 0 && char.IsHighSurrogate(sanitized[max - 1]) ? sanitized[..(max - 1)] : sanitized[..max];
    }

    private static string ReplaceUnstorable(string value)
    {
        // Most values carry neither surrogates nor NUL; scan first so the common case allocates nothing.
        var first = 0;
        while (first < value.Length && !char.IsSurrogate(value[first]) && value[first] != '\0')
        {
            first++;
        }
        if (first == value.Length)
        {
            return value;
        }

        var chars = value.ToCharArray();
        for (var i = first; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            {
                i++;
                continue;
            }
            if (char.IsSurrogate(chars[i]) || chars[i] == '\0')
            {
                chars[i] = '\uFFFD';
            }
        }
        return new string(chars);
    }
}

/// <summary>
/// Provider-independent product limits for free-form text validated before a store call. Relational
/// schema tests pin these constants to the corresponding column declarations.
/// </summary>
internal static class ActaTextLimits
{
    public const int ActorKey = 128;
    public const int ReasonMessage = 512;
    public const int AlertChannelName = 128;
    public const int AlertDedupeKey = 512;
    public const int AlertTitle = 512;
    public const int AlertMessage = 512;
    public const int DefinitionBackoff = 64;
    public const int DefinitionDisplayName = 128;
    public const int DefinitionDescription = 512;
    public const int DefinitionRunbookUrl = 512;
    public const int ScheduleReasonMessage = 512;
}

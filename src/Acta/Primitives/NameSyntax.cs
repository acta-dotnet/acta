namespace Acta;

/// <summary>
/// The rule for a name, Acta's own vocabulary: namespaces, job names, schedules, signals, steps, lanes, tenants, rate
/// meters, channels, and tag names. A name is ASCII dotted kebab: one or more <c>[a-z][a-z0-9-]*</c> segments without
/// a trailing <c>-</c>, joined by <c>.</c>. Names are case-insensitive: <see cref="Canonicalize"/> trims and lowercases
/// the input, so every stored name is lowercase and <c>Billing.Send</c> is <c>billing.send</c>.
/// </summary>
public static class NameSyntax
{
    /// <summary>The system reservation prefix: names under it belong to Acta (<c>sys.retention</c>).</summary>
    public const string SystemPrefix = "sys.";

    /// <summary>The bare reserved name: the seeded <c>sys</c> namespace.</summary>
    public const string ReservedSystemName = "sys";

    /// <summary>The seeded <c>sys</c> namespace's id: cross-namespace audit events and the shared calendar catalog live under it.</summary>
    public const int SystemNamespaceId = 1;

    /// <summary>Default length cap (64 chars): namespaces, payload format names, and worker names.</summary>
    public const int DefaultMaxLength = 64;

    /// <summary>Extended length cap (128 chars): job names, slot names (variable, signal, timer, step), lanes, tenants,
    /// channel and tag names, and caller-supplied keys.</summary>
    public const int ExtendedMaxLength = 128;

    /// <summary>True if <paramref name="value"/> starts with <see cref="SystemPrefix"/>.</summary>
    public static bool StartsWithSystemPrefix(string value) =>
        value is not null && value.StartsWith(SystemPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Returns the stored form of a name: <paramref name="value"/> trimmed and lowercased. Throws
    /// <see cref="ArgumentNullException"/> when it is null, and <see cref="ArgumentException"/> unless the result is a
    /// dotted kebab name of at most <paramref name="maxLength"/> characters. With <paramref name="reserveSystem"/>, as
    /// every registration of a caller's name sets it, the bare <c>sys</c> and any name under <c>sys.</c> are rejected
    /// too; lookups leave it off so they still resolve the seeded <c>sys</c> namespace and the system jobs.
    /// </summary>
    public static string Canonicalize(string value, string paramName, int maxLength = DefaultMaxLength, bool reserveSystem = false)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var canonical = value.Trim().ToLowerInvariant();
        if (canonical.Length == 0)
        {
            throw new ArgumentException("Identifier must not be empty.", paramName);
        }

        if (canonical.Length > maxLength)
        {
            throw new ArgumentException($"Identifier must not exceed {maxLength} characters (got {canonical.Length}).", paramName);
        }

        if (!IsDottedKebab(canonical))
        {
            throw new ArgumentException(
                $"Identifier '{value}' is not kebab-case or dotted-kebab (each segment must match `[a-z][a-z0-9-]*`).",
                paramName
            );
        }

        if (reserveSystem && canonical == ReservedSystemName)
        {
            throw new ArgumentException(
                $"Identifier '{value}' is reserved for system-internal names (the bare name '{ReservedSystemName}' and the '{SystemPrefix}' prefix are both reserved).",
                paramName
            );
        }

        if (reserveSystem && StartsWithSystemPrefix(canonical))
        {
            throw new ArgumentException(
                $"Identifier '{value}' uses the reserved system prefix '{SystemPrefix}': reserved for system-internal names.",
                paramName
            );
        }

        return canonical;
    }

    private static bool IsDottedKebab(string value)
    {
        var start = 0;
        while (true)
        {
            var dot = value.IndexOf('.', start);
            var end = dot < 0 ? value.Length : dot;
            if (!IsKebab(value.AsSpan(start, end - start)))
            {
                return false;
            }

            if (dot < 0)
            {
                return true;
            }

            start = dot + 1;
        }
    }

    private static bool IsKebab(ReadOnlySpan<char> segment)
    {
        if (segment.Length == 0 || segment[0] is < 'a' or > 'z' || segment[^1] == '-')
        {
            return false;
        }

        foreach (var c in segment[1..])
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }
}

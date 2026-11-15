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

    /// <summary>Extended length cap (128 chars): job names, checkpoint names (variable, signal, timer), step names, lanes, tenants,
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
        return Problem(value, canonical, maxLength, reserveSystem) is { } problem
            ? throw new ArgumentException(problem, paramName)
            : canonical;
    }

    /// <summary>
    /// <see cref="Canonicalize"/> for a name a handler records (a step, variable, signal, timer, or child): a name that
    /// breaks the rule throws <see cref="InvalidDurableNameException"/>, which fails the job at once.
    /// </summary>
    internal static string CanonicalizeDurable(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var canonical = value.Trim().ToLowerInvariant();
        return Problem(value, canonical, ExtendedMaxLength, reserveSystem: true) is { } problem
            ? throw new InvalidDurableNameException(problem, paramName)
            : canonical;
    }

    private static string? Problem(string value, string canonical, int maxLength, bool reserveSystem) =>
        canonical.Length == 0 ? "Identifier must not be empty."
        : canonical.Length > maxLength ? $"Identifier must not exceed {maxLength} characters (got {canonical.Length})."
        : !IsDottedKebab(canonical) ? $"Identifier '{value}' is not kebab-case or dotted-kebab (each segment must match `[a-z][a-z0-9-]*`)."
        : reserveSystem && canonical == ReservedSystemName
            ? $"Identifier '{value}' is reserved for system-internal names (the bare name '{ReservedSystemName}' and the '{SystemPrefix}' prefix are both reserved)."
        : reserveSystem && StartsWithSystemPrefix(canonical)
            ? $"Identifier '{value}' uses the reserved system prefix '{SystemPrefix}': reserved for system-internal names."
        : null;

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

namespace Acta;

/// <summary>
/// Identifier-syntax rules for Acta-owned names, Acta-normalized keys, preserved external tokens,
/// bare-SQL shapes, length caps, and the system <c>sys.</c> reservation prefix.
/// </summary>
public static class IdentifierSyntax
{
    /// <summary>System reservation prefix (<c>sys.</c>). User-supplied identifiers must not start with this.</summary>
    public const string SystemPrefix = "sys.";

    /// <summary>Bare system-reserved name (the seeded <c>sys</c> namespace row, id=1). Registration
    /// validators reject this exact value in addition to anything under <see cref="SystemPrefix"/>.</summary>
    public const string ReservedSystemName = "sys";

    /// <summary>Default length cap for kebab identifiers (64 chars). Matches the operator-stable
    /// identifier tier: payload format names, channel names, schedule names, tag keys, reason codes.</summary>
    public const int DefaultMaxLength = 64;

    /// <summary>Extended length cap (128 chars). Matches the longer-identifier tier: job names,
    /// substrate slot names (variable, signal, timer, step), deduplication keys, exclusive keys.</summary>
    public const int ExtendedMaxLength = 128;

    /// <summary>
    /// Length cap for bare SQL identifiers (63 chars). Sized to PostgreSQL's
    /// <c>NAMEDATALEN - 1</c> so any name that validates here is safely under both PG (63) and SQL
    /// Server (128) identifier limits.
    /// </summary>
    public const int BareIdentifierMaxLength = 63;

    // ----- Predicates (pure, no throw) -----

    /// <summary>True if <paramref name="value"/> is a strict single-segment kebab identifier:
    /// <c>^[a-z][a-z0-9-]*$</c>, no dots, no leading / trailing <c>-</c>.</summary>
    public static bool IsKebab(string value) => IsKebabSegment(value);

    /// <summary>True if <paramref name="value"/> is a bare SQL identifier:
    /// <c>^[a-z][a-z0-9_]*$</c>. Permits underscores instead of hyphens; used for schema names,
    /// table names, and column names, anywhere that gets substituted into SQL unquoted ("bare" means
    /// it needs no delimited-identifier quoting on either provider).</summary>
    public static bool IsBareIdentifier(string value) => IsBareIdentifierSegment(value);

    /// <summary>True if <paramref name="value"/> is an allowed dev-convenience database name:
    /// <c>^[a-z][a-z0-9_-]*$</c>. Wider than a bare identifier (hyphens permitted) but still free of
    /// the SQL delimiter characters, so it is safe to interpolate into quoted CREATE DATABASE DDL.</summary>
    public static bool IsDatabaseName(string value) => IsDatabaseNameSegment(value);

    /// <summary>True if <paramref name="value"/> is one or more kebab segments separated by <c>.</c>
    /// (e.g., <c>"json"</c>, <c>"job.execution.finished"</c>, <c>"com.acme.priority"</c>).</summary>
    public static bool IsDottedKebab(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var segment in value.Split('.'))
        {
            if (!IsKebabSegment(segment))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True if <paramref name="value"/> starts with the system reservation prefix.</summary>
    public static bool StartsWithSystemPrefix(string value) =>
        value is not null && value.StartsWith(SystemPrefix, StringComparison.Ordinal);

    /// <summary>True if <paramref name="value"/> is the bare reserved name (<see
    /// cref="ReservedSystemName"/>) or starts with the system reservation prefix (<see
    /// cref="SystemPrefix"/>). Used by registration validators (<see cref="ValidateUserKebab"/>); read
    /// paths that must stay permissive of the seeded <c>sys</c> namespace (dashboard filters) validate
    /// shape only and do not call this.</summary>
    public static bool IsReservedSystemName(string value) => value == ReservedSystemName || StartsWithSystemPrefix(value);

    // ----- Validators (throw ArgumentException on failure) -----

    /// <summary>Validate strict single-segment kebab shape + length. Throws on dots, on leading /
    /// trailing <c>-</c>, on uppercase letters, on length overflow, or on empty.</summary>
    public static void ValidateKebab(string value, string paramName, int maxLength = DefaultMaxLength)
    {
        EnsureNonEmptyAndLength(value, paramName, maxLength);
        if (!IsKebab(value))
        {
            throw new ArgumentException(
                $"Identifier '{value}' is not kebab-case (`[a-z][a-z0-9-]*`, single segment, no leading/trailing '-').",
                paramName
            );
        }
    }

    /// <summary>
    /// Validate bare SQL-identifier shape + length: <c>^[a-z][a-z0-9_]*$</c>, max
    /// <see cref="BareIdentifierMaxLength"/> chars by default. Required at every site that
    /// substitutes an identifier directly into unquoted SQL (notably the <c>{{schema}}</c>
    /// substitution token in M001). "Bare" = the identifier needs no delimited-identifier quoting
    /// on either provider; rejecting unsafe names here is the canonical guard against SQL
    /// injection through schema / table / column names.
    /// </summary>
    public static void ValidateBareIdentifier(string value, string paramName, int maxLength = BareIdentifierMaxLength)
    {
        EnsureNonEmptyAndLength(value, paramName, maxLength);
        if (!IsBareIdentifier(value))
        {
            throw new ArgumentException(
                $"Identifier '{value}' is not a bare SQL identifier (`[a-z][a-z0-9_]*`, starts with a letter, no uppercase, no hyphens).",
                paramName
            );
        }
    }

    /// <summary>
    /// Validate an operator-supplied database name for the dev-convenience CREATE DATABASE path.
    /// Permits lowercase letters, digits, '_' and '-' (so hyphenated names like 'acta-pg' pass),
    /// starts with a letter, max <see cref="BareIdentifierMaxLength"/> chars. Rejects the delimiter
    /// characters (']', single-quote, double-quote) that would otherwise break out of the bracket,
    /// string-literal, and quoted-identifier contexts the name is interpolated into, keeping that
    /// interpolation injection-safe.
    /// </summary>
    public static void ValidateDatabaseName(string value, string paramName, int maxLength = BareIdentifierMaxLength)
    {
        EnsureNonEmptyAndLength(value, paramName, maxLength);
        if (!IsDatabaseName(value))
        {
            throw new ArgumentException(
                $"Database name '{value}' is not allowed (`[a-z][a-z0-9_-]*`, starts with a lowercase letter; punctuation that breaks out of quoted DDL is rejected).",
                paramName
            );
        }
    }

    /// <summary>Validate kebab-or-dotted-kebab shape + length. Permits multiple kebab segments
    /// separated by <c>.</c>. Used for event-type codes, tag keys, and other identifiers that
    /// support vendor namespacing.</summary>
    public static void ValidateDottedKebab(string value, string paramName, int maxLength = DefaultMaxLength)
    {
        EnsureNonEmptyAndLength(value, paramName, maxLength);
        if (!IsDottedKebab(value))
        {
            throw new ArgumentException(
                $"Identifier '{value}' is not kebab-case or dotted-kebab (each segment must match `[a-z][a-z0-9-]*`).",
                paramName
            );
        }
    }

    /// <summary>Validate strict kebab + length + reject the reserved system name (<see
    /// cref="ReservedSystemName"/>, e.g. the seeded <c>sys</c> namespace) and identifiers starting
    /// with the system reservation prefix (<see cref="SystemPrefix"/>). Used at every
    /// user-supplied-name *registration* boundary (namespace names, handler names, step slot names,
    /// user variable / signal names, user tag keys). Read/lookup paths (dashboard filters) validate
    /// shape only and stay permissive of <c>sys</c>.</summary>
    public static void ValidateUserKebab(string value, string paramName, int maxLength = DefaultMaxLength)
    {
        ValidateKebab(value, paramName, maxLength);
        if (IsReservedSystemName(value))
        {
            throw new ArgumentException(
                $"Identifier '{value}' is reserved for system-internal names (the bare name '{ReservedSystemName}' and the '{SystemPrefix}' prefix are both reserved).",
                paramName
            );
        }
    }

    /// <summary>
    /// Validate dotted-kebab + length + reject identifiers starting with the system
    /// reservation prefix (<see cref="SystemPrefix"/>). Symmetric with
    /// <see cref="ValidateUserKebab"/> but permits multiple kebab segments joined by <c>.</c>
    /// for vendor / system / integration namespacing (e.g. <c>env.prod</c>,
    /// <c>com.acme.tier</c>). Used at user-supplied tag-name boundaries.
    /// </summary>
    public static void ValidateUserDottedKebab(string value, string paramName, int maxLength = DefaultMaxLength)
    {
        ValidateDottedKebab(value, paramName, maxLength);
        if (StartsWithSystemPrefix(value))
        {
            throw new ArgumentException(
                $"Identifier '{value}' uses the reserved system prefix '{SystemPrefix}': reserved for system-internal names.",
                paramName
            );
        }
    }

    /// <summary>
    /// Validate an external token (correlation id, actor id, host/version field): non-null,
    /// non-whitespace, at most <paramref name="maxLength"/> chars, and free of control characters.
    /// The value is preserved exactly; Acta does not lowercase or otherwise canonicalize it.
    /// </summary>
    internal static void ValidateExternalToken(string value, string paramName, int maxLength = ExtendedMaxLength)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Token must be non-whitespace.", paramName);
        }
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Token length {value.Length} exceeds the {maxLength}-char limit.", paramName);
        }

        EnsureNoControlChars(value, paramName, "Token");
    }

    /// <summary>
    /// Validate a preserved display/search value. The value is never canonicalized; this guard only
    /// enforces null, length, and control-character safety.
    /// </summary>
    internal static void ValidateDisplayValue(string value, string paramName, int maxLength = ExtendedMaxLength)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Value length {value.Length} exceeds the {maxLength}-char limit.", paramName);
        }

        EnsureNoControlChars(value, paramName, "Value");
    }

    // ----- Canonicalizers and normalizers -----

    /// <summary>Normalize a string by folding it to invariant lowercase. Prefer the name/key-specific
    /// helpers below at Acta boundaries so each caller uses the right validation and normalization policy.</summary>
    public static string NormalizeLowerInvariant(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToLowerInvariant();
    }

    /// <summary>Validate <see cref="ValidateKebab"/>, returning the already-lowercase identifier.</summary>
    public static string CanonicalizeKebab(string value, string paramName, int maxLength = DefaultMaxLength)
    {
        ValidateKebab(value, paramName, maxLength);
        return value;
    }

    /// <summary>Validate <see cref="ValidateUserKebab"/>, returning the already-lowercase identifier.</summary>
    public static string CanonicalizeUserKebab(string value, string paramName, int maxLength = DefaultMaxLength)
    {
        ValidateUserKebab(value, paramName, maxLength);
        return value;
    }

    /// <summary>Validate <see cref="ValidateUserDottedKebab"/>, returning the already-lowercase identifier.</summary>
    public static string CanonicalizeUserDottedKebab(string value, string paramName, int maxLength = DefaultMaxLength)
    {
        ValidateUserDottedKebab(value, paramName, maxLength);
        return value;
    }

    /// <summary>
    /// Normalize an Acta definition name: trim, fold to invariant lowercase, and validate dotted-kebab
    /// shape without applying the user-only <c>sys.</c> reservation. This is a component normalizer;
    /// callers composing a user key must still validate the completed key with <see cref="NormalizeKey"/>.
    /// </summary>
    public static string NormalizeName(string value, string paramName, int maxLength = ExtendedMaxLength)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var normalized = value.Trim().ToLowerInvariant();
        ValidateDottedKebab(normalized, paramName, maxLength);
        return normalized;
    }

    /// <summary>
    /// Normalize an Acta-owned equality key: trim, validate the practical printable-ASCII key alphabet,
    /// reject the reserved <c>sys.</c> prefix, then lowercase invariant. Registration/write boundaries
    /// (deduplication keys supplied at enqueue time) use this; lookup paths that must resolve system rows
    /// use <see cref="NormalizeKeyLookup"/> instead.
    /// </summary>
    public static string NormalizeKey(string value, string paramName, int maxLength = ExtendedMaxLength)
    {
        var canonical = NormalizeKeyLookup(value, paramName, maxLength);
        if (StartsWithSystemPrefix(canonical))
        {
            throw new ArgumentException(
                $"Key '{value.Trim()}' uses the reserved system prefix '{SystemPrefix}': reserved for system-internal names.",
                paramName
            );
        }

        return canonical;
    }

    /// <summary>
    /// Normalize an Acta-owned equality key for lookup paths: same trim/charset/length/lowercase rules
    /// as <see cref="NormalizeKey"/> but WITHOUT the <c>sys.</c>-prefix rejection, so a lookup keyed on a
    /// system row's deduplication key (e.g. <c>sys.retention</c>) can still resolve. Never use this at a
    /// registration/write boundary: the <c>sys.</c> reservation must stay enforced there.
    /// </summary>
    public static string NormalizeKeyLookup(string value, string paramName, int maxLength = ExtendedMaxLength)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Key must be non-whitespace.", paramName);
        }
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"Key length {trimmed.Length} exceeds the {maxLength}-char limit.", paramName);
        }

        foreach (var c in trimmed)
        {
            if (!IsKeyChar(c))
            {
                throw new ArgumentException(
                    "Key must contain only printable ASCII key characters (a-z, A-Z, 0-9, '.', '-', '_', ':', '/', '@', '+', '=').",
                    paramName
                );
            }
        }

        return trimmed.ToLowerInvariant();
    }

    // ----- Internals -----

    private static void EnsureNonEmptyAndLength(string value, string paramName, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Length == 0)
        {
            throw new ArgumentException("Identifier must not be empty.", paramName);
        }

        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Identifier must not exceed {maxLength} characters (got {value.Length}).", paramName);
        }
    }

    private static void EnsureNoControlChars(string value, string paramName, string noun)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException($"{noun} must not contain control characters.", paramName);
            }
        }
    }

    private static bool IsKebabSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return false;
        }

        if (!IsLowerLetter(segment[0]))
        {
            return false;
        }

        if (segment[^1] == '-')
        {
            return false;
        }

        for (var i = 1; i < segment.Length; i++)
        {
            var c = segment[i];
            if (!IsLowerLetter(c) && !IsDigit(c) && c != '-')
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsBareIdentifierSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return false;
        }

        if (!IsLowerLetter(segment[0]))
        {
            return false;
        }

        for (var i = 1; i < segment.Length; i++)
        {
            var c = segment[i];
            if (!IsLowerLetter(c) && !IsDigit(c) && c != '_')
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsDatabaseNameSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return false;
        }

        if (!IsLowerLetter(segment[0]))
        {
            return false;
        }

        for (var i = 1; i < segment.Length; i++)
        {
            var c = segment[i];
            if (!IsLowerLetter(c) && !IsDigit(c) && c != '_' && c != '-')
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsLowerLetter(char c) => c >= 'a' && c <= 'z';

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static bool IsAsciiLetter(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    private static bool IsKeyChar(char c) => IsAsciiLetter(c) || IsDigit(c) || c is '.' or '-' or '_' or ':' or '/' or '@' or '+' or '=';
}

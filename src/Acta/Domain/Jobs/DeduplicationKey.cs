using System.Globalization;

namespace Acta;

/// <summary>
/// Composes stable values for <see cref="EnqueueJobRequest.DeduplicationKey"/>. The business key keeps its case, so
/// two ids that differ only by case are two keys; a definition or tenant name part folds to lowercase like every name.
/// The completed key holds to the <see cref="KeySyntax"/> rule and the storage length, and may not start with the
/// reserved <c>sys.</c> prefix in any case.
/// </summary>
public static class DeduplicationKey
{
    /// <summary>Maximum key length, delegated to the shared extended identifier limit.</summary>
    public const int MaxLength = NameSyntax.ExtendedMaxLength;

    /// <summary>
    /// Compose a definition-qualified key: <c>&lt;definitionName&gt;:&lt;businessKey&gt;</c>.
    /// </summary>
    public static string ForDefinition(string definitionName, string businessKey)
    {
        var definition = NameSyntax.Canonicalize(definitionName, nameof(definitionName), NameSyntax.ExtendedMaxLength);
        var business = KeySyntax.Canonicalize(businessKey, nameof(businessKey));

        return Canonicalize($"{definition}:{business}", nameof(businessKey));
    }

    /// <summary>
    /// Compose a tenant-relative key, <c>&lt;tenantName&gt;:&lt;businessKey&gt;</c>, so equal business keys under
    /// different tenants never collide. Deduplication and concurrency keys are both namespace-scoped opaque strings,
    /// so the same composition serves <see cref="EnqueueJobRequest.ConcurrencyKey"/> values, and the result nests as
    /// the business key of <see cref="ForDefinition"/> when definition and tenant qualification are both wanted. The
    /// tenant part is a name, which never holds <c>:</c>, so two tenants never compose to one key.
    /// </summary>
    public static string ForTenant(string tenantName, string businessKey)
    {
        var tenant = NameSyntax.Canonicalize(tenantName, nameof(tenantName), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        var business = KeySyntax.Canonicalize(businessKey, nameof(businessKey));

        return Canonicalize($"{tenant}:{business}", nameof(businessKey));
    }

    /// <summary>
    /// Compose a key that intentionally deduplicates across definitions in the same job namespace.
    /// </summary>
    /// <remarks>
    /// Time-bucket helpers are definition-qualified. A combined cross-definition time-bucket API is
    /// intentionally deferred; applications needing that scope should derive the bucketed business
    /// key explicitly and pass it here.
    /// </remarks>
    public static string AcrossDefinitions(string businessKey) => Canonicalize(businessKey, nameof(businessKey));

    /// <summary>Compose a definition-qualified key for the current UTC hour.</summary>
    public static string PerHour(string definitionName, string businessKey) => PerHour(definitionName, businessKey, DateTimeOffset.UtcNow);

    /// <summary>Compose a definition-qualified key for the hour containing <paramref name="instant"/>.</summary>
    public static string PerHour(string definitionName, string businessKey, DateTimeOffset instant) =>
        PerTimeBucket(definitionName, businessKey, instant, TimeSpan.FromHours(1));

    /// <summary>Compose a definition-qualified key for the current UTC day.</summary>
    public static string PerDay(string definitionName, string businessKey) =>
        PerDay(definitionName, businessKey, DateOnly.FromDateTime(DateTime.UtcNow));

    /// <summary>Compose a definition-qualified key for the supplied logical day.</summary>
    public static string PerDay(string definitionName, string businessKey, DateOnly day) =>
        PerTimeBucket(
            definitionName,
            businessKey,
            new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            TimeSpan.FromDays(1)
        );

    /// <summary>Compose a definition-qualified key for the current UTC time bucket.</summary>
    public static string PerTimeBucket(string definitionName, string businessKey, TimeSpan bucketSize) =>
        PerTimeBucket(definitionName, businessKey, DateTimeOffset.UtcNow, bucketSize);

    /// <summary>
    /// Compose a definition-qualified key for the bucket containing <paramref name="instant"/>.
    /// Equivalent instants with different offsets produce the same signed Unix-epoch bucket ordinal.
    /// </summary>
    public static string PerTimeBucket(string definitionName, string businessKey, DateTimeOffset instant, TimeSpan bucketSize)
    {
        var baseKey = ForDefinition(definitionName, businessKey);
        var ordinal = GetBucketOrdinal(instant, bucketSize);
        var size = FormatBucketSize(bucketSize);

        return Canonicalize($"{baseKey}:bucket:{size}:{ordinal.ToString(CultureInfo.InvariantCulture)}", nameof(businessKey));
    }

    /// <summary>
    /// Returns the stored form of a deduplication key at enqueue: the <see cref="KeySyntax"/> rule, and no key under
    /// <c>sys.</c> in any case, because the deduplication index also holds the system jobs' keys (<c>sys.retention</c>).
    /// Lookups use <see cref="KeySyntax.Canonicalize"/> alone, so a system job's key still resolves.
    /// </summary>
    internal static string Canonicalize(string value, string paramName)
    {
        var canonical = KeySyntax.Canonicalize(value, paramName, MaxLength);
        return canonical.StartsWith(NameSyntax.SystemPrefix, StringComparison.OrdinalIgnoreCase)
            ? throw new ArgumentException(
                $"Key '{canonical}' uses the reserved system prefix '{NameSyntax.SystemPrefix}': reserved for system-internal names.",
                paramName
            )
            : canonical;
    }

    private static long GetBucketOrdinal(DateTimeOffset instant, TimeSpan bucketSize)
    {
        if (bucketSize <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketSize), bucketSize, "Bucket size must be greater than zero.");
        }

        var ticksSinceEpoch = instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var ordinal = ticksSinceEpoch / bucketSize.Ticks;

        if (ticksSinceEpoch < 0 && ticksSinceEpoch % bucketSize.Ticks != 0)
        {
            ordinal--;
        }

        return ordinal;
    }

    private static string FormatBucketSize(TimeSpan bucketSize)
    {
        if (bucketSize.Ticks % TimeSpan.TicksPerDay == 0)
        {
            return $"{(bucketSize.Ticks / TimeSpan.TicksPerDay).ToString(CultureInfo.InvariantCulture)}d";
        }

        if (bucketSize.Ticks % TimeSpan.TicksPerHour == 0)
        {
            return $"{(bucketSize.Ticks / TimeSpan.TicksPerHour).ToString(CultureInfo.InvariantCulture)}h";
        }

        if (bucketSize.Ticks % TimeSpan.TicksPerMinute == 0)
        {
            return $"{(bucketSize.Ticks / TimeSpan.TicksPerMinute).ToString(CultureInfo.InvariantCulture)}m";
        }

        if (bucketSize.Ticks % TimeSpan.TicksPerSecond == 0)
        {
            return $"{(bucketSize.Ticks / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture)}s";
        }

        return bucketSize.Ticks % TimeSpan.TicksPerMillisecond == 0
            ? $"{(bucketSize.Ticks / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture)}ms"
            : $"{bucketSize.Ticks.ToString(CultureInfo.InvariantCulture)}ticks";
    }
}

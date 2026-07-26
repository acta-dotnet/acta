using System.Globalization;

namespace Acta;

/// <summary>
/// Composes stable, normalized string values for <see cref="JobEnqueueRequest.DeduplicationKey"/>.
/// Component normalization permits system-shaped text inside a definition-qualified key; validation
/// of the completed user key enforces the reserved <c>sys.</c> prefix and storage length boundary.
/// </summary>
public static class DeduplicationKey
{
    /// <summary>Maximum key length, delegated to the shared extended identifier limit.</summary>
    public const int MaxLength = IdentifierSyntax.ExtendedMaxLength;

    /// <summary>
    /// Compose a definition-qualified key: <c>&lt;definitionName&gt;:&lt;businessKey&gt;</c>.
    /// </summary>
    public static string ForDefinition(string definitionName, string businessKey)
    {
        var definition = IdentifierSyntax.NormalizeName(definitionName, nameof(definitionName));
        var business = IdentifierSyntax.NormalizeKeyLookup(businessKey, nameof(businessKey));

        return IdentifierSyntax.NormalizeKey($"{definition}:{business}", nameof(businessKey), MaxLength);
    }

    /// <summary>
    /// Compose a tenant-relative key: <c>&lt;tenantKey&gt;:&lt;businessKey&gt;</c>, so equal business
    /// keys under different tenants never collide. Deduplication and exclusive keys are both
    /// namespace-scoped opaque strings, so the same composition serves
    /// <see cref="JobEnqueueRequest.ExclusiveKey"/> values, and the result nests as the business key
    /// of <see cref="ForDefinition"/> when definition and tenant qualification are both wanted.
    /// </summary>
    public static string ForTenant(string tenantKey, string businessKey)
    {
        var tenant = IdentifierSyntax.NormalizeTenantKey(tenantKey, nameof(tenantKey));
        var business = IdentifierSyntax.NormalizeKeyLookup(businessKey, nameof(businessKey));

        return IdentifierSyntax.NormalizeKey($"{tenant}:{business}", nameof(businessKey), MaxLength);
    }

    /// <summary>
    /// Compose a key that intentionally deduplicates across definitions in the same job namespace.
    /// </summary>
    /// <remarks>
    /// Time-bucket helpers are definition-qualified. A combined cross-definition time-bucket API is
    /// intentionally deferred; applications needing that scope should derive the bucketed business
    /// key explicitly and pass it here.
    /// </remarks>
    public static string AcrossDefinitions(string businessKey)
    {
        var business = IdentifierSyntax.NormalizeKeyLookup(businessKey, nameof(businessKey));
        return IdentifierSyntax.NormalizeKey(business, nameof(businessKey), MaxLength);
    }

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

        return IdentifierSyntax.NormalizeKey(
            $"{baseKey}:bucket:{size}:{ordinal.ToString(CultureInfo.InvariantCulture)}",
            nameof(businessKey),
            MaxLength
        );
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

        if (bucketSize.Ticks % TimeSpan.TicksPerMillisecond == 0)
        {
            return $"{(bucketSize.Ticks / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture)}ms";
        }

        return $"{bucketSize.Ticks.ToString(CultureInfo.InvariantCulture)}ticks";
    }
}

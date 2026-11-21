using System.Buffers.Binary;
using System.Globalization;

namespace Acta;

/// <summary>
/// Lookup key for the read surface of <see cref="IJobs"/>. Carries the public <see cref="JobRef"/>,
/// a <c>(JobNamespace, IdempotencyKey)</c> pair that resolves to the root <c>job</c> row (root only,
/// parent_job_id IS NULL), or the database-local numeric <see cref="JobId"/>. Construct via
/// <see cref="ByRef"/>, <see cref="ByIdempotencyKey"/>, or <see cref="ById"/>; implicit conversions
/// exist for <see cref="JobRef"/> and <see cref="EnqueueJobResponse"/>.
/// </summary>
public readonly record struct JobLookup
{
    private JobLookup(JobLookupKind kind, long jobId, string? jobNamespace, string? idempotencyKey, JobRef jobRef)
    {
        Kind = kind;
        JobId = jobId;
        JobNamespace = jobNamespace;
        IdempotencyKey = idempotencyKey;
        JobRef = jobRef;
    }

    /// <summary>Which lookup field is populated.</summary>
    public JobLookupKind Kind { get; }

    /// <summary>
    /// Public job ref; populated when <see cref="Kind"/> is <see cref="JobLookupKind.JobRef"/>, and beside the id on a
    /// lookup converted from an <see cref="EnqueueJobResponse"/>, which a read that finds no job then names.
    /// </summary>
    public JobRef JobRef { get; }

    /// <summary>Database-local numeric job identity; populated when <see cref="Kind"/> is <see cref="JobLookupKind.JobId"/>.</summary>
    public long JobId { get; }

    /// <summary>Lowercase kebab-case namespace; populated when <see cref="Kind"/> is <see cref="JobLookupKind.IdempotencyKey"/>.</summary>
    public string? JobNamespace { get; }

    /// <summary>Acta-normalized idempotency key; populated when <see cref="Kind"/> is <see cref="JobLookupKind.IdempotencyKey"/>.</summary>
    public string? IdempotencyKey { get; }

    /// <summary>
    /// Build a lookup by public <paramref name="jobRef"/>; the normal identity for dashboard and
    /// HTTP callers. The all-zero ref is a well-formed ref no job is minted with, so its lookup finds
    /// nothing, as an alert's or a worker's does.
    /// </summary>
    public static JobLookup ByRef(JobRef jobRef) => new(JobLookupKind.JobRef, 0, null, null, jobRef);

    /// <summary>
    /// Build a lookup by caller-supplied <paramref name="idempotencyKey"/> within
    /// <paramref name="jobNamespace"/>. Matches the root Job row only (parent_job_id IS NULL).
    /// </summary>
    public static JobLookup ByIdempotencyKey(string jobNamespace, string idempotencyKey)
    {
        jobNamespace = NameSyntax.Canonicalize(jobNamespace, nameof(jobNamespace));
        idempotencyKey = KeySyntax.Canonicalize(idempotencyKey, nameof(idempotencyKey));

        return new JobLookup(JobLookupKind.IdempotencyKey, 0, jobNamespace, idempotencyKey, default);
    }

    /// <summary>
    /// Build a lookup by database-local numeric <paramref name="jobId"/>; the advanced, test, and
    /// debug path. The id is namespace-agnostic.
    /// </summary>
    public static JobLookup ById(long jobId)
    {
        return jobId <= 0
            ? throw new ArgumentOutOfRangeException(nameof(jobId), jobId, "Job id must be positive.")
            : new JobLookup(JobLookupKind.JobId, jobId, null, null, default);
    }

    public static implicit operator JobLookup(JobRef jobRef) => ByRef(jobRef);

    public static implicit operator JobLookup(EnqueueJobResponse outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new JobLookup(JobLookupKind.JobId, ById(outcome.JobId).JobId, null, null, outcome.JobRef);
    }

    /// <summary>
    /// What may have happened to the job this lookup's ref named, for a read that found none, or null when the ref is
    /// not one Acta mints: a version 7 Guid, whose first 48 bits are its mint time. It names that time and the causes
    /// that fit, and is a hint, never proof: a ref can also come from a rolled-back transaction or another database.
    /// </summary>
    public string? NotFoundHint() =>
        MintedAtUtc(JobRef.Value) is { } minted
            ? $"The ref was minted at {minted.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}. If a job "
                + "held it, the job's retention or a purge may have removed it; a ref minted moments ago may belong to a "
                + "transaction that has not committed."
            : null;

    /// <summary>The message of a read that expects a job and finds none: what the lookup named, then the hint.</summary>
    internal string NoJobMessage()
    {
        var named =
            Kind == JobLookupKind.JobRef || JobRef != default ? JobRef.ToString()
            : Kind == JobLookupKind.JobId ? $"job id {JobId}"
            : Kind == JobLookupKind.IdempotencyKey ? $"idempotency key '{IdempotencyKey}' in namespace '{JobNamespace}'"
            : "the lookup";
        return NotFoundHint() is { } hint ? $"No job matches {named}. {hint}" : $"No job matches {named}.";
    }

    /// <summary>
    /// The mint time of a version 7 Guid, or null for any other Guid and for a time no ref Acta minted can have: before
    /// 2024, or more than a day of clock skew past now.
    /// </summary>
    private static DateTime? MintedAtUtc(Guid value)
    {
        const long floorUnixMs = 1_704_067_200_000;
        if (value.Version != 7 || (value.Variant & 0b1100) != 0b1000)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        var unixMs = (long)(BinaryPrimitives.ReadUInt64BigEndian(bytes) >> 16);
        var ceilingUnixMs = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        return unixMs >= floorUnixMs && unixMs <= ceilingUnixMs ? DateTime.UnixEpoch.AddMilliseconds(unixMs) : null;
    }
}

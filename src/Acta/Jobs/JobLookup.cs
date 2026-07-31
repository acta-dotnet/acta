namespace Acta;

/// <summary>
/// Lookup key for the read surface of <see cref="IJobs"/>. Carries the public <see cref="JobRef"/>,
/// a <c>(JobNamespace, DeduplicationKey)</c> pair that resolves to the root <c>job</c> row (root only,
/// parent_id IS NULL), or the database-local numeric <see cref="JobId"/>. Construct via
/// <see cref="ByRef"/>, <see cref="ByDeduplicationKey"/>, or <see cref="ById"/>; implicit conversions
/// exist for <see cref="JobRef"/> and <see cref="JobEnqueueOutcome"/>.
/// </summary>
public readonly record struct JobLookup
{
    private JobLookup(JobLookupKind kind, long jobId, string? jobNamespace, string? deduplicationKey, JobRef jobRef)
    {
        Kind = kind;
        JobId = jobId;
        JobNamespace = jobNamespace;
        DeduplicationKey = deduplicationKey;
        JobRef = jobRef;
    }

    /// <summary>Which lookup field is populated.</summary>
    public JobLookupKind Kind { get; }

    /// <summary>Public job ref; populated when <see cref="Kind"/> is <see cref="JobLookupKind.JobRef"/>.</summary>
    public JobRef JobRef { get; }

    /// <summary>Database-local numeric job identity; populated when <see cref="Kind"/> is <see cref="JobLookupKind.JobId"/>.</summary>
    public long JobId { get; }

    /// <summary>Lowercase kebab-case namespace; populated when <see cref="Kind"/> is <see cref="JobLookupKind.DeduplicationKey"/>.</summary>
    public string? JobNamespace { get; }

    /// <summary>Acta-normalized deduplication key; populated when <see cref="Kind"/> is <see cref="JobLookupKind.DeduplicationKey"/>.</summary>
    public string? DeduplicationKey { get; }

    /// <summary>
    /// Build a lookup by public <paramref name="jobRef"/>; the normal identity for dashboard and
    /// HTTP callers.
    /// </summary>
    public static JobLookup ByRef(JobRef jobRef)
    {
        return jobRef.Value == Guid.Empty
            ? throw new ArgumentException("Job ref is empty.", nameof(jobRef))
            : new JobLookup(JobLookupKind.JobRef, 0, null, null, jobRef);
    }

    /// <summary>
    /// Build a lookup by caller-supplied <paramref name="deduplicationKey"/> within
    /// <paramref name="jobNamespace"/>. Matches the root Job row only (parent_id IS NULL).
    /// </summary>
    public static JobLookup ByDeduplicationKey(string jobNamespace, string deduplicationKey)
    {
        jobNamespace = IdentifierSyntax.CanonicalizeKebab(jobNamespace, nameof(jobNamespace));
        deduplicationKey = IdentifierSyntax.NormalizeKeyLookup(deduplicationKey, nameof(deduplicationKey));

        return new JobLookup(JobLookupKind.DeduplicationKey, 0, jobNamespace, deduplicationKey, default);
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

    public static implicit operator JobLookup(JobEnqueueOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return ById(outcome.JobId);
    }
}

/// <summary>
/// Which field of a <see cref="JobLookup"/> identifies the target job. The <c>default</c>
/// <see cref="JobLookup"/> value carries <see cref="None"/> and is rejected by the read seam.
/// </summary>
public enum JobLookupKind : byte
{
    /// <summary>Uninitialized; produced only by <c>default(JobLookup)</c> and rejected by the read seam.</summary>
    None = 0,

    /// <summary>Lookup by public <c>JobRef</c>.</summary>
    JobRef = 1,

    /// <summary>Lookup by <c>(JobNamespace, DeduplicationKey)</c>; root job only.</summary>
    DeduplicationKey = 2,

    /// <summary>Lookup by database-local numeric <c>JobId</c>.</summary>
    JobId = 3,
}

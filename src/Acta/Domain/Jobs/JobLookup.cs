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

    /// <summary>Public job ref; populated when <see cref="Kind"/> is <see cref="JobLookupKind.JobRef"/>.</summary>
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
        return ById(outcome.JobId);
    }
}

namespace Acta;

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

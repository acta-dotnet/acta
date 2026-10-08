namespace Acta;

/// <summary>
/// Coarse outcome classifier for one enqueued row.
/// </summary>
public enum EnqueueJobAction : byte
{
    /// <summary>A fresh <c>job</c> row was inserted.</summary>
    Inserted = 1,

    /// <summary>An existing row matched the <see cref="EnqueueJobRequest.DeduplicationKey"/>; no new row was inserted.</summary>
    Deduplicated = 2,
}

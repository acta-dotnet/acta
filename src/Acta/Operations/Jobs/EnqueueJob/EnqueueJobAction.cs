namespace Acta;

/// <summary>What an enqueue did: inserted a new job, or deduplicated onto the job that already holds the deduplication key.</summary>
public enum EnqueueJobAction : byte
{
    /// <summary>A fresh <c>job</c> row was inserted.</summary>
    Inserted = 1,

    /// <summary>An existing row matched the <see cref="EnqueueJobRequest.DeduplicationKey"/>; no new row was inserted.</summary>
    Deduplicated = 2,
}

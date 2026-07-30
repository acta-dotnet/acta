namespace Acta;

/// <summary>
/// Thrown before a batch enqueue reaches SQL when two rows carry the same deduplication key inside
/// the same root or child uniqueness boundary.
/// </summary>
public sealed class DuplicateDeduplicationKeyInBatchException : Exception
{
    private DuplicateDeduplicationKeyInBatchException(
        string message,
        string deduplicationKey,
        string? rootJobNamespace,
        long? parentJobId,
        int firstOrdinal,
        int secondOrdinal
    )
        : base(message)
    {
        DeduplicationKey = deduplicationKey;
        RootJobNamespace = rootJobNamespace;
        ParentJobId = parentJobId;
        FirstOrdinal = firstOrdinal;
        SecondOrdinal = secondOrdinal;
    }

    /// <summary>Create a duplicate failure for root jobs, whose key is unique within a namespace.</summary>
    public static DuplicateDeduplicationKeyInBatchException ForRoot(
        string rootJobNamespace,
        string deduplicationKey,
        int firstOrdinal,
        int secondOrdinal
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootJobNamespace);
        ValidateCommon(deduplicationKey, firstOrdinal, secondOrdinal);

        return new DuplicateDeduplicationKeyInBatchException(
            $"Enqueue batch has duplicate DeduplicationKey '{deduplicationKey}' for root namespace "
                + $"'{rootJobNamespace}' at rows {firstOrdinal} and {secondOrdinal}.",
            deduplicationKey,
            rootJobNamespace,
            parentJobId: null,
            firstOrdinal,
            secondOrdinal
        );
    }

    /// <summary>Create a duplicate failure for child jobs, whose key is unique under a direct parent.</summary>
    public static DuplicateDeduplicationKeyInBatchException ForChild(
        long parentJobId,
        string deduplicationKey,
        int firstOrdinal,
        int secondOrdinal
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentJobId);
        ValidateCommon(deduplicationKey, firstOrdinal, secondOrdinal);

        return new DuplicateDeduplicationKeyInBatchException(
            $"Enqueue batch has duplicate DeduplicationKey '{deduplicationKey}' for child parent job "
                + $"{parentJobId} at rows {firstOrdinal} and {secondOrdinal}.",
            deduplicationKey,
            rootJobNamespace: null,
            parentJobId,
            firstOrdinal,
            secondOrdinal
        );
    }

    /// <summary>The duplicated canonical key.</summary>
    public string DeduplicationKey { get; }

    /// <summary>The root-job namespace boundary, or null for a child-job duplicate.</summary>
    public string? RootJobNamespace { get; }

    /// <summary>The direct parent boundary, or null for a root-job duplicate.</summary>
    public long? ParentJobId { get; }

    /// <summary>The zero-based ordinal of the first row carrying the key.</summary>
    public int FirstOrdinal { get; }

    /// <summary>The zero-based ordinal of the second duplicate row.</summary>
    public int SecondOrdinal { get; }

    private static void ValidateCommon(string deduplicationKey, int firstOrdinal, int secondOrdinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);
        ArgumentOutOfRangeException.ThrowIfNegative(firstOrdinal);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(secondOrdinal, firstOrdinal);
    }
}

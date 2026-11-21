namespace Acta;

/// <summary>
/// Thrown before a batch enqueue reaches SQL when two rows carry the same idempotency key inside
/// the same root or child uniqueness boundary.
/// </summary>
public sealed class DuplicateIdempotencyKeyInBatchException : ActaException
{
    private DuplicateIdempotencyKeyInBatchException(
        string message,
        string idempotencyKey,
        string? rootJobNamespace,
        long? parentJobId,
        int firstOrdinal,
        int secondOrdinal
    )
        : base(message)
    {
        IdempotencyKey = idempotencyKey;
        RootJobNamespace = rootJobNamespace;
        ParentJobId = parentJobId;
        FirstOrdinal = firstOrdinal;
        SecondOrdinal = secondOrdinal;
    }

    /// <summary>Create a duplicate failure for root jobs, whose key is unique within a namespace.</summary>
    public static DuplicateIdempotencyKeyInBatchException ForRoot(
        string rootJobNamespace,
        string idempotencyKey,
        int firstOrdinal,
        int secondOrdinal
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootJobNamespace);
        ValidateCommon(idempotencyKey, firstOrdinal, secondOrdinal);

        return new DuplicateIdempotencyKeyInBatchException(
            $"Enqueue batch has duplicate IdempotencyKey '{idempotencyKey}' for root namespace "
                + $"'{rootJobNamespace}' at rows {firstOrdinal} and {secondOrdinal}.",
            idempotencyKey,
            rootJobNamespace,
            parentJobId: null,
            firstOrdinal,
            secondOrdinal
        );
    }

    /// <summary>Create a duplicate failure for child jobs, whose key is unique under a direct parent.</summary>
    public static DuplicateIdempotencyKeyInBatchException ForChild(
        long parentJobId,
        string idempotencyKey,
        int firstOrdinal,
        int secondOrdinal
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentJobId);
        ValidateCommon(idempotencyKey, firstOrdinal, secondOrdinal);

        return new DuplicateIdempotencyKeyInBatchException(
            $"Enqueue batch has duplicate IdempotencyKey '{idempotencyKey}' for child parent job "
                + $"{parentJobId} at rows {firstOrdinal} and {secondOrdinal}.",
            idempotencyKey,
            rootJobNamespace: null,
            parentJobId,
            firstOrdinal,
            secondOrdinal
        );
    }

    /// <summary>The duplicated canonical key.</summary>
    public string IdempotencyKey { get; }

    /// <summary>The root-job namespace boundary, or null for a child-job duplicate.</summary>
    public string? RootJobNamespace { get; }

    /// <summary>The direct parent boundary, or null for a root-job duplicate.</summary>
    public long? ParentJobId { get; }

    /// <summary>The zero-based ordinal of the first row carrying the key.</summary>
    public int FirstOrdinal { get; }

    /// <summary>The zero-based ordinal of the second duplicate row.</summary>
    public int SecondOrdinal { get; }

    private static void ValidateCommon(string idempotencyKey, int firstOrdinal, int secondOrdinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentOutOfRangeException.ThrowIfNegative(firstOrdinal);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(secondOrdinal, firstOrdinal);
    }
}

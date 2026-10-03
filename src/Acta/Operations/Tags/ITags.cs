namespace Acta;

/// <summary>Reads and mutates exact searchable metadata attachments.</summary>
/// <remarks>
/// The mutation verbs accept <c>actorKey</c> / <c>reasonMessage</c> like every other operator
/// mutation on the surface. Tag writes are unevented (tags are searchable metadata, not ledger
/// state), so both values are accepted and unrecorded: the parameters exist because adding them
/// after the surface freezes is a binary-breaking change, while an audit sink that consumes them
/// is an additive one.
/// </remarks>
public interface ITags
{
    /// <summary>Returns null for a missing target, otherwise its normalized name-ordered tag set.</summary>
    ValueTask<TagSet?> GetAsync(TagTarget target, CancellationToken ct = default);

    /// <summary>Atomically replaces all tags on an existing target. An empty list clears the target.</summary>
    ValueTask<TagMutationResult> ReplaceAsync(
        TagTarget target,
        IReadOnlyList<TagInput> tags,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>Inserts or replaces one tag on an existing target; repeating the same write is harmless.</summary>
    ValueTask<TagMutationResult> UpsertAsync(
        TagTarget target,
        TagInput tag,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>Removes one normalized tag name from an existing target; a missing tag is harmless.</summary>
    ValueTask<TagMutationResult> RemoveAsync(
        TagTarget target,
        string name,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );
}

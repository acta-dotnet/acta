namespace Acta;

/// <summary>Reads and mutates exact searchable metadata attachments.</summary>
public interface ITags
{
    /// <summary>Returns null for a missing target, otherwise its normalized name-ordered tag set.</summary>
    ValueTask<TagSet?> GetAsync(TagTarget target, CancellationToken ct = default);

    /// <summary>Atomically replaces all tags on an existing target. An empty list clears the target.</summary>
    ValueTask<TagMutationResult> ReplaceAsync(TagTarget target, IReadOnlyList<TagInput> tags, CancellationToken ct = default);

    /// <summary>Inserts or replaces one tag on an existing target; repeating the same write is harmless.</summary>
    ValueTask<TagMutationResult> UpsertAsync(TagTarget target, TagInput tag, CancellationToken ct = default);

    /// <summary>Removes one normalized tag name from an existing target; a missing tag is harmless.</summary>
    ValueTask<TagMutationResult> RemoveAsync(TagTarget target, string name, CancellationToken ct = default);
}

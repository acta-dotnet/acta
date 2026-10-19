namespace Acta;

/// <summary>Reads and mutates exact searchable metadata attachments.</summary>
public interface ITags
{
    /// <summary>Returns null for a missing target, otherwise its normalized name-ordered tag set.</summary>
    ValueTask<GetTagsResponse?> GetAsync(TagTarget target, CancellationToken ct = default);

    /// <summary>Atomically replaces all tags on an existing target. An empty list clears the target.</summary>
    ValueTask<ReplaceTagsResponse> ReplaceAsync(TagTarget target, IReadOnlyList<TagInput> tags, CancellationToken ct = default);

    /// <summary>
    /// Inserts or replaces one tag on an existing target; repeating the same write is harmless. A new name past
    /// <see cref="TagLimits.MaxTagsPerTarget"/> throws <see cref="ArgumentException"/> and writes nothing.
    /// </summary>
    ValueTask<UpsertTagsResponse> UpsertAsync(TagTarget target, TagInput tag, CancellationToken ct = default);

    /// <summary>Removes one normalized tag name from an existing target; a missing tag is harmless.</summary>
    ValueTask<RemoveTagsResponse> RemoveAsync(TagTarget target, string name, CancellationToken ct = default);
}

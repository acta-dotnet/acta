namespace Acta;

/// <summary>Reads and mutates exact searchable metadata attachments.</summary>
public interface ITags
{
    /// <summary>
    /// One page of a target's normalized tags by name, in ordinal order. A target that does not exist answers an
    /// empty page, as one without tags does.
    /// </summary>
    ValueTask<ListTagsResponse> ListAsync(ListTagsQuery query, CancellationToken ct = default);

    /// <summary>Atomically replaces all tags on an existing target. An empty list clears the target.</summary>
    ValueTask<ReplaceTagsResponse> ReplaceAsync(TagTarget target, IReadOnlyList<TagInput> tags, CancellationToken ct = default);

    /// <summary>
    /// Inserts or replaces one tag on an existing target; repeating the same write is harmless. A new name past
    /// <see cref="TagLimits.MaxTagsPerTarget"/> throws <see cref="ArgumentException"/> and writes nothing.
    /// </summary>
    ValueTask<UpsertTagResponse> UpsertAsync(TagTarget target, TagInput tag, CancellationToken ct = default);

    /// <summary>Removes one normalized tag name from an existing target; a missing tag is harmless.</summary>
    ValueTask<RemoveTagResponse> RemoveAsync(TagTarget target, string name, CancellationToken ct = default);
}

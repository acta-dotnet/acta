using System.Collections;

namespace Acta;

/// <summary>An immutable, name-ordered snapshot of the tags attached to one existing target.</summary>
public sealed class TagSet : IReadOnlyList<TagItem>
{
    public TagSet(IReadOnlyList<TagItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Tags = items.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The tags, ordered by normalized name using ordinal comparison.</summary>
    public IReadOnlyList<TagItem> Items => Tags;

    /// <summary>Alias for <see cref="Items"/> for object-style serialization and binding.</summary>
    public IReadOnlyList<TagItem> Tags { get; }

    public int Count => Tags.Count;

    public TagItem this[int index] => Tags[index];

    public IEnumerator<TagItem> GetEnumerator() => Tags.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

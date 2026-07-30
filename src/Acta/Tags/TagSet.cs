using System.Collections;

namespace Acta;

/// <summary>An immutable, name-ordered snapshot of the tags attached to one existing target.</summary>
public sealed class TagSet : IReadOnlyList<TagItem>
{
    private readonly IReadOnlyList<TagItem> _items;

    public TagSet(IReadOnlyList<TagItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The tags, ordered by normalized name using ordinal comparison.</summary>
    public IReadOnlyList<TagItem> Items => _items;

    /// <summary>Alias for <see cref="Items"/> for object-style serialization and binding.</summary>
    public IReadOnlyList<TagItem> Tags => _items;

    public int Count => _items.Count;

    public TagItem this[int index] => _items[index];

    public IEnumerator<TagItem> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

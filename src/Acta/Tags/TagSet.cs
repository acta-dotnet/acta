using System.Collections;

namespace Acta;

/// <summary>An immutable, name-ordered snapshot of the tags attached to one existing target.</summary>
public sealed class TagSet : IReadOnlyList<TagItem>
{
    public TagSet(IReadOnlyList<TagItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The tags, ordered by normalized name using ordinal comparison.</summary>
    public IReadOnlyList<TagItem> Items { get; }

    public int Count => Items.Count;

    public TagItem this[int index] => Items[index];

    public IEnumerator<TagItem> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

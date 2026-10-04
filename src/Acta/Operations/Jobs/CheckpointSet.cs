using System.Collections;

namespace Acta;

/// <summary>An immutable snapshot of one existing job's checkpoint slots, ordered by kind, then by name using ordinal comparison.</summary>
public sealed class CheckpointSet : IReadOnlyList<CheckpointItem>
{
    public CheckpointSet(IReadOnlyList<CheckpointItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items.OrderBy(static item => item.Kind).ThenBy(static item => item.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The slots, ordered by kind, then by name.</summary>
    public IReadOnlyList<CheckpointItem> Items { get; }

    public int Count => Items.Count;

    public CheckpointItem this[int index] => Items[index];

    public IEnumerator<CheckpointItem> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

using System.Collections.Concurrent;

namespace Acta;

/// <summary>
/// Every claim this worker holds, from the claim to its completion write: the set the heartbeat renews, so a row no
/// entry holds lapses to recovery. Keyed by execution: only a claim moves a job's execution number, so a key names one
/// claim, and an older attempt still unwinding never shares an entry with the claim that replaced it.
/// </summary>
internal sealed class InFlightJobRegistry
{
    private readonly ConcurrentDictionary<(long JobId, int ExecutionNumber), InFlightJob> _entries = new();

    public bool TryAdd(InFlightJob entry) => _entries.TryAdd(entry.Key, entry);

    /// <summary>Removes <paramref name="entry"/> if it is the one registered under its key, and disposes it.</summary>
    public void Remove(InFlightJob entry)
    {
        _entries.TryRemove(new KeyValuePair<(long, int), InFlightJob>(entry.Key, entry));
        entry.Dispose();
    }

    public InFlightJob[] Snapshot() => [.. _entries.Values];

    public int Count() => _entries.Count;

    public int Count(InFlightJobState state) => _entries.Values.Count(entry => entry.State == state);
}

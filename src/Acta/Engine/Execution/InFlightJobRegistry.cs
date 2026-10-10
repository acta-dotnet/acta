using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Acta;

/// <summary>
/// Every claim this worker holds, from the claim to its completion write: the set the heartbeat renews, so a row no
/// entry holds lapses to recovery. Keyed by execution: only a claim moves a job's execution number, so a key names one
/// claim, and an older attempt still unwinding never shares an entry with the claim that replaced it.
/// </summary>
internal sealed class InFlightJobRegistry
{
    private readonly ConcurrentDictionary<(long JobId, int ExecutionNumber), InFlightJob> _entries = new();

    public InFlightJobRegistry(JobsOptions options)
    {
        var permits =
            options.ExecutionProfile == ExecutionProfile.Direct
                ? options.MaxConcurrentExecutors
                : options.MaxConcurrentExecutors + options.ClaimBatchSize;
        Permits = new SemaphoreSlim(permits, permits);
        Claimed = Channel.CreateBounded<InFlightJob>(permits);
    }

    /// <summary>
    /// One per entry from its claim until its executor lets it go, so claiming never outruns the executors: Direct
    /// claims only for a free executor, and Buffered holds at most one batch ahead of them.
    /// </summary>
    public SemaphoreSlim Permits { get; }

    /// <summary>The claimer's hand-off to the executors, as large as the permits so a write never blocks.</summary>
    public Channel<InFlightJob> Claimed { get; }

    public bool TryAdd(InFlightJob entry) => _entries.TryAdd(entry.Key, entry);

    /// <summary>
    /// Removes <paramref name="entry"/> if it is the one registered under its key, returns its permit if it holds one,
    /// and disposes it.
    /// </summary>
    public void Remove(InFlightJob entry)
    {
        _entries.TryRemove(new KeyValuePair<(long, int), InFlightJob>(entry.Key, entry));
        if (entry.ReleasePermit())
        {
            Permits.Release();
        }
        entry.Dispose();
    }

    public InFlightJob[] Snapshot() => [.. _entries.Values];

    public int Count() => _entries.Count;

    public int Count(InFlightJobState state) => _entries.Values.Count(entry => entry.State == state);
}

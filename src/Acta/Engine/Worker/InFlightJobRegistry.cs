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
        var permits = options.MaxConcurrentExecutors;
        Permits = new SemaphoreSlim(permits, permits);
        Claimed = Channel.CreateBounded<InFlightJob>(permits);
        // Room for a full batch and the executors behind it, so a batch can grow while the finalizers write.
        Completed = Channel.CreateBounded<InFlightJob>(WorkerFinalizer.MaxBatch + options.MaxConcurrentExecutors);
    }

    /// <summary>
    /// One per entry from its claim until its executor lets it go, so claiming never outruns the executors: a claim
    /// takes a row only for a free executor.
    /// </summary>
    public SemaphoreSlim Permits { get; }

    /// <summary>The claimer's hand-off to the executors, as large as the permits so a write never blocks.</summary>
    public Channel<InFlightJob> Claimed { get; }

    /// <summary>
    /// The executors' hand-off to the finalizer: when the writes fall behind it fills, the executors wait on it with
    /// their permits held, and the claimer stops.
    /// </summary>
    public Channel<InFlightJob> Completed { get; }

    public bool TryAdd(InFlightJob entry) => _entries.TryAdd(entry.Key, entry);

    /// <summary>
    /// Removes <paramref name="entry"/> if it is the one registered under its key, returns its permit if it holds one,
    /// and disposes it.
    /// </summary>
    public void Remove(InFlightJob entry)
    {
        _entries.TryRemove(new KeyValuePair<(long, int), InFlightJob>(entry.Key, entry));
        ReleasePermit(entry);
        entry.Dispose();
    }

    /// <summary>Returns <paramref name="entry"/>'s permit if it holds one, while the entry stays held.</summary>
    public void ReleasePermit(InFlightJob entry)
    {
        if (entry.ReleasePermit())
        {
            Permits.Release();
        }
    }

    public InFlightJob[] Snapshot() => [.. _entries.Values];

    public int Count() => _entries.Count;

    public int Count(InFlightJobState state) => _entries.Values.Count(entry => entry.State == state);
}

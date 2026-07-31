using Acta;

namespace Anvil.Bench;

/// <summary>
/// A set of in-process benchmark hosts sharing one schema and one <see cref="BenchSink"/>. The schema
/// is reset once (on host 0), then every host registers its own worker row and runs its own claim loop,
/// so the cluster measures multi-worker claim contention and worker-kill recovery without separate
/// processes. The shared sink makes the aggregate drain converge on a single completion count.
/// </summary>
public sealed class BenchCluster : IAsyncDisposable
{
    private readonly List<BenchHost> _hosts;
    private readonly HashSet<int> _killed = new();

    private BenchCluster(List<BenchHost> hosts, BenchSink sink, RecoveryCoordinator recovery)
    {
        _hosts = hosts;
        Sink = sink;
        Recovery = recovery;
    }

    /// <summary>The shared completion and latency collector all hosts record into.</summary>
    public BenchSink Sink { get; }

    /// <summary>The shared recovery coordinator the blocking probe uses.</summary>
    public RecoveryCoordinator Recovery { get; }

    /// <summary>The hosts, indexed by worker id.</summary>
    public IReadOnlyList<BenchHost> Hosts => _hosts;

    /// <summary>The enqueue surface of the first host that is still alive.</summary>
    public IJobs Jobs => Surviving().Jobs;

    /// <summary>The read surface of the first host that is still alive.</summary>
    public IActaOperations Queries => Surviving().Queries;

    /// <summary>
    /// Resets the schema once, then starts <paramref name="workers"/> hosts from the template, each with
    /// the shared sink and recovery coordinator and a distinct worker id.
    /// </summary>
    public static async Task<BenchCluster> StartAsync(BenchHostOptions template, int workers, CancellationToken ct)
    {
        var sink = template.Sink ?? new BenchSink();
        var recovery = template.Recovery ?? new RecoveryCoordinator();
        var hosts = new List<BenchHost>(workers);
        for (var i = 0; i < workers; i++)
        {
            var opt = template with { Sink = sink, Recovery = recovery, WorkerId = i, ResetSchema = i == 0 };
            hosts.Add(await BenchHost.StartAsync(opt, ct));
        }
        return new BenchCluster(hosts, sink, recovery);
    }

    /// <summary>Abruptly kills the host with the given worker id (no graceful stop).</summary>
    public void Kill(int workerId)
    {
        _hosts[workerId].Kill();
        _killed.Add(workerId);
    }

    private BenchHost Surviving()
    {
        for (var i = 0; i < _hosts.Count; i++)
        {
            if (!_killed.Contains(i))
            {
                return _hosts[i];
            }
        }
        throw new InvalidOperationException("All cluster hosts have been killed.");
    }

    public async ValueTask DisposeAsync()
    {
        for (var i = 0; i < _hosts.Count; i++)
        {
            if (_killed.Contains(i))
            {
                continue;
            }
            await _hosts[i].DisposeAsync();
        }
    }
}

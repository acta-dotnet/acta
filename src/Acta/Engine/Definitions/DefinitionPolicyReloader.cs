using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// The runtime-owned definition-policy reload loop. Every <see cref="JobsOptions.SafetyPollInterval"/> it re-reads the
/// namespace's effective policy (the DB-computed <c>*_effective</c> columns) and re-overlays it onto the live
/// <see cref="WorkerContext.DescriptorByDefinitionId"/> index, so an operator override edited in the dashboard reaches
/// a running worker's hot path (backoff, timeout, deadline, retention, max attempts read from the descriptor) within
/// one tick, no restart required. Started beside the claim loop and the heartbeat on its own
/// <see cref="PeriodicTimer"/>; a no-op in enqueue-only mode.
/// </summary>
internal sealed class DefinitionPolicyReloader(
    GetDefinitionContractsHandler getDefinitionContracts,
    IOptions<JobsOptions> options,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    ILogger log
)
{
    private readonly GetDefinitionContractsHandler _getDefinitionContracts = getDefinitionContracts;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly TimeSpan _interval = options.Value.SafetyPollInterval;
    private readonly ILogger _log = log;

    public async Task RunAsync(CancellationToken ct)
    {
        if (_workerRegistration is null)
        {
            return;
        }

        var ns = _workerRegistration.NamespaceName;
        _log.LogInformation(
            "WorkerRuntime: starting definition-policy reload loop (interval {DurationMs}ms).",
            (long)_interval.TotalMilliseconds
        );

        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await TickAsync(ns, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "WorkerRuntime: definition-policy reload tick failed; retrying next tick.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// One reload pass: re-overlay the effective policy of every definition in the namespace. The
    /// whole catalog is read each tick regardless, so tracking which rows changed buys nothing. It
    /// also costs correctness: a modified_at_utc watermark drops any row committed after the read
    /// but stamped at or before the newest value that read saw, leaving that override unapplied
    /// until the row changes again. Deterministic single-shot the loop drives per tick; tests drive
    /// it via WorkerRuntime.
    /// </summary>
    public async Task TickAsync(string ns, CancellationToken ct)
    {
        if (!_context.NamespaceIds.TryGetValue(ns, out var namespaceId))
        {
            return;
        }

        var catalog = (await _getDefinitionContracts.HandleAsync(new GetDefinitionContractsQuery(namespaceId), ct)).Contracts;
        foreach (var c in catalog)
        {
            if (_context.DescriptorByDefinitionId.TryGetValue(c.Id, out var descriptor))
            {
                _context.DescriptorByDefinitionId[c.Id] = EffectivePolicyOverlay.Apply(descriptor, c.Effective);
            }
        }
        MeterConsistencyCheck.Check(ns, catalog, _log);
    }
}

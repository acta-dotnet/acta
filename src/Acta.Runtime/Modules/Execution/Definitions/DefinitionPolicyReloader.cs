using Acta.Configuration;
using Acta.Modules.Execution.Definitions;
using Acta.Modules.Execution.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta.Modules.Execution.Definitions;

/// <summary>
/// The runtime-owned definition-policy reload loop. Every <see cref="JobsOptions.SafetyPollInterval"/> it
/// re-reads the namespace's effective policy (the DB-computed <c>*_effective</c> columns) and re-overlays
/// it onto the live <see cref="WorkerContext.DescriptorByDefinitionId"/> index, so an operator override
/// edited via the dashboard reaches a running worker's execution hot path (backoff / timeout / deadline /
/// retention / max-attempts read from the descriptor) within one tick, no restart required. Started
/// alongside the claim/dispatch loop and the heartbeat on its own <see cref="PeriodicTimer"/>; keyed on
/// <c>modified_at_utc</c> so only rows written since the last sweep are re-overlaid; self-gates to a no-op
/// in enqueue-only mode.
/// </summary>
internal sealed class DefinitionPolicyReloader
{
    private readonly IDefinitionStore _store;
    private readonly WorkerRegistration? _workerRegistration;
    private readonly WorkerContext _context;
    private readonly TimeSpan _interval;
    private readonly ILogger _log;
    private DateTime _watermarkUtc = DateTime.MinValue;

    public DefinitionPolicyReloader(
        IDefinitionStore store,
        IOptions<JobsOptions> options,
        WorkerRegistration? workerRegistration,
        WorkerContext context,
        ILogger log
    )
    {
        _store = store;
        _workerRegistration = workerRegistration;
        _context = context;
        _interval = options.Value.SafetyPollInterval;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_workerRegistration is null)
        {
            return;
        }

        var ns = _workerRegistration.NamespaceName;
        _log.LogInformation("WorkerRuntime: starting definition-policy reload loop (interval {Interval}).", _interval);

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

    // One reload pass: re-overlay the effective policy of every definition whose row was modified since
    // the last sweep. Uses DB-sourced modified_at_utc on both sides, so the watermark needs no host clock.
    // Deterministic single-shot the loop drives per tick; tests drive it via WorkerRuntime.
    public async Task TickAsync(string ns, CancellationToken ct)
    {
        if (!_context.NamespaceIds.TryGetValue(ns, out var namespaceId))
        {
            return;
        }

        var catalog = await _store.GetDefinitionContractsAsync(namespaceId, ct);
        var maxSeen = _watermarkUtc;
        foreach (var c in catalog)
        {
            if (c.ModifiedAtUtc > maxSeen)
            {
                maxSeen = c.ModifiedAtUtc;
            }
            if (c.ModifiedAtUtc <= _watermarkUtc)
            {
                continue; // unchanged since the last sweep
            }
            if (_context.DescriptorByDefinitionId.TryGetValue(c.Id, out var descriptor))
            {
                _context.DescriptorByDefinitionId[c.Id] = EffectivePolicyOverlay.Apply(descriptor, c.Effective);
            }
        }

        _watermarkUtc = maxSeen;
    }
}

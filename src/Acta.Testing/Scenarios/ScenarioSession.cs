using Acta.Relational.Entities;
using Acta.Testing.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Testing.Scenarios;

/// <summary>
/// Pinned Scenario Studio session for one enqueued job. All drive, signal, recovery, and diagnostic
/// helpers target this job id.
/// </summary>
public class ScenarioSession<TInput>
    where TInput : notnull
{
    private const int DefaultMaxTicks = 32;

    private readonly IDbSession _db;

    internal ScenarioSession(IActaTestHost host, JobEnqueueOutcome enqueueOutcome, string jobNamespace, string jobName)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(enqueueOutcome);
        Host = host;
        _db = host.Services.GetRequiredService<IDbSession>();
        EnqueueOutcome = enqueueOutcome;
        Namespace = jobNamespace;
        JobName = jobName;
    }

    public JobEnqueueOutcome EnqueueOutcome { get; }

    public long JobId => EnqueueOutcome.JobId;

    public JobRef JobRef => EnqueueOutcome.JobRef;

    public JobLookup Lookup => JobLookup.ById(JobId);

    public string Namespace { get; }

    public string JobName { get; }

    protected IActaTestHost Host { get; }

    public Task<ActaRunOutcome> RunOnceAsync(CancellationToken ct = default) => Host.RunOnceAsync(JobId, ct);

    public Task RunUntilDoneAsync(int maxTicks = DefaultMaxTicks, CancellationToken ct = default) =>
        RunUntilStatusAsync(JobStatusCode.Succeeded, "reach Succeeded", maxTicks, ct);

    public Task RunUntilFailedAsync(int maxTicks = DefaultMaxTicks, CancellationToken ct = default) =>
        RunUntilStatusAsync(JobStatusCode.Failed, "reach Failed", maxTicks, ct);

    public async Task RunUntilSignalAsync(string? name = null, int maxTicks = DefaultMaxTicks, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicks, 1);

        for (var tick = 0; tick <= maxTicks; tick++)
        {
            if (await IsWaitingForSignalAsync(name, ct))
            {
                return;
            }

            var status = await StatusAsync(ct);
            if (status.IsTerminal)
            {
                throw await AssertionFailureAsync(
                    $"Scenario job {JobId} reached terminal status {status} before waiting for signal {FormatName(name)}.",
                    ct
                );
            }

            if (tick == maxTicks)
            {
                break;
            }

            await RunOnceAsync(ct);
        }

        throw await AssertionFailureAsync($"Scenario job {JobId} did not wait for signal {FormatName(name)} within {maxTicks} ticks.", ct);
    }

    public ValueTask<JobControlResult> RaiseSignalAsync(string name, CancellationToken ct = default) =>
        Host.Jobs.RaiseSignalAsync(Lookup, name, ct: ct);

    public ValueTask<JobControlResult> RaiseSignalAsync<T>(string name, T value, CancellationToken ct = default) =>
        Host.Jobs.RaiseSignalAsync(Lookup, name, value, ct: ct);

    public ValueTask<JobControlResult> RaiseSignalAsync(string name, JobPayload value, CancellationToken ct = default) =>
        Host.Jobs.RaiseSignalAsync(Lookup, name, value, ct: ct);

    public Task FastForwardToNextTimerAsync(CancellationToken ct = default) => Host.ForceTimerDueAsync(JobId, ct: ct);

    public Task FastForwardToNextTimerAsync(string name, CancellationToken ct = default) => Host.ForceTimerDueAsync(JobId, name, ct);

    public Task FastForwardToStepRetryAsync(CancellationToken ct = default) => Host.ForceStepRetryDueAsync(JobId, ct: ct);

    public Task FastForwardToStepRetryAsync(string name, CancellationToken ct = default) => Host.ForceStepRetryDueAsync(JobId, name, ct);

    public Task ExpireLeaseAsync(CancellationToken ct = default) => Host.ExpireExecutionLeaseAsync(JobId, ct);

    public Task<ActaRecoveryOutcome> RecoverAsync(CancellationToken ct = default) => Host.RunRecoveryOnceAsync(Namespace, ct);

    public async Task<ScenarioJobSnapshot> JobAsync(CancellationToken ct = default)
    {
        var snapshot =
            await Host.Jobs.GetAsync(Lookup, ct) ?? throw await AssertionFailureAsync($"Scenario job {JobId} could not be read.", ct);
        return ScenarioDiagnostics.ToScenario(snapshot);
    }

    public async Task<JobStatusCode> StatusAsync(CancellationToken ct = default)
    {
        var status =
            await Host.Jobs.GetStatusAsync(Lookup, ct)
            ?? throw await AssertionFailureAsync($"Scenario job {JobId} status could not be read.", ct);
        return status;
    }

    public async Task<IReadOnlyList<ScenarioEventSnapshot>> EventsAsync(CancellationToken ct = default)
    {
        var page = await Host.Operations.Ledger.ListEventsAsync(new ListEventsQuery(JobId: JobId, PageSize: 100), ct);
        return page.Items.Reverse().Select(ScenarioDiagnostics.ToScenario).ToList();
    }

    public async Task<IReadOnlyList<ScenarioStepSnapshot>> StepsAsync(CancellationToken ct = default)
    {
        var steps = await _db.From<JobStep>().Where(s => s.JobId == JobId).ToListAsync(ct);
        return steps.OrderBy(s => s.Name, StringComparer.Ordinal).Select(ScenarioDiagnostics.ToScenario).ToList();
    }

    public async Task<ScenarioStepSnapshot?> StepAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var step = await _db.From<JobStep>().Where(s => s.JobId == JobId && s.Name == name).SingleOrDefaultAsync(ct);
        return step is null ? null : ScenarioDiagnostics.ToScenario(step);
    }

    public Task<IReadOnlyList<ScenarioCheckpointSnapshot>> SignalsAsync(CancellationToken ct = default) =>
        CheckpointsAsync(JobCheckpointKindCode.Signal, ct);

    public async Task<ScenarioCheckpointSnapshot?> SignalAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return await CheckpointAsync(JobCheckpointKindCode.Signal, name, ct);
    }

    public Task<IReadOnlyList<ScenarioCheckpointSnapshot>> TimersAsync(CancellationToken ct = default) =>
        CheckpointsAsync(JobCheckpointKindCode.Timer, ct);

    public async Task<ScenarioCheckpointSnapshot?> TimerAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return await CheckpointAsync(JobCheckpointKindCode.Timer, name, ct);
    }

    public async Task<IReadOnlyList<ScenarioCheckpointSnapshot>> CheckpointsAsync(
        JobCheckpointKindCode kind,
        CancellationToken ct = default
    )
    {
        var checkpoints = await _db.From<JobCheckpoint>().Where(c => c.JobId == JobId && c.Kind == kind).ToListAsync(ct);
        return checkpoints.OrderBy(c => c.Name, StringComparer.Ordinal).Select(ScenarioDiagnostics.ToScenario).ToList();
    }

    protected Task<ScenarioAssertionException> AssertionFailureAsync(string summary, CancellationToken ct) =>
        ScenarioDiagnostics.FailureAsync(Host, JobId, summary, ct);

    private async Task RunUntilStatusAsync(JobStatusCode expected, string description, int maxTicks, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicks, 1);

        for (var tick = 0; tick <= maxTicks; tick++)
        {
            var status = await StatusAsync(ct);
            if (status == expected)
            {
                return;
            }

            if (status.IsTerminal)
            {
                throw await AssertionFailureAsync(
                    $"Scenario job {JobId} reached terminal status {status} before it could {description}.",
                    ct
                );
            }

            if (tick == maxTicks)
            {
                break;
            }

            await RunOnceAsync(ct);
        }

        throw await AssertionFailureAsync($"Scenario job {JobId} did not {description} within {maxTicks} ticks.", ct);
    }

    private async Task<bool> IsWaitingForSignalAsync(string? name, CancellationToken ct)
    {
        if (await StatusAsync(ct) != JobStatusCode.Suspended)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var signal = await SignalAsync(name, ct);
        return signal?.Status == JobCheckpointStatusCode.Pending;
    }

    private async Task<ScenarioCheckpointSnapshot?> CheckpointAsync(JobCheckpointKindCode kind, string name, CancellationToken ct)
    {
        var checkpoint = await _db.From<JobCheckpoint>()
            .Where(c => c.JobId == JobId && c.Kind == kind && c.Name == name)
            .SingleOrDefaultAsync(ct);
        return checkpoint is null ? null : ScenarioDiagnostics.ToScenario(checkpoint);
    }

    private static string FormatName(string? name) => string.IsNullOrWhiteSpace(name) ? "<any>" : $"'{name}'";
}

/// <summary>Scenario session with a typed result helper.</summary>
public sealed class ScenarioSession<TInput, TResult> : ScenarioSession<TInput>
    where TInput : notnull
    where TResult : notnull
{
    internal ScenarioSession(IActaTestHost host, JobEnqueueOutcome enqueueOutcome, string jobNamespace, string jobName)
        : base(host, enqueueOutcome, jobNamespace, jobName) { }

    public async Task<TResult> ResultAsync(CancellationToken ct = default)
    {
        var status = await StatusAsync(ct);
        if (status != JobStatusCode.Succeeded)
        {
            throw await AssertionFailureAsync($"Scenario job {JobId} result was requested while status is {status}, not Succeeded.", ct);
        }

        var payload =
            await Host.Jobs.GetResultAsync(Lookup, ct)
            ?? throw await AssertionFailureAsync($"Scenario job {JobId} completed without a result payload.", ct);
        var serializers = Host.Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        return serializers.Resolve(payload.Format.Id).Deserialize<TResult>(payload)!;
    }

    public async Task AssertResultAsync(TResult expected, IEqualityComparer<TResult>? comparer = null, CancellationToken ct = default)
    {
        comparer ??= EqualityComparer<TResult>.Default;
        var actual = await ResultAsync(ct);
        if (!comparer.Equals(expected, actual))
        {
            throw await AssertionFailureAsync($"Scenario job {JobId} result did not match. Expected '{expected}', actual '{actual}'.", ct);
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test support for the durable alert capture specs: a terminal failure to project, the aging that puts it past every
/// retention window, the sweep that would delete it, and the definition edit that makes its projection poison.
/// </summary>
internal static class AlertCaptureOps
{
    /// <summary>The window the sweeps run with: an aged event sits far outside it, a fresh one inside it.</summary>
    public const int EventsRetentionDays = 30;

    /// <summary>A job that fails on its only attempt: NotImplementedException is not retryable, so it lands JobFailed.</summary>
    public static async Task<EnqueueJobResponse> FailTerminallyAsync(
        WorkerRuntime runtime,
        IJobs jobs,
        string jobNamespace,
        CancellationToken ct
    )
    {
        var job = await jobs.EnqueueAsync(new EnqueueJobRequest(jobNamespace, "job-throw-not-implemented", JobPayload.None), ct);
        await runtime.RunOnceAsync(job, ct);
        Assert.Equal(JobStatusCode.Failed, (await jobs.GetStatusAsync(job, ct))?.Status);
        return job;
    }

    /// <summary>Puts the job's events far outside the events window and its own retention in the past.</summary>
    public static async Task AgeAsync(IDbSession db, long jobId, CancellationToken ct)
    {
        var aged = DateTime.UtcNow.AddDays(-10 * EventsRetentionDays);
        await db.From<EventEntity>()
            .Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId)
            .UpdateOnlyAsync(() => new EventEntity { CreatedAtUtc = aged }, ct);
        await db.From<JobStateEntity>()
            .Where(r => r.Id == jobId)
            .UpdateOnlyAsync(() => new JobStateEntity { RetentionUntilUtc = aged }, ct);
    }

    /// <summary>One bounded retention sweep with the events window above and the other windows out of the way.</summary>
    public static Task SweepAsync(IServiceProvider services, int namespaceId, CancellationToken ct) =>
        RetentionTestOps.PurgeAsync(services, namespaceId, EventsRetentionDays, 100_000, 100_000_000, 1000, 50, ct);

    /// <summary>Sweeps until the job and every event of it are gone, the way the hourly sweep settles.</summary>
    public static Task SweepUntilGoneAsync(IServiceProvider services, IDbSession db, int namespaceId, long jobId, CancellationToken ct) =>
        RetentionTestOps.PurgeUntilAsync(
            services,
            namespaceId,
            EventsRetentionDays,
            100_000,
            100_000_000,
            1000,
            50,
            async () =>
                await db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct) is null
                && (await JobEventsAsync(db, jobId, ct)).Count == 0,
            ct
        );

    public static Task<IReadOnlyList<EventEntity>> JobEventsAsync(IDbSession db, long jobId, CancellationToken ct) =>
        db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId).ToListAsync(ct);

    /// <summary>
    /// Points the job's definition at an alert channel name that fails canonicalization, which makes every raise for it
    /// poison, or back to inheriting its declared channel when <paramref name="channel"/> is null.
    /// </summary>
    public static async Task OverrideChannelAsync(IDbSession db, long jobId, string? channel, CancellationToken ct)
    {
        var definitionId = Assert.Single(await db.From<JobEntity>().Where(j => j.Id == jobId).ToListAsync(ct)).DefinitionId;
        Assert.Equal(
            1,
            await db.From<DefinitionEntity>()
                .Where(d => d.Id == definitionId)
                .UpdateOnlyAsync(() => new DefinitionEntity { AlertChannelNameOverride = channel }, ct)
        );
    }

    /// <summary>One projector pass against the namespace's real <c>sys.alerts</c> slot, whose cursor the floor reads.</summary>
    public static async Task ProjectAsync(IServiceProvider services, string jobNamespace, int namespaceId, CancellationToken ct) =>
        await AlertTestOps.RunAlertsJobAsync(
            services,
            jobNamespace,
            namespaceId,
            await ProjectorAsync(services, jobNamespace, ct),
            options: null,
            drain: null,
            ct
        );

    public static Task<long> ProjectorAsync(IServiceProvider services, string jobNamespace, CancellationToken ct) =>
        AlertTestOps.RecurringSlotIdAsync(services, jobNamespace, "sys.alerts", ct);

    public static Task<IReadOnlyList<AlertEntity>> AlertsAsync(IServiceProvider services, long jobId, CancellationToken ct) =>
        services.GetRequiredService<IDbSession>().From<AlertEntity>().Where(a => a.JobId == jobId).ToListAsync(ct);
}

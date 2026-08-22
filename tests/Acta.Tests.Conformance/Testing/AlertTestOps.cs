using Acta.Relational.Entities;
using Acta.Relational.Stores;
using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Alerting.Api;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Runtime.Services.Locks;
using Acta.Runtime.Services.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support entry point into the Alerts feature: the raise upsert through the store port with
/// production channel canonicalization, deduplication-key normalization, and prose truncation, plus
/// the shared driver that runs a real <see cref="AlertsJob"/> pass on a spec's own container.
/// </summary>
internal static class AlertTestOps
{
    /// <summary>
    /// Raises one alert with no projected event behind it, the shape a manual
    /// <c>ctx.AlertAsync</c> takes: the raise always applies and never moves the row's projection
    /// high-water mark. Specs that need the projector's event-scoped behavior drive
    /// <c>AlertsJob</c> itself.
    /// </summary>
    public static Task<AlertRaiseOutcome> RaiseAsync(
        IServiceProvider services,
        string jobNamespace,
        long? jobId,
        AlertOriginCode origin,
        AlertSeverityCode severity,
        AlertKindCode kind,
        string title,
        string message,
        string channelName,
        AlertDeliveryStatusCode deliveryStatus,
        string? deduplicationKey,
        CancellationToken ct
    ) =>
        services
            .GetRequiredService<IAlertStore>()
            .RaiseJobAlertAsync(
                RaiseJobAlertCommand.Create(
                    jobNamespace,
                    jobId,
                    origin,
                    severity,
                    kind,
                    title,
                    message,
                    channelName,
                    deliveryStatus,
                    deduplicationKey,
                    sourceEventId: null
                ),
                ct
            );

    /// <summary>
    /// Runs one full <see cref="AlertsJob"/> pass (generate + deliver) against the spec's namespace,
    /// constructing the job from the spec's own service provider. Shared by every alert spec so a
    /// change to the projector's constructor is one edit here instead of a synchronized edit in each
    /// spec's private copy. <paramref name="options"/> replaces the container's
    /// <see cref="JobsOptions"/> when a fact needs its own thresholds; <c>null</c> runs with whatever
    /// the spec's container configured. <paramref name="drain"/> replaces the generate drain's shipped
    /// bounds; <c>null</c> runs the production 256 / 40 / 30s budget.
    ///
    /// <para><paramref name="ageEventsPastHorizon"/> ages the namespace's events back behind the
    /// projection read's safe horizon first, which every spec but the horizon's own wants: the horizon is
    /// two production command timeouts wide, so a spec that drove a job seconds ago would otherwise
    /// project nothing at all. Aging the stamps is the same input the projector sees a minute later in
    /// production, without the minute.</para>
    /// </summary>
    public static async Task RunAlertsJobAsync(
        IServiceProvider services,
        string jobNamespace,
        int namespaceId,
        long cursorOwnerJobId,
        JobsOptions? options,
        AlertDrainBudget? drain,
        CancellationToken ct,
        bool ageEventsPastHorizon = true
    )
    {
        if (ageEventsPastHorizon)
        {
            await AgeEventsPastHorizonAsync(services, namespaceId, ct);
        }

        var alertsJob = new AlertsJob(
            services.GetRequiredService<IAlertStore>(),
            services.GetRequiredService<IActaClock>(),
            services.GetRequiredService<IAlertChannelRegistry>(),
            services.GetRequiredService<IAlertTransportRegistry>(),
            options is null ? services.GetRequiredService<IOptions<JobsOptions>>() : Options.Create(options)
        )
        {
            Drain = drain ?? AlertsJob.DefaultDrain,
        };

        await alertsJob.Handle(BuildAlertsContext(services, jobNamespace, namespaceId, cursorOwnerJobId), ct);
    }

    /// <summary>
    /// Backdates every event in <paramref name="namespaceId"/> to behind the projection read's safe
    /// horizon, so the next pass sees them all. Every alert spec's namespace is its own, so nothing
    /// outside the calling fact is touched.
    /// </summary>
    /// <remarks>
    /// The instant comes from <see cref="IServerClock"/> - the database's own clock, which is what the
    /// horizon predicate compares against - so the whole alert family rides on no assumption about this
    /// host's clock agreeing with the database's. One second of slack puts the stamp strictly inside the
    /// horizon rather than exactly on its boundary; nothing larger is needed, because the read that
    /// follows reads its own <c>now()</c>, which cannot precede the one taken here.
    /// </remarks>
    public static async Task AgeEventsPastHorizonAsync(IServiceProvider services, int namespaceId, CancellationToken ct)
    {
        var lag = TimeSpan.FromSeconds(
            RelationalAlertStore.SafeHorizonLagSeconds(services.GetRequiredService<SqlProviderOptions>().CommandTimeout)
        );
        var serverNowUtc = await services.GetRequiredService<IServerClock>().GetUtcNowAsync(ct);
        var aged = serverNowUtc - lag - TimeSpan.FromSeconds(1);
        await services
            .GetRequiredService<IDbSession>()
            .From<JobEvent>()
            .Where(e => e.NamespaceId == namespaceId)
            .UpdateOnlyAsync(() => new JobEvent { CreatedAtUtc = aged }, ct);
    }

    /// <summary>
    /// The projector's cursor as the projector itself reads it, through a stand-in context on the
    /// same slot: a spec asserting how far a pass drained compares against this rather than decoding
    /// the checkpoint row's payload by hand. Zero when no pass has checkpointed yet.
    /// </summary>
    public static Task<long> ReadAlertsCursorAsync(
        IServiceProvider services,
        string jobNamespace,
        int namespaceId,
        long cursorOwnerJobId,
        CancellationToken ct
    ) =>
        BuildAlertsContext(services, jobNamespace, namespaceId, cursorOwnerJobId)
            .GetVariableOrDefaultAsync(AlertsJob.CursorVariableName, 0L, ct);

    /// <summary>
    /// Rewinds the projector's cursor to <paramref name="cursorEventId"/> through the same variable
    /// write the projector uses, which is how a spec stages a crash that lost one batch's checkpoint
    /// while every alert write that batch made stands. Deleting the row (the whole-pass crash) is the
    /// coarser sibling of this and lives in the specs that need it.
    /// </summary>
    public static Task RewindAlertsCursorAsync(
        IServiceProvider services,
        string jobNamespace,
        int namespaceId,
        long cursorOwnerJobId,
        long cursorEventId,
        CancellationToken ct
    ) =>
        BuildAlertsContext(services, jobNamespace, namespaceId, cursorOwnerJobId)
            .SetVariableAsync(AlertsJob.CursorVariableName, cursorEventId, ct);

    /// <summary>
    /// Records one poison-skip variable for <paramref name="eventId"/> through the same checkpoint
    /// write the projector uses, so a spec can stage the rows retention prunes without having to
    /// manufacture an event that is genuinely unprojectable.
    /// </summary>
    public static Task RecordProjectionSkipAsync(
        IServiceProvider services,
        string jobNamespace,
        int namespaceId,
        long slotJobId,
        long eventId,
        CancellationToken ct
    ) =>
        BuildAlertsContext(services, jobNamespace, namespaceId, slotJobId)
            .SetVariableAsync(SkipVariableName(eventId), $"namespace={jobNamespace};eventId={eventId};reason=unknown-job", ct);

    /// <summary>The projector's variable name for one skipped event, from the projector's own prefix.</summary>
    public static string SkipVariableName(long eventId) =>
        AlertsJob.SkipVariablePrefix + eventId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The internal job id of a seeded recurring slot, looked up by its deduplication key, which for
    /// a recurring slot is the definition's job name.
    /// </summary>
    public static async Task<long> RecurringSlotIdAsync(
        IServiceProvider services,
        string jobNamespace,
        string jobName,
        CancellationToken ct
    )
    {
        var id = await services.GetRequiredService<IJobs>().GetJobIdAsync(JobLookup.ByDeduplicationKey(jobNamespace, jobName), ct);
        Assert.NotNull(id);
        return id!.Value;
    }

    // Claim with the lease already lapsed so the sys.recovery sweep reclaims the attempt with no
    // real-time wait, exactly as ReclaimStuckJobsSpec does. Never reaches JobsOptions, which rejects a
    // non-positive lease.
    private const int ExpiredLeaseTtlSeconds = -5;

    /// <summary>
    /// One orphaned attempt on a recurring slot: claim it with a lease that is already in the past, then
    /// let the recovery sweep reclaim it. That writes the slot's alertable failure event (Orphaned,
    /// <c>JobLeaseExpired</c>, back to Ready) and leaves the slot alive for the next fire - the only
    /// shape that produces an alertable failure with no real-time wait and without the handler running.
    /// Shared, because more than one spec needs a failure it can put behind a success.
    /// </summary>
    public static async Task OrphanOneAttemptAsync(IServiceProvider services, int namespaceId, long slotId, CancellationToken ct)
    {
        await MakeSlotClaimableAsync(services, slotId, ct);

        var db = services.GetRequiredService<IDbSession>();
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(db, namespaceId, ct);
        var claim = await services
            .GetRequiredService<IExecutionStore>()
            .ClaimOneAsync(namespaceId, workerId, ExpiredLeaseTtlSeconds, slotId, ct);
        Assert.Equal(slotId, Assert.Single(claim).JobId);

        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(services, namespaceId, ct)).Reclaimed);
    }

    /// <summary>
    /// The harness parks seeded slots a day out; this pulls one back so a by-id claim, which filters
    /// on <c>next_run_at_utc</c> like every other claim, can take it.
    /// </summary>
    public static Task MakeSlotClaimableAsync(IServiceProvider services, long slotId, CancellationToken ct)
    {
        var due = DateTime.UtcNow.AddMinutes(-5);
        return services
            .GetRequiredService<IDbSession>()
            .From<JobRuntime>()
            .Where(r => r.Id == slotId)
            .UpdateOnlyAsync(() => new JobRuntime { NextRunAtUtc = due }, ct);
    }

    // A JobContext standing in for the sys.alerts slot: the projector reads ctx.NamespaceId / JobNamespace
    // and stores the cursor variable as a checkpoints row keyed by the supplied (real) job's id.
    private static RuntimeJobContext BuildAlertsContext(
        IServiceProvider services,
        string jobNamespace,
        int namespaceId,
        long cursorOwnerJobId
    )
    {
        var slot = new ClaimedJob(
            JobId: cursorOwnerJobId,
            JobRef: Guid.Empty,
            NamespaceId: namespaceId,
            DefinitionId: 1,
            TenantId: null,
            ExecutionNumber: 1,
            DeduplicationKey: null,
            CorrelationKey: null,
            ExclusiveKey: null,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            NextRunAtUtc: null,
            LeaseExpiresAtUtc: default,
            CreatedAtUtc: default,
            FailureCount: 0,
            Version: 0
        );

        return new RuntimeJobContext(
            slot,
            jobName: "sys.alerts",
            namespaceName: jobNamespace,
            namespaceId: namespaceId,
            leaseTtlSeconds: 180,
            jobStore: services.GetRequiredService<IJobStore>(),
            signalStore: services.GetRequiredService<ISignalStore>(),
            alerts: services.GetRequiredService<IAlertSink>(),
            executionStore: services.GetRequiredService<IExecutionStore>(),
            serializers: services.GetRequiredService<IJobPayloadSerializerRegistry>(),
            lockStore: services.GetRequiredService<ILockStore>(),
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null
        );
    }
}

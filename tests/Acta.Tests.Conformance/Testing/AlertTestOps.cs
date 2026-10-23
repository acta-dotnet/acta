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
    public static Task<RaiseAlertResponse> RaiseAsync(
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
            .GetRequiredService<AlertsOperations>()
            .RaiseAlertAsync(
                RaiseAlertCommand.Create(
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
            services.GetRequiredService<AlertsOperations>(),
            services.GetRequiredService<IActaClock>(),
            services.GetRequiredService<AlertChannelRegistry>(),
            services.GetRequiredService<IAlertTransportRegistry>(),
            options is null ? services.GetRequiredService<IOptions<JobsOptions>>() : Options.Create(options)
        )
        {
            Drain = drain ?? AlertsJob.DefaultDrain,
        };

        await AsExecutionAsync(
            services,
            cursorOwnerJobId,
            fence => alertsJob.Handle(BuildAlertsContext(services, jobNamespace, namespaceId, fence), ct),
            ct
        );
    }

    /// <summary>
    /// Backdates every event in <paramref name="namespaceId"/> to behind the projection read's safe
    /// horizon, so the next pass sees them all. Every alert spec's namespace is its own, so nothing
    /// outside the calling fact is touched.
    /// </summary>
    /// <remarks>
    /// The instant comes from <see cref="IClockOperations"/> - the database's own clock, which is what the
    /// horizon predicate compares against - so the whole alert family rides on no assumption about this
    /// host's clock agreeing with the database's. Only rows still inside the horizon move, and they land
    /// exactly on it: the projector's cursor instant sits at the horizon of the last pass, so a stamp any
    /// earlier could fall behind the cursor, and a stamp any later stays inside the horizon the next pass
    /// computes. Rows already behind it keep their stamps, so a cursor that passed them is not rewound.
    /// </remarks>
    public static async Task AgeEventsPastHorizonAsync(IServiceProvider services, int namespaceId, CancellationToken ct)
    {
        var aged = await HorizonOfThisMomentAsync(services, ct);
        await services
            .GetRequiredService<IDbSession>()
            .From<EventEntity>()
            .Where(e => e.NamespaceId == namespaceId && e.CreatedAtUtc > aged)
            .UpdateOnlyAsync(() => new EventEntity { CreatedAtUtc = aged }, ct);
    }

    /// <summary>
    /// The safe horizon a pass starting now would compute, from the database's own clock and the
    /// container's command timeout through the projector's own arithmetic: the newest stamp such a
    /// pass would still read.
    /// </summary>
    public static async Task<DateTime> HorizonOfThisMomentAsync(IServiceProvider services, CancellationToken ct) =>
        AlertsJob.SafeHorizon(
            await services.GetRequiredService<IClockOperations>().GetUtcNowAsync(ct),
            services.GetRequiredService<AlertsOperations>().SafeHorizonLag
        );

    /// <summary>
    /// The projector's cursor as the projector itself reads it, through a stand-in context on the
    /// same slot: a spec asserting how far a pass drained compares against this rather than decoding
    /// the checkpoint row's payload by hand. Zero when no pass has checkpointed yet.
    /// </summary>
    public static async Task<long> ReadAlertsCursorAsync(
        IServiceProvider services,
        string jobNamespace,
        int namespaceId,
        long cursorOwnerJobId,
        CancellationToken ct
    ) => (await ReadCursorAsync(services, jobNamespace, namespaceId, cursorOwnerJobId, ct)).EventId;

    /// <summary>
    /// The instant half of the projector's cursor, read the same way: the stamp the last pass checkpointed
    /// to, or a short batch's horizon. <see cref="DateTime.MinValue"/> when no pass has checkpointed yet.
    /// </summary>
    public static async Task<DateTime> ReadAlertsCursorInstantAsync(
        IServiceProvider services,
        string jobNamespace,
        int namespaceId,
        long cursorOwnerJobId,
        CancellationToken ct
    ) => (await ReadCursorAsync(services, jobNamespace, namespaceId, cursorOwnerJobId, ct)).Utc;

    private static Task<AlertsCursor> ReadCursorAsync(
        IServiceProvider services,
        string jobNamespace,
        int namespaceId,
        long cursorOwnerJobId,
        CancellationToken ct
    ) =>
        // A read checks no fence, so the stand-in reads the slot without standing executing.
        BuildAlertsContext(services, jobNamespace, namespaceId, new ExecutionFence(cursorOwnerJobId, 0, 0))
            .GetVariableOrDefaultAsync(AlertsJob.CursorVariableName, new AlertsCursor(0L, 0L), ct);

    /// <summary>
    /// Rewinds the projector's cursor to <paramref name="cursorEventId"/> through the same variable
    /// write the projector uses, which is how a spec stages a crash that lost one batch's checkpoint
    /// while every alert write that batch made stands. The instant half is that event's own stamp when
    /// the row exists, else zero. Deleting the row (the whole-pass crash) is the coarser sibling of this
    /// and lives in the specs that need it.
    /// </summary>
    public static async Task RewindAlertsCursorAsync(
        IServiceProvider services,
        string jobNamespace,
        int namespaceId,
        long cursorOwnerJobId,
        long cursorEventId,
        CancellationToken ct
    )
    {
        var row = await services.GetRequiredService<IDbSession>().From<EventEntity>().Where(e => e.Id == cursorEventId).ToListAsync(ct);
        var ticks = row.Count == 1 ? DateTime.SpecifyKind(row[0].CreatedAtUtc, DateTimeKind.Utc).Ticks : 0L;
        await AsExecutionAsync(
            services,
            cursorOwnerJobId,
            fence =>
                BuildAlertsContext(services, jobNamespace, namespaceId, fence)
                    .SetVariableAsync(AlertsJob.CursorVariableName, new AlertsCursor(ticks, cursorEventId), ct),
            ct
        );
    }

    /// <summary>
    /// Records one poison-skip variable for <paramref name="eventId"/> of <paramref name="jobId"/> through the same
    /// checkpoint write and record format the projector uses, so a spec can stage the rows retention keeps
    /// without having to manufacture an event that is genuinely unprojectable.
    /// </summary>
    public static Task RecordProjectionSkipAsync(
        IServiceProvider services,
        string jobNamespace,
        int namespaceId,
        long slotJobId,
        long eventId,
        CancellationToken ct,
        long jobId = 0,
        long ticks = 0
    ) =>
        AsExecutionAsync(
            services,
            slotJobId,
            fence =>
                BuildAlertsContext(services, jobNamespace, namespaceId, fence)
                    .SetVariableAsync(
                        SkipVariableName(eventId),
                        new PoisonAlertEvent(eventId, jobId, ticks).Record(
                            jobNamespace,
                            "unknown-job",
                            nameof(ArgumentException),
                            "staged"
                        ),
                        ct
                    ),
            ct
        );

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
        var id = (await services.GetRequiredService<IJobs>().GetJobIdAsync(JobLookup.ByDeduplicationKey(jobNamespace, jobName), ct))?.JobId;
        Assert.NotNull(id);
        return id!.Value;
    }

    // Claim with the lease already lapsed so the recovery sweep reclaims the attempt with no
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
            .GetRequiredService<ExecutionOperations>()
            .ClaimJobAsync(namespaceId, workerId, ExpiredLeaseTtlSeconds, slotId, ct);
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
            .From<JobStateEntity>()
            .Where(r => r.Id == slotId)
            .UpdateOnlyAsync(() => new JobStateEntity { NextRunAtUtc = due }, ct);
    }

    // A JobContext standing in for the sys.alerts slot: the projector reads ctx.NamespaceId / JobNamespace
    // and stores the cursor variable as a checkpoints row keyed by the supplied (real) job's id, under its fence.
    private static RuntimeJobContext BuildAlertsContext(
        IServiceProvider services,
        string jobNamespace,
        int namespaceId,
        ExecutionFence owner
    )
    {
        var slot = new ClaimedJob(
            JobId: owner.JobId,
            JobRef: Guid.Empty,
            DefinitionId: 1,
            TenantId: null,
            ExecutionNumber: owner.ExecutionNumber,
            DeduplicationKey: null,
            CorrelationKey: null,
            ConcurrencyKey: null,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            NextRunAtUtc: null,
            CreatedAtUtc: default,
            FailureCount: 0
        );

        return new RuntimeJobContext(
            slot,
            jobName: "sys.alerts",
            namespaceName: jobNamespace,
            namespaceId: namespaceId,
            leaseTtlSeconds: 180,
            jobOperations: services.GetRequiredService<JobsOperations>(),
            signals: services.GetRequiredService<SignalsOperations>(),
            alerts: services.GetRequiredService<AlertsOperations>(),
            executionStore: services.GetRequiredService<ExecutionOperations>(),
            serializers: services.GetRequiredService<IJobPayloadSerializerRegistry>(),
            locks: services.GetRequiredService<LocksOperations>(),
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null,
            workerId: owner.WorkerId
        );
    }

    // The projector writes its checkpoints as the sys.alerts slot's own attempt does, under the execution fence, so a
    // stand-in pass runs with the owner job standing executing a stand-in attempt, and its row goes back as it was
    // found: restored, or removed when a seeded owner had none. No alert operation reads jobs_state, so the pass
    // projects what it would have.
    private static async Task AsExecutionAsync(
        IServiceProvider services,
        long ownerJobId,
        Func<ExecutionFence, Task> pass,
        CancellationToken ct
    )
    {
        var db = services.GetRequiredService<IDbSession>();
        var found = await db.From<JobStateEntity>().Where(r => r.Id == ownerJobId).SingleOrDefaultAsync(ct);
        if (found is null)
        {
            try
            {
                await pass(await ExecutionFenceStaging.ExecutingAsync(db, ownerJobId, ct));
            }
            finally
            {
                await db.From<JobStateEntity>().Where(r => r.Id == ownerJobId).DeleteAsync(ct);
            }
            return;
        }

        var (status, worker, leaseExpires, resultFormat, result) = (
            found.Status,
            found.LeasedByWorkerId,
            found.LeaseExpiresAtUtc,
            found.ResultFormatId,
            found.Result
        );
        var fence = await ExecutionFenceStaging.ExecutingAsync(db, ownerJobId, ct, executionNumber: found.ExecutionNumber);
        try
        {
            await pass(fence);
        }
        finally
        {
            await db.From<JobStateEntity>()
                .Where(r => r.Id == ownerJobId)
                .UpdateOnlyAsync(
                    () =>
                        new JobStateEntity
                        {
                            Status = status,
                            LeasedByWorkerId = worker,
                            LeaseExpiresAtUtc = leaseExpires,
                            ResultFormatId = resultFormat,
                            Result = result,
                        },
                    ct
                );
        }
    }

    /// <summary>
    /// Runs a job by id until its handler has counted <paramref name="target"/> attempts. RunOnceAsync can
    /// no-op when a claim is lost to provider timing, so the loop is driven by the probe's own count rather
    /// than by ticks, which is what makes the projected event count deterministic; three alert specs used to
    /// carry their own copy of this.
    /// </summary>
    public static async Task RunUntilAttemptsAsync(
        WorkerRuntime runtime,
        EnqueueJobResponse job,
        Func<int> attempts,
        int target,
        CancellationToken ct
    )
    {
        for (var i = 0; i < target + 12 && attempts() < target; i++)
        {
            await runtime.RunOnceAsync(job, ct);
        }

        Assert.Equal(target, attempts());
    }

    /// <summary>
    /// Fires a recurring slot until its handler has counted <paramref name="target"/> fires: the same
    /// count-driven loop as <see cref="RunUntilAttemptsAsync"/>, after pulling the parked slot back to due.
    /// </summary>
    public static async Task FireSlotUntilAsync(
        IServiceProvider services,
        WorkerRuntime runtime,
        long slotId,
        Func<int> fires,
        int target,
        CancellationToken ct
    )
    {
        await MakeSlotClaimableAsync(services, slotId, ct);
        for (var i = 0; i < target + 12 && fires() < target; i++)
        {
            await runtime.RunOnceAsync(slotId, ct);
        }

        Assert.Equal(target, fires());
    }
}

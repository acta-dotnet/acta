using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>The reads one attempt needs before its handler runs.</summary>
internal readonly record struct AttemptSetup(RecurringFirePlan? Fire, StepRetryDefaults StepRetryDefaults, string? TenantKey);

/// <summary>
/// Builds the <see cref="RuntimeJobContext"/> a handler receives: first the reads it needs (the recurring fire plan,
/// the step retry defaults, the tenant key), repeated like the writes are on the host token, then the context.
/// </summary>
internal sealed class AttemptContextFactory(
    IServiceProvider rootServices,
    WorkerContext context,
    IActaClock clock,
    IJobPayloadSerializerRegistry serializers,
    Locks locks,
    IOptions<JobsOptions> options,
    ILogger log,
    ActaMetrics? metrics
)
{
    public async Task<AttemptSetup> ReadSetupAsync(ClaimedJob job, JobDescriptor descriptor, CancellationToken ct)
    {
        var fire = await PlanFireAsync(job, descriptor, ct);
        var backoff = Backoff.Parse(descriptor.Backoff ?? JobDefinitionRegistration.DefaultBackoffExpression);
        var stepRetryDefaults = new StepRetryDefaults(
            descriptor.MaxAttempts,
            DurationSyntax.ToWholeSeconds(backoff.InitialDelay, nameof(backoff)),
            DurationSyntax.ToWholeSeconds(backoff.MaxDelay, nameof(backoff)),
            (decimal)backoff.Multiplier,
            (decimal)backoff.Jitter
        );

        // Resolve the external tenant key off the process-lifetime cache (one point read per
        // distinct tenant); the claim projection itself stays join-free.
        string? tenantKey = null;
        if (job.TenantId is { } jobTenantId)
        {
            var tenants = rootServices.GetRequiredService<TenantKeyCache>();
            tenantKey = await JobFinalizer.RepeatAsync(
                async token => await tenants.ResolveAsync(jobTenantId, token),
                log,
                job.JobId,
                ct,
                metrics
            );
        }
        return new AttemptSetup(fire, stepRetryDefaults, tenantKey);
    }

    public RuntimeJobContext Create(
        ClaimedJob job,
        JobDescriptor descriptor,
        string namespaceName,
        int namespaceId,
        int workerId,
        AttemptSetup setup,
        InFlightJob entry,
        IServiceProvider attemptServices
    ) =>
        new(
            job,
            descriptor.JobName,
            namespaceName,
            namespaceId,
            options.Value.LeaseTtlSeconds,
            rootServices.GetRequiredService<CancelJobHandler>(),
            rootServices.GetRequiredService<ResetJobStateHandler>(),
            rootServices.GetRequiredService<Signals>(),
            rootServices.GetRequiredService<AlertLifecycle>(),
            rootServices.GetRequiredService<Execution>(),
            serializers,
            locks,
            setup.Fire?.TriggeringScheduleNames ?? [],
            // A recurring slot never carries the whole-job deadline: it anchors to job creation
            // and the slot row lives forever, so the Strict paths would cancel the slot
            // terminally and the advisory surface (ctx.IsOverdue, ctx.TimeUntilDeadline) would
            // report permanently overdue to every occurrence. Worker init rejects the
            // Deadline + [JobSchedule] combination; a null here covers slots that predate the
            // rule or gained a deadline through an override, in the one place every consumer
            // reads from.
            setup.Fire
                is null
            && descriptor.DeadlineSeconds is { } deadlineSecs
            && deadlineSecs > 0
                ? job.CreatedAtUtc.AddSeconds(deadlineSecs)
                : (DateTime?)null,
            entry.AttemptToken,
            options.Value.MaxInlinePayloadBytes,
            entry,
            setup.StepRetryDefaults,
            log,
            metrics,
            attemptServices.GetService<IJobs>(),
            setup.TenantKey,
            workerId,
            clock
        );

    /// <summary>
    /// Recurring slot fire: reads live schedules once and plans the due set + cursor advances at a single
    /// captured nowUtc. The due names are visible to the handler; the advances + slot MIN apply at
    /// completion. Null for a one-shot job, which keeps the unchanged one-shot path.
    /// </summary>
    private async Task<RecurringFirePlan?> PlanFireAsync(ClaimedJob job, JobDescriptor descriptor, CancellationToken ct)
    {
        var isRecurring = context.RecurringSlotJobIds.ContainsKey(job.JobId);
        if (!isRecurring && !string.Equals(job.DeduplicationKey, descriptor.JobName, StringComparison.Ordinal))
        {
            return null;
        }

        var nowUtc = await JobFinalizer.RepeatAsync(async token => await clock.GetUtcNowAsync(token), log, job.JobId, ct, metrics);
        var liveSchedules = rootServices.GetRequiredService<GetLiveSchedulesHandler>();
        var live = await JobFinalizer.RepeatAsync(
            async token => (await liveSchedules.HandleAsync(new GetLiveSchedulesQuery(job.JobId), token)).Schedules,
            log,
            job.JobId,
            ct,
            metrics
        );
        // A slot another runtime registered after this one started is not in the startup set. Its
        // deduplication key is its job name, which a one-off may share, so live schedules decide.
        if (!isRecurring && live.Count > 0)
        {
            isRecurring = true;
            context.RecurringSlotJobIds.TryAdd(job.JobId, 0);
        }
        return isRecurring ? ScheduleWalker.PlanFire(live, nowUtc) : null;
    }
}

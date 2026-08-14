using System.Globalization;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Querying;
using Acta.Runtime.Services.Time;
using Cronos;

namespace Acta.Runtime.Modules.Execution.Schedules;

/// <summary>
/// <see cref="ISchedules"/> implementation: the operator pause/resume surface for recurring schedules
/// plus the keyset-paginated schedule list. Resolves the owning recurring slot job, recomputes the
/// misfire-aware slot MIN over the slot's schedules in C# (cron math the database cannot do), then
/// applies the changes through the schedule store's control transitions.
/// </summary>
internal sealed class SchedulesApi(IScheduleStore store, IActaClock clock, WorkerWakeupPublisher wakeupPublisher, JobsService jobs)
    : ISchedules
{
    private const string ListOperationName = "ListJobSchedules";
    private const string OrderSchedules = "next_run_at_utc asc, id asc";

    private static readonly ScheduleControlResult NotFound = new(JobControlAction.NotFound, null, null, null, null);
    private static readonly ScheduleControlResult Rejected = new(JobControlAction.Rejected, null, null, null, null);

    // The control surface is operator/manual only: the actor (Operator) is stamped here, never accepted
    // from the caller, so a caller cannot forge the audit actor.
    private static JobControlActor Operator(string? actorKey) =>
        new(ActorCode.Operator, JobControlActor.SanitizeActorKey(actorKey).Truncate(ActaTextLimits.ActorKey));

    public async ValueTask<ScheduleControlResult> PauseAsync(
        ScheduleLookup schedule,
        DateTime? untilUtc = null,
        string? note = null,
        string? actorKey = null,
        CancellationToken ct = default
    )
    {
        if (await ResolveTargetAsync(schedule, ct) is not { } ctx)
        {
            return NotFound;
        }

        if (untilUtc is { } until && until <= ctx.NowUtc)
        {
            return Rejected;
        }

        // Recompute the slot MIN with this one paused: an indefinite pause drops out of the MIN, a timed
        // pause contributes its wake instant.
        var simulated = Simulate(ctx.Live, ctx.Target.Id, s => s with { Status = ScheduleStatusCode.Paused, PausedUntilUtc = untilUtc });
        var jobNextRun = ScheduleWalker.RecomputeSlotMin(simulated, ctx.NowUtc);

        var outcome = await store.PauseScheduleAsync(
            new PauseScheduleCommand(ctx.JobId, ctx.Target.Name, untilUtc, jobNextRun, Operator(actorKey), Note(note)),
            ct
        );
        await PublishScheduleWakeAsync(jobNextRun, outcome.Action, ct);
        return ToResult(outcome);
    }

    public async ValueTask<ScheduleControlResult> ResumeAsync(
        ScheduleLookup schedule,
        string? note = null,
        string? actorKey = null,
        CancellationToken ct = default
    )
    {
        if (await ResolveTargetAsync(schedule, ct) is not { } ctx)
        {
            return NotFound;
        }

        // Resume reconciles the schedule's stored cursor by its misfire policy (Skip jumps forward,
        // FireOnceCatchUp fires once), then recomputes the slot MIN over the now-active set.
        var t = ctx.Target;
        var reconciled = NextOccurrenceCalculator.Reconcile(
            t.Expression,
            t.TimeZone,
            t.ExpressionKind,
            t.Misfire,
            t.NextRunAtUtc,
            ctx.NowUtc
        );
        var simulated = Simulate(
            ctx.Live,
            t.Id,
            s => s with { Status = ScheduleStatusCode.Active, PausedUntilUtc = null, NextRunAtUtc = reconciled }
        );
        var jobNextRun = ScheduleWalker.RecomputeSlotMin(simulated, ctx.NowUtc);

        var outcome = await store.ResumeScheduleAsync(
            new ResumeScheduleCommand(ctx.JobId, ctx.Target.Name, reconciled, jobNextRun, Operator(actorKey), Note(note)),
            ct
        );
        await PublishScheduleWakeAsync(jobNextRun, outcome.Action, ct);
        return ToResult(outcome);
    }

    public async ValueTask<ScheduleControlResult> UpdateOverridesAsync(
        ScheduleLookup schedule,
        int expectedVersion,
        string? expression,
        string? timeZoneId,
        string? note = null,
        string? actorKey = null,
        CancellationToken ct = default
    )
    {
        if (await ResolveTargetAsync(schedule, ct) is not { } ctx)
        {
            return NotFound;
        }

        var t = ctx.Target;
        if (expression is not null)
        {
            ValidateExpression(expression, t.ExpressionKind);
        }
        if (timeZoneId is not null)
        {
            ValidateTimeZone(timeZoneId);
        }

        var effectiveExpression = expression ?? t.BaseExpression;
        var effectiveTimeZone = timeZoneId ?? t.BaseTimeZone;

        // The stored cursor was computed under the OLD effective expression; discard it and recompute
        // the target's own next occurrence fresh under the new one (mirrors how the walker treats an
        // expression change on reload) before folding it into the slot MIN.
        var scheduleNextRun = NextOccurrenceCalculator.Next(effectiveExpression, effectiveTimeZone, t.ExpressionKind, ctx.NowUtc);
        var simulated = Simulate(
            ctx.Live,
            t.Id,
            s => s with { Expression = effectiveExpression, TimeZone = effectiveTimeZone, NextRunAtUtc = scheduleNextRun }
        );
        var jobNextRun = ScheduleWalker.RecomputeSlotMin(simulated, ctx.NowUtc);

        var reasonMessage = ChangeSummary(t, effectiveExpression, effectiveTimeZone).Truncate(ActaTextLimits.ReasonMessage);
        var outcome = await store.SetScheduleOverridesAsync(
            new SetScheduleOverridesCommand(
                ctx.JobId,
                t.Name,
                expectedVersion,
                expression,
                timeZoneId,
                Note(note),
                scheduleNextRun,
                jobNextRun,
                Operator(actorKey),
                reasonMessage
            ),
            ct
        );
        await PublishScheduleWakeAsync(jobNextRun, outcome.Action, ct);
        return ToResult(outcome);
    }

    public async ValueTask<ScheduleControlResult> TriggerNowAsync(
        ScheduleLookup schedule,
        string? note = null,
        string? actorKey = null,
        CancellationToken ct = default
    )
    {
        if (await ResolveTargetAsync(schedule, ct) is not { } ctx)
        {
            return NotFound;
        }

        // The authoritative paused/in-flight guards live in trigger_schedule_now itself; no C#
        // short-circuit here keeps a single source of truth for transition legality.
        var reason = (note is null ? ctx.Target.Name : $"{ctx.Target.Name}: {Note(note)}").Truncate(ActaTextLimits.ReasonMessage)!;
        var outcome = await store.TriggerScheduleNowAsync(
            new TriggerScheduleNowCommand(ctx.JobId, ctx.Target.Name, Operator(actorKey), reason),
            ct
        );
        if (outcome.Action == JobControlActionInternal.Applied)
        {
            // A trigger always makes the slot due right now, so unlike Pause/Resume this wake is
            // unconditional on Applied rather than gated on an upcoming jobNextRun.
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }
        return ToResult(outcome);
    }

    public async ValueTask<SchedulePreview?> PreviewAsync(ScheduleLookup schedule, int count = 10, CancellationToken ct = default)
    {
        if (await ResolveTargetAsync(schedule, ct) is not { } ctx)
        {
            return null;
        }

        var t = ctx.Target;
        var nextRuns = NextOccurrenceCalculator.Walk(t.Expression, t.TimeZone, t.ExpressionKind, ctx.NowUtc, count);
        return new SchedulePreview(t.Expression, t.TimeZone ?? "UTC", nextRuns);
    }

    public async ValueTask<PagedResult<ScheduleListItem>> ListAsync(ListSchedulesQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = JobsQueryLimits.NormalizePageSize(query.PageSize);
        query = query with
        {
            JobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace)),
            // RHS reads the origin instance (pre-fold); ValidateJobName only null-checks the namespace, so the pre-fold value is fine.
            JobName = QueryValidation.ValidateJobName(query.JobName, query.JobNamespace, nameof(query.JobName)),
        };
        QueryValidation.ValidateEnum(query.Origin, nameof(query.Origin));

        var liveOnly = query.LiveOnly ? true : (bool?)null;
        var tagFilters = TagFilterJson.Normalize(query.Tags, nameof(ListSchedulesQuery));
        var filterHash = QueryFilterHash.Compute([
            ("ns", query.JobNamespace),
            ("name", query.JobName),
            ("origin", Num(query.Origin)),
            ("live", liveOnly?.ToString()),
            ("tags", tagFilters),
        ]);

        DateTime? cursorNextRunAtUtc = null;
        long? cursorId = null;
        if (query.Cursor is not null)
        {
            var keys = PageCursorCodec.Decode(
                query.Cursor,
                ListOperationName,
                OrderSchedules,
                filterHash,
                [CursorKeyKind.Utc, CursorKeyKind.Long]
            );
            cursorNextRunAtUtc = (DateTime)keys[0];
            cursorId = (long)keys[1];
        }

        var page = await store.ListJobSchedulesAsync(
            new SchedulePageRequest(
                query.JobNamespace,
                query.JobName,
                query.Origin,
                liveOnly,
                cursorNextRunAtUtc,
                cursorId,
                pageSize + 1,
                query.IncludeTotal,
                tagFilters
            ),
            ct
        );

        var rows = page.Rows;
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows.Take(pageSize).ToList() : rows;

        // The list excludes rows without a next run, so the cursor key is always present.
        var nextCursor = hasMore
            ? PageCursorCodec.Encode(
                ListOperationName,
                OrderSchedules,
                filterHash,
                [items[^1].NextRunAtUtc!.Value, items[^1].JobScheduleId]
            )
            : null;

        return new PagedResult<ScheduleListItem>(items, nextCursor, hasMore, pageSize, page.Total);
    }

    // Resolve the owning slot job and locate the named schedule among its live (non-orphaned) rows.
    // Null means either the job or the schedule was absent (both surface as NotFound).
    private async ValueTask<(long JobId, DateTime NowUtc, IReadOnlyList<LiveSchedule> Live, LiveSchedule Target)?> ResolveTargetAsync(
        ScheduleLookup schedule,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var scheduleName = IdentifierSyntax.CanonicalizeKebab(
            schedule.ScheduleName,
            nameof(schedule.ScheduleName),
            IdentifierSyntax.ExtendedMaxLength
        );

        var jobId = await jobs.ResolveJobIdAsync(schedule.Job, ct);
        if (jobId is null)
        {
            return null;
        }

        var nowUtc = await clock.GetUtcNowAsync(ct);
        var live = await store.GetLiveSchedulesAsync(jobId.Value, ct);
        var target = live.FirstOrDefault(s => s.Name == scheduleName);
        return target is null ? null : (jobId.Value, nowUtc, live, target);
    }

    private static List<LiveSchedule> Simulate(IReadOnlyList<LiveSchedule> live, long targetId, Func<LiveSchedule, LiveSchedule> change) =>
        [.. live.Select(s => s.Id == targetId ? change(s) : s)];

    private static string? Note(string? note) => note.Truncate(ActaTextLimits.ScheduleNote);

    // Mirrors the [JobSchedule] registration path's validation, restricted to the schedule's existing
    // kind (expression_kind_code carries no override, so an operator override cannot switch Cron<->interval).
    // Cronos/interval parse failures surface as ArgumentException before any DB write.
    private static void ValidateExpression(string expression, ScheduleExpressionKindCode kind)
    {
        try
        {
            if (kind == ScheduleExpressionKindCode.Interval)
            {
                if (NextOccurrenceCalculator.ParseInterval(expression) <= TimeSpan.Zero)
                {
                    throw new ArgumentException($"Expression '{expression}' must be a positive interval duration.", nameof(expression));
                }
            }
            else
            {
                NextOccurrenceCalculator.ParseCron(expression);
            }
        }
        catch (Exception ex) when (ex is FormatException or CronFormatException)
        {
            throw new ArgumentException($"Expression '{expression}' is not a valid schedule expression.", nameof(expression), ex);
        }
    }

    private static void ValidateTimeZone(string timeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"Time zone '{timeZoneId}' is not recognized.", nameof(timeZoneId), ex);
        }
    }

    // A short operator-readable summary of what changed, e.g. "name: expression 0 0 * * * -> */5 * * * *; tz cleared".
    private static string ChangeSummary(LiveSchedule t, string newExpression, string? newTimeZone)
    {
        var parts = new List<string>(2);
        if (t.Expression != newExpression)
        {
            parts.Add($"expression {t.Expression} -> {newExpression}");
        }
        if (t.TimeZone != newTimeZone)
        {
            parts.Add(
                newTimeZone is null ? "tz cleared"
                : t.TimeZone is null ? $"tz set to {newTimeZone}"
                : $"tz {t.TimeZone} -> {newTimeZone}"
            );
        }
        return parts.Count == 0 ? $"{t.Name}: overrides unchanged" : $"{t.Name}: {string.Join("; ", parts)}";
    }

    private static ScheduleControlResult ToResult(ScheduleControlOutcome o) =>
        new((JobControlAction)(byte)o.Action, o.Status, o.PausedUntilUtc, o.NextRunAtUtc, o.Version);

    // When recomputation finds an upcoming slot, the slot job lands ready. It may carry a scheduled run
    // time, but waking idle loops makes them re-read their horizon. Without an upcoming slot, the job
    // stays paused and needs no wake.
    private ValueTask PublishScheduleWakeAsync(DateTime? jobNextRun, JobControlActionInternal action, CancellationToken ct) =>
        action == JobControlActionInternal.Applied && jobNextRun is not null
            ? wakeupPublisher.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct)
            : ValueTask.CompletedTask;

    private static string? Num<T>(T? value)
        where T : struct, Enum =>
        value is null ? null : Convert.ToInt32(value.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
}

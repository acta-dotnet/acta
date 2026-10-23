using Cronos;

namespace Acta;

/// <summary>
/// <see cref="ISchedules.UpdateOverridesAsync"/>: validates the overrides against the schedule's kind,
/// recomputes the schedule and slot cursors under the new effective expression, then one routine call
/// writes the overrides and both cursors, version-guarded.
/// </summary>
internal sealed class SetScheduleOverridesHandler(
    ResolveScheduleTargetHandler resolveScheduleTarget,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup
)
{
    private static readonly SetScheduleOverridesResponse NotFound = new(ControlAction.NotFound, null, null, null, null);

    public async ValueTask<SetScheduleOverridesResponse> HandleAsync(SetScheduleOverridesCommand command, CancellationToken ct)
    {
        if (await resolveScheduleTarget.HandleAsync(new ResolveScheduleTargetQuery(command.Schedule), ct) is not { } target)
        {
            return NotFound;
        }

        var t = target.Schedule;
        if (command.Expression is { } expression)
        {
            ValidateExpression(expression, t.ExpressionKind);
        }
        if (command.TimeZoneId is { } timeZoneId)
        {
            ValidateTimeZone(timeZoneId);
        }

        var effectiveExpression = command.Expression ?? t.BaseExpression;
        var effectiveTimeZone = command.TimeZoneId ?? t.BaseTimeZone;

        // The stored cursor was computed under the OLD effective expression; discard it and recompute
        // the target's own next occurrence fresh under the new one before folding it into the slot MIN.
        var scheduleNextRun = NextOccurrenceCalculator.Next(effectiveExpression, effectiveTimeZone, t.ExpressionKind, target.NowUtc);
        var jobNextRun = target.SlotNextRunWith(s =>
            s with
            {
                Expression = effectiveExpression,
                TimeZoneId = effectiveTimeZone,
                NextOccurrenceAtUtc = scheduleNextRun,
            }
        );

        var changeSummary = ChangeSummary(t, effectiveExpression, effectiveTimeZone).Truncate(ActaTextLimits.EventMessage);
        var row = await session.ExecuteOneAsync(
            new StoreCommand("SetScheduleOverrides"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.JobId, target.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.ScheduleName, t.ScheduleName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedScheduleVersion, command.ExpectedVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ScheduleExpressionOverride, command.Expression));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ScheduleTimeZoneIdOverride, command.TimeZoneId));
                cmd.Parameters.Add(
                    dialect.CreateParameter(ActaSchema.Schedule.ChangeMessage, ScheduleControl.RowReason(command.ReasonMessage))
                );
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.JobNextRunAtUtc, jobNextRun));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.ScheduleState.NextOccurrenceAtUtc, scheduleNextRun));
                cmd.AddEventFields(dialect, changeSummary, ScheduleControl.Details(t.ScheduleName, command.Actor));
            },
            DbProjectionResolver.Resolve<SetScheduleOverridesRow>(),
            ct
        );
        await ScheduleControl.WakeIfDueAsync(wakeup, jobNextRun, row.Action, ct);
        return new SetScheduleOverridesResponse(row.Action, row.Status, row.PausedUntilUtc, row.NextOccurrenceAtUtc, row.Version);
    }

    /// <summary>
    /// Mirrors the [JobSchedule] registration path's validation, restricted to the schedule's existing kind
    /// (expression_kind_code carries no override, so an override cannot switch Cron and interval). Cronos
    /// and interval parse failures surface as ArgumentException before any write.
    /// </summary>
    private static void ValidateExpression(string expression, ScheduleExpressionKindCode kind)
    {
        try
        {
            if (kind == ScheduleExpressionKindCode.Interval)
            {
                // The generator's floor: an interval fires at most once a second, as cron does.
                if (NextOccurrenceCalculator.ParseInterval(expression) < TimeSpan.FromSeconds(1))
                {
                    throw new ArgumentException(
                        $"Expression '{expression}' must be an interval duration of at least one second.",
                        nameof(expression)
                    );
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

    /// <summary>
    /// A short operator-readable summary of what changed, e.g. "expression 0 0 * * * -&gt; */5 * * * *; tz cleared";
    /// the schedule's name rides the event's details.
    /// </summary>
    private static string ChangeSummary(LiveSchedule t, string newExpression, string? newTimeZone)
    {
        var parts = new List<string>(2);
        if (t.Expression != newExpression)
        {
            parts.Add($"expression {t.Expression} -> {newExpression}");
        }
        if (t.TimeZoneId != newTimeZone)
        {
            parts.Add(
                newTimeZone is null ? "tz cleared"
                : t.TimeZoneId is null ? $"tz set to {newTimeZone}"
                : $"tz {t.TimeZoneId} -> {newTimeZone}"
            );
        }
        return parts.Count == 0 ? "overrides unchanged" : string.Join("; ", parts);
    }
}

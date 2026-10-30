using System.Globalization;
using System.Text;

namespace Acta;

/// <summary>
/// <see cref="ICalendars.PublishAsync"/>: plans a publication in C# from a snapshot, then applies it atomically. The
/// plan compiles the candidate and resolves each bound schedule's calendars as they stand, retimes every schedule that
/// names the family (its first occurrence after the database clock, from the candidate), and decides the overdue
/// occurrences the old source left undecided: refused, or discarded as an audited skipped range. A fence another
/// writer moved replans.
/// </summary>
internal sealed class PublishCalendarHandler(
    IDbSession session,
    ISqlDialect dialect,
    IActaClock clock,
    GetCalendarPublicationHandler family,
    GetCalendarSchedulesHandler dependents,
    CalendarSources sources
)
{
    /// <summary>Bound schedules one publication retimes at most; a wider fan-out is refused with its size.</summary>
    internal const int MaxDependents = 200;

    private const int MaxReplans = 3;

    public async Task<PublishCalendarResponse> HandleAsync(PublishCalendarCommand command, CancellationToken ct)
    {
        var (jobNamespace, calendarName) = CalendarControl.Names(command.Calendar);
        if (command.CandidateVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(command), command.CandidateVersion, "The candidate is a version of at least 1.");
        }

        if (command.Overdue is not (OverdueDecisionCode.Refuse or OverdueDecisionCode.Discard))
        {
            throw new ArgumentOutOfRangeException(nameof(command), command.Overdue, "The overdue decision is Refuse or Discard.");
        }

        PublishCalendarResponse? last = null;
        for (var attempt = 0; attempt <= MaxReplans; attempt++)
        {
            var rows = (
                await family.HandleAsync(new GetCalendarPublicationQuery(jobNamespace, calendarName, command.CandidateVersion), ct)
            ).Rows;
            var candidate = rows.FirstOrDefault(r => r.Version == command.CandidateVersion);
            var current = rows.FirstOrDefault(r => r.Status == CalendarStatusCode.Published);
            if (candidate is null)
            {
                return new PublishCalendarResponse(ControlAction.NotFound, null, [], [], null);
            }

            if ((current?.Version ?? 0) != command.ExpectedCurrentVersion)
            {
                return new PublishCalendarResponse(
                    ControlAction.VersionConflict,
                    current?.Version,
                    [],
                    [],
                    current is null
                        ? "The family has no published version; expect 0."
                        : $"The current published version is {current.Version}, not {command.ExpectedCurrentVersion}."
                );
            }

            if (candidate.Status != CalendarStatusCode.Draft)
            {
                return Rejected($"Version {candidate.Version} is {candidate.Status.Code}; only a draft is published.");
            }

            var bound = (await dependents.HandleAsync(new GetCalendarSchedulesQuery(candidate.NamespaceId, calendarName), ct)).Schedules;
            if (bound.Count > MaxDependents)
            {
                return Rejected($"{bound.Count} schedules bind '{calendarName}'; a publication retimes at most {MaxDependents}.");
            }

            var nowUtc = await clock.GetUtcNowAsync(ct);
            var compiled = CalendarDefinition.Compile(candidate.Role, candidate.Definition);
            foreach (var ns in bound.GroupBy(s => s.NamespaceId))
            {
                await sources.PreloadAsync(ns.Key, ns.SelectMany(s => s.Timing().CalendarNames()), ct);
            }

            var plan = new List<PublishCalendarValues>(bound.Count);
            var overdue = new List<string>();
            foreach (var s in bound)
            {
                // An orphaned schedule has no cursor to move; its declaration comes back with its definition.
                if (s.Status == ScheduleStatusCode.Orphaned)
                {
                    continue;
                }

                var oldSource = sources.Source(s.NamespaceId, s.Timing());
                var newSource = Rebind(oldSource, calendarName, compiled);
                DateTime? next;
                try
                {
                    next = NextOccurrenceCalculator.Next(newSource, nowUtc);
                }
                catch (CalendarUnavailableException)
                {
                    // Its other calendar resolves to nothing, so the admitter already parks it as an evaluation error;
                    // the publication leaves it as it is rather than failing for every bound schedule.
                    continue;
                }

                string? skipped = null;
                if (s.NextOccurrenceAtUtc is { } due && due <= nowUtc)
                {
                    if (command.Overdue == OverdueDecisionCode.Refuse)
                    {
                        overdue.Add(Path(s));
                        continue;
                    }

                    var through = ScheduleEvaluator.LatestDue(oldSource, DateTime.SpecifyKind(due, DateTimeKind.Utc), nowUtc);
                    skipped = EventDetails.Text(
                        ("schedule", s.ScheduleName),
                        ("from", EventDetails.Instant(due)),
                        ("through", EventDetails.Instant(through)),
                        ("reason", "retimed")
                    );
                }

                plan.Add(
                    new PublishCalendarValues(
                        s.ScheduleId,
                        s.ScheduleVersion,
                        s.StateVersion,
                        next,
                        EventDetails.Text(
                            ("schedule", s.ScheduleName),
                            ("calendar", calendarName),
                            ("fromVersion", (current?.Version ?? 0).ToString(CultureInfo.InvariantCulture)),
                            ("toVersion", candidate.Version.ToString(CultureInfo.InvariantCulture))
                        )!,
                        skipped
                    )
                );
            }

            if (overdue.Count > 0)
            {
                return new PublishCalendarResponse(
                    ControlAction.Rejected,
                    current?.Version,
                    [],
                    overdue,
                    $"{overdue.Count} schedule(s) hold undecided overdue occurrences under version {current?.Version ?? 0}; drain them, or publish with the Discard decision."
                );
            }

            var outcome = await session.ExecuteOneAsync(
                new StoreCommand("PublishCalendar"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.NamespaceId, candidate.NamespaceId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, calendarName));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedCurrentVersion, command.ExpectedCurrentVersion));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CandidateVersion, candidate.Version));
                    cmd.AddEventFields(
                        dialect,
                        command.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
                        EventDetails.Write(
                            ("calendar", calendarName),
                            ("version", candidate.Version.ToString(CultureInfo.InvariantCulture)),
                            ("fromVersion", (current?.Version ?? 0).ToString(CultureInfo.InvariantCulture)),
                            ("overdue", command.Overdue.Code),
                            ("affected", plan.Count.ToString(CultureInfo.InvariantCulture)),
                            ("actor", JobControlActor.Operator(command.Actor))
                        )
                    );
                    dialect.BindPublishCalendar(cmd, plan);
                },
                DbProjectionResolver.Resolve<PublishCalendarRow>(),
                ct
            );
            last = new PublishCalendarResponse(
                outcome.Action,
                outcome.Action == ControlAction.Applied ? candidate.Version : outcome.CurrentVersion,
                outcome.Action == ControlAction.Applied ? [.. bound.Select(Path)] : [],
                [],
                outcome.Action == ControlAction.Applied
                    ? null
                    : "Another writer moved the calendar or a bound schedule since the plan was made."
            );
            if (outcome.Action != ControlAction.Rejected)
            {
                return last;
            }
        }

        return last!;

        static PublishCalendarResponse Rejected(string reason) => new(ControlAction.Rejected, null, [], [], reason);
    }

    /// <summary>A bound schedule's path, the way <see cref="ScheduleLookup"/> addresses it.</summary>
    internal static string Path(DueSchedule s) => $"{s.NamespaceName}/{s.DefinitionName}/{s.ScheduleName}";

    /// <summary>
    /// The source with the candidate in place of the family wherever the timing names <paramref name="calendarName"/> in
    /// the candidate's role; the other calendar stays as it resolved.
    /// </summary>
    private static ScheduleSource Rebind(ScheduleSource source, string calendarName, CompiledCalendar candidate)
    {
        var timing = source.Timing;
        if (candidate.Role == CalendarRoleCode.Occurrence)
        {
            if (timing.ExpressionKind == ScheduleExpressionKindCode.Calendar && timing.Expression == calendarName)
            {
                return source with { Calendar = candidate.Calendar };
            }
        }
        else if (timing.AvailabilityCalendarName == calendarName)
        {
            return source with { Availability = candidate.Availability };
        }

        return source;
    }
}

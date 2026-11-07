using System.Globalization;
using System.Text;

namespace Acta;

/// <summary>
/// <see cref="ICalendars.PublishAsync"/>: plans a publication in C# from a snapshot, then applies it atomically. The
/// plan compiles the candidate and resolves each bound schedule's calendars as they stand, retimes every schedule that
/// names the family (its first occurrence after the database clock, from the candidate), and decides the overdue
/// occurrences the old source left undecided: refused, or discarded as an audited skipped range. A fence another
/// writer moved replans. Publishing the current version plans and writes nothing.
/// </summary>
internal sealed class PublishCalendarHandler(
    IDbSession session,
    ISqlDialect dialect,
    IActaClock clock,
    GetCalendarPublicationHandler family,
    ListCalendarSchedulesHandler dependents,
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
            var (current, candidate) = await family.HandleAsync(
                new GetCalendarPublicationQuery(jobNamespace, calendarName, command.CandidateVersion),
                ct
            );
            if (candidate is null)
            {
                return new PublishCalendarResponse(ControlAction.NotFound, null, [], [], null);
            }

            // The one Published version is the current one, so the call asks for what already holds, whatever it expected.
            if (candidate.Status == CalendarStatusCode.Published)
            {
                return new PublishCalendarResponse(ControlAction.AlreadyInState, candidate.Version, [], [], null);
            }

            if (command.ExpectedCurrentVersion is { } expected && (current?.Version ?? 0) != expected)
            {
                return new PublishCalendarResponse(
                    ControlAction.VersionConflict,
                    current?.Version,
                    [],
                    [],
                    current is null
                        ? "The family has no published version; expect 0."
                        : $"The current published version is {current.Version}, not {expected}."
                );
            }

            if (candidate.Status != CalendarStatusCode.Draft)
            {
                return Rejected($"Version {candidate.Version} is {candidate.Status.Code}; only a draft is published.");
            }

            var bound = (await dependents.HandleAsync(new ListCalendarSchedulesQuery(candidate.NamespaceId, calendarName), ct)).Schedules;
            if (bound.Count > MaxDependents)
            {
                return Rejected($"{bound.Count} schedules bind '{calendarName}'; a publication retimes at most {MaxDependents}.");
            }

            var nowUtc = await clock.GetUtcNowAsync(ct);
            var compiled = CalendarDefinition.Compile(candidate.Role, candidate.Definition);
            var resolvers = new Dictionary<int, Func<ScheduleTiming, ScheduleSource>>();
            foreach (var ns in bound.GroupBy(s => s.NamespaceId))
            {
                resolvers[ns.Key] = await sources.PreloadAsync(ns.Key, ns.SelectMany(s => s.Timing().CalendarNames()), ct);
            }

            var plan = new List<PublishCalendarValues>(bound.Count);
            var retimed = new List<string>(bound.Count);
            var overdue = new List<string>();
            foreach (var s in bound)
            {
                // An orphaned schedule has no cursor to move; its declaration comes back with its definition. The apply
                // still checks it has not moved, as it does every schedule that names the family.
                var unchanged = new PublishCalendarValues(s.ScheduleId, s.ScheduleVersion, s.StateVersion, null, null, null);
                if (s.Status == ScheduleStatusCode.Orphaned)
                {
                    plan.Add(unchanged);
                    continue;
                }

                var oldSource = resolvers[s.NamespaceId](s.Timing());
                var newSource = Rebind(oldSource, calendarName, compiled);
                DateTime? next;
                string? skipped = null;
                try
                {
                    next = NextOccurrenceCalculator.Next(newSource, nowUtc);
                    if (s.NextOccurrenceAtUtc is { } due && due <= nowUtc)
                    {
                        if (command.Overdue == OverdueDecisionCode.Refuse)
                        {
                            overdue.Add(Path(s));
                            continue;
                        }

                        var through = ScheduleEvaluator.LatestDue(oldSource, DateTime.SpecifyKind(due, DateTimeKind.Utc), nowUtc).Latest;
                        skipped = EventDetails.Text(
                            ("schedule", s.ScheduleName),
                            ("from", EventDetails.Instant(due)),
                            ("through", EventDetails.Instant(through)),
                            ("reason", "retimed")
                        );
                    }
                }
                catch (CalendarUnavailableException)
                {
                    // Its other calendar, or the family before this version, resolves to nothing, so the admitter
                    // already parks it as an evaluation error; the publication leaves it as it is rather than failing
                    // for every bound schedule.
                    plan.Add(unchanged);
                    continue;
                }

                retimed.Add(Path(s));
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
                    // The apply fences the current version the plan read, which is the caller's when it named one.
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedCurrentVersion, current?.Version ?? 0));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CandidateVersion, candidate.Version));
                    cmd.AddEventFields(
                        dialect,
                        command.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
                        EventDetails.Write(
                            ("calendar", calendarName),
                            ("version", candidate.Version.ToString(CultureInfo.InvariantCulture)),
                            ("fromVersion", (current?.Version ?? 0).ToString(CultureInfo.InvariantCulture)),
                            ("overdue", command.Overdue.Code),
                            ("affected", retimed.Count.ToString(CultureInfo.InvariantCulture)),
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
                outcome.Action == ControlAction.Applied ? retimed : [],
                [],
                outcome.Action == ControlAction.Applied
                    ? null
                    : "Another writer moved the calendar or a bound schedule since the plan was made."
            );
            // The apply answers NotFound when the candidate is no longer the draft the plan read, as when a concurrent call
            // published it, so the plan is made again and answers what the candidate is now.
            if (outcome.Action is not (ControlAction.Rejected or ControlAction.NotFound))
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

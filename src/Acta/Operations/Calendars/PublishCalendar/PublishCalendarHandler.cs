using System.Globalization;
using System.Text;

namespace Acta;

/// <summary>
/// <see cref="ICalendars.PublishAsync"/>: plans a publication in C# from a snapshot, then applies it atomically. The
/// plan compiles the candidate and the current version, retimes every schedule bound to the family (its pinned
/// version, fingerprint, and first occurrence after the database clock), and decides the overdue occurrences the old
/// version left undecided: refused, or discarded as an audited skipped range. A fence another writer moved replans.
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
            var plan = new List<PublishCalendarValues>(bound.Count);
            var overdue = new List<string>();
            foreach (var s in bound)
            {
                var old = s.Timing();
                var retimed = Rebind(old, candidate.Role, candidate.Version);
                var newSource = await sources.SourceAsync(s.NamespaceId, retimed, ct);
                var next = retimed.IntervalAnchorAtUtc is { } anchor
                    ? NextOccurrenceCalculator.FirstAfter(newSource, anchor, nowUtc)
                    : NextOccurrenceCalculator.Next(newSource, nowUtc);
                string? skipped = null;
                if (s.Status != ScheduleStatusCode.Orphaned && s.NextOccurrenceAtUtc is { } due && due <= nowUtc)
                {
                    if (command.Overdue == OverdueDecisionCode.Refuse)
                    {
                        overdue.Add(Path(s));
                        continue;
                    }

                    var oldSource = await sources.SourceAsync(s.NamespaceId, old, ct);
                    var through = ScheduleEvaluator.LatestDue(oldSource, DateTime.SpecifyKind(due, DateTimeKind.Utc), nowUtc);
                    skipped = Facts(
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
                        candidate.Role == CalendarRoleCode.Occurrence ? candidate.Version : null,
                        candidate.Role == CalendarRoleCode.Availability ? candidate.Version : null,
                        TimingFingerprint.Compute(retimed),
                        next,
                        Facts(
                            ("schedule", s.ScheduleName),
                            ("calendar", calendarName),
                            ("fromVersion", (current?.Version ?? 0).ToString(CultureInfo.InvariantCulture)),
                            ("toVersion", candidate.Version.ToString(CultureInfo.InvariantCulture))
                        ),
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

    /// <summary>The schedule's timing with the family's binding moved to <paramref name="version"/>; the other binding is untouched.</summary>
    internal static ScheduleTiming Rebind(ScheduleTiming timing, CalendarRoleCode role, int version) =>
        role == CalendarRoleCode.Occurrence ? timing with { CalendarVersion = version } : timing with { AvailabilityVersion = version };

    /// <summary>A decision's details as the JSON text the apply stores, in the form every schedule event carries.</summary>
    private static string Facts(params ReadOnlySpan<(string Name, string? Value)> facts) =>
        Encoding.UTF8.GetString(EventDetails.Write(facts)!);
}

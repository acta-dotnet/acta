using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test support for the calendar catalog specs: one definition per role, a publication written straight to a version's
/// row, and a per-occurrence definition whose schedules name an availability calendar.
/// </summary>
internal static class CalendarTestOps
{
    public const string AvailabilityDefinition = """
        {"kind":"weekly-availability","timeZone":"schedule","weekly":[{"days":["Monday","Tuesday","Wednesday","Thursday","Friday"],"windows":[["09:00","12:00"],["13:00","17:00"]]}],"dateOverrides":{"2027-12-25":[]},"excludedRanges":[]}
        """;

    public const string OccurrenceDefinition = """
        {"kind":"explicit-instants","timeZone":"UTC","instants":["2027-01-15T16:00:00Z","2027-02-15T16:00:00Z"]}
        """;

    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static ICalendars Calendars(IServiceProvider services) => services.GetRequiredService<IActaOperations>().Calendars;

    /// <summary>Marks one version Published on its row, as a publication leaves the family's current version.</summary>
    public static async Task PublishAsync(IDbSession db, int namespaceId, string calendarName, int version, CancellationToken ct)
    {
        var publishedAtUtc = DateTime.UtcNow;
        await db.From<CalendarEntity>()
            .Where(c => c.NamespaceId == namespaceId && c.CalendarName == calendarName && c.Version == version)
            .UpdateOnlyAsync(() => new CalendarEntity { Status = CalendarStatusCode.Published, PublishedAtUtc = publishedAtUtc }, ct);
    }

    /// <summary>
    /// Registers a per-occurrence definition with one schedule per name, each filtered by the availability calendar
    /// <paramref name="calendarName"/>; returns the definition's id.
    /// </summary>
    public static async Task<int> BindAvailabilityAsync(
        IServiceProvider services,
        int namespaceId,
        string jobName,
        string calendarName,
        IReadOnlyList<string> scheduleNames,
        CancellationToken ct
    )
    {
        var defId = (await DefinitionTestOps.RegisterAsync(services, namespaceId, Generation, [Def(jobName)], ct))[jobName];
        await RegisterAsync(services, namespaceId, defId, calendarName, scheduleNames, ct);
        return defId;
    }

    /// <summary>Re-registers the definition's schedules as <paramref name="scheduleNames"/>; a schedule it drops is orphaned.</summary>
    public static Task RegisterAsync(
        IServiceProvider services,
        int namespaceId,
        int defId,
        string calendarName,
        IReadOnlyList<string> scheduleNames,
        CancellationToken ct
    )
    {
        var cursor = DateTime.UtcNow.Date.AddDays(1);
        var slots = scheduleNames
            .Select(name => new SlotSchedule(
                name,
                "*/5 * * * *",
                null,
                MisfireStrategyCode.Skip,
                ScheduleExpressionKindCode.Cron,
                null,
                cursor,
                AvailabilityCalendarName: calendarName
            ))
            .ToList();
        return ScheduleTestOps.RegisterAsync(
            services,
            [
                new DefinitionSchedules(
                    namespaceId,
                    defId,
                    0,
                    ReadOnlyMemory<byte>.Empty,
                    EventRecordingCode.Audit,
                    ScheduledDeliveryCode.PerOccurrence,
                    slots
                ),
            ],
            ct
        );
    }

    private static JobDescriptor Def(string name) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: typeof(int),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            Priority: default,
            MaxAttempts: 1,
            EventRecording: EventRecordingCode.Audit,
            AlertProfile: default,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.PerOccurrence,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );
}

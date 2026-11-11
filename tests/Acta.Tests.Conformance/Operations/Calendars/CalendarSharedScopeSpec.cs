using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Calendars;

/// <summary>
/// Conformance for the shared scope: a calendar saved in <c>sys</c> is one family that every namespace reads and lists
/// and that no namespace can shadow. Every write in <c>sys</c> takes the schedule lock each deploy in the schema shares,
/// so it holds up the rest of a parallel run: the facts that need one live here and stay few, and a calendar rule that
/// is not about the shared scope is held on a namespace's own calendar.
/// </summary>
[ConformanceSpec(
    "calendar.shared-scope",
    "A calendar saved in sys is one family every namespace reads and none can shadow",
    Area = "Scheduling",
    Contract = "A family in sys reads back from sys, resolves and lists in every namespace under its latest status, and takes its name in both scopes whichever saves first."
)]
public sealed class CalendarSharedScopeSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ICalendars Calendars => CalendarTestOps.Calendars(Services);

    [Fact(DisplayName = "A shared family reads back from sys, resolves from a namespace, and lists there under its latest status")]
    public async Task A_shared_family_is_read_and_listed_from_a_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var own = new CalendarLookup(TestNamespace, Unique("a-own"));
        var shared = new CalendarLookup("sys", Unique("b-shared"));
        foreach (var calendar in new[] { own, shared })
        {
            await Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct);
        }

        // The publication's read finds the family in sys, under the sys namespace id.
        var (current, candidate) = await Services
            .GetRequiredService<GetCalendarPublicationHandler>()
            .HandleAsync(new GetCalendarPublicationQuery("sys", shared.CalendarName, 1), ct);
        Assert.Null(current);
        Assert.NotNull(candidate);
        Assert.Equal(
            (NameSyntax.SystemNamespaceId, 1, CalendarStatusCode.Draft),
            (candidate.NamespaceId, candidate.Version, candidate.Status)
        );

        // The engine resolves the version for a namespace that holds no family of that name.
        var resolved = await Services
            .GetRequiredService<CalendarsOperations>()
            .GetCalendarVersionAsync(TestNamespaceId, shared.CalendarName, 1, ct);
        Assert.NotNull(resolved);
        Assert.Equal(CalendarRoleCode.Availability, resolved.Role);

        // A namespace's list adds the shared family beside its own, the sys list holds the shared one alone, and a list
        // with no namespace holds both.
        Assert.Equal(
            [(TestNamespace, own.CalendarName), ("sys", shared.CalendarName)],
            await ListAsync(TestNamespace, CalendarStatusCode.Draft, ct)
        );
        Assert.Equal([("sys", shared.CalendarName)], await ListAsync("sys", null, ct));
        Assert.Equal([(TestNamespace, own.CalendarName), ("sys", shared.CalendarName)], await ListAsync(null, null, ct));

        // The status filter follows the shared family's latest version as it follows the namespace's own.
        Assert.Equal(ControlAction.Applied, (await Calendars.PublishAsync(shared, 1, 0, ct: ct)).Action);
        Assert.Equal([("sys", shared.CalendarName)], await ListAsync(TestNamespace, CalendarStatusCode.Published, ct));
        Assert.Equal([(TestNamespace, own.CalendarName)], await ListAsync(TestNamespace, CalendarStatusCode.Draft, ct));
    }

    [Fact(DisplayName = "A shared calendar and a namespace's cannot share a name, in either order")]
    public async Task A_name_belongs_to_one_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = Unique("holidays");
        var shared = new CalendarLookup("sys", name);
        var local = new CalendarLookup(TestNamespace, name);
        Assert.Equal(
            ControlAction.Applied,
            (
                await Calendars.SaveDraftAsync(shared, CalendarRoleCode.Availability, CalendarTestOps.AvailabilityDefinition, 0, ct: ct)
            ).Action
        );

        var shadowing = await Calendars.SaveDraftAsync(
            local,
            CalendarRoleCode.Availability,
            CalendarTestOps.AvailabilityDefinition,
            0,
            ct: ct
        );

        Assert.Equal(ControlAction.Rejected, shadowing.Action);
        Assert.Contains("shared calendar in sys", Assert.Single(shadowing.Problems), StringComparison.Ordinal);
        var other = Unique("cutoffs");
        Assert.Equal(
            ControlAction.Applied,
            (
                await Calendars.SaveDraftAsync(
                    new CalendarLookup(TestNamespace, other),
                    CalendarRoleCode.Occurrence,
                    CalendarTestOps.OccurrenceDefinition,
                    0,
                    ct: ct
                )
            ).Action
        );
        var taken = await Calendars.SaveDraftAsync(
            new CalendarLookup("sys", other),
            CalendarRoleCode.Occurrence,
            CalendarTestOps.OccurrenceDefinition,
            0,
            ct: ct
        );
        Assert.Equal(ControlAction.Rejected, taken.Action);
        Assert.Contains($"namespace '{TestNamespace}'", Assert.Single(taken.Problems), StringComparison.Ordinal);
    }

    [Fact(
        DisplayName = "Two first drafts of one name at the same moment, one shared and one in a namespace, save one and reject the other"
    )]
    public async Task Two_first_drafts_of_one_name_in_both_scopes_save_only_one()
    {
        var ct = TestContext.Current.CancellationToken;
        // Without the name's lock both saves apply in every round, so two rounds show it, and each one writes in sys.
        for (var round = 0; round < 2; round++)
        {
            var name = Unique($"race-{round}");
            var saves = await Task.WhenAll(
                Task.Run(() => SaveAsync(new CalendarLookup("sys", name)), ct),
                Task.Run(() => SaveAsync(new CalendarLookup(TestNamespace, name)), ct)
            );

            Assert.Equal(1, saves.Count(save => save.Action == ControlAction.Applied));
            Assert.Equal(1, saves.Count(save => save.Action == ControlAction.Rejected));
        }

        Task<SaveCalendarDraftResponse> SaveAsync(CalendarLookup calendar) =>
            Calendars.SaveDraftAsync(calendar, CalendarRoleCode.Occurrence, CalendarTestOps.OccurrenceDefinition, 0, ct: ct).AsTask();
    }

    /// <summary>
    /// This test's families in a list, by name. Shared families other runs left in <c>sys</c> list too and outlive a run
    /// on a server, so every read searches by the test's id.
    /// </summary>
    private async Task<(string JobNamespace, string CalendarName)[]> ListAsync(
        string? jobNamespace,
        CalendarStatusCode? latestStatus,
        CancellationToken ct
    ) =>
        [
            .. (
                await Calendars.ListAsync(new ListCalendarsQuery(jobNamespace, NameContains: TestId, LatestStatus: latestStatus), ct)
            ).Items.Select(i => (i.JobNamespace, i.CalendarName)),
        ];
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Acta.AspNetCore;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The calendar catalog routes over the in-process host: the list with and without a namespace, a family and one of its
/// versions, a version's preview, and the draft, publish, and archive verbs with their status mapping (200, 409, 404).
/// </summary>
public sealed class CalendarEndpointTests
{
    private const string Prefix = "/acta/api/v1/calendars";
    private const string Confirm = "X-Acta-Control";

    private static Task<(WebApplication App, HttpClient Client)> StartWithControlsAsync(FakeOperations fakes) =>
        TestDashboardHost.StartAuthenticatedAsync("ops-lead", options => options.EnableControls = true, fakes);

    private static HttpRequestMessage Post(string path, object body, bool confirm = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Prefix + path);
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }

        request.Content = JsonContent.Create(body, body.GetType());
        return request;
    }

    [Fact]
    public async Task The_list_without_a_namespace_lists_every_family()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(Prefix, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(fakes.Calendars.ListQueries).JobNamespace);
        Assert.Contains("\"calendarName\":\"office-hours\"", body);
        Assert.Contains("\"jobNamespace\":\"sys\"", body);
        Assert.Contains("\"role\":\"availability\"", body);
    }

    [Fact]
    public async Task The_list_binds_its_namespace_role_search_and_paging()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            $"{Prefix}?jobNamespace=billing&role=availability&nameSearch=hours&pageSize=5&includeTotal=true",
            ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new ListCalendarsQuery("billing", CalendarRoleCode.Availability, "hours", 5, null, true),
            Assert.Single(fakes.Calendars.ListQueries)
        );
    }

    [Fact]
    public async Task The_list_answers_an_unknown_role_with_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var response = await client.GetAsync($"{Prefix}?role=hourly", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Calendars.ListQueries);
    }

    [Fact]
    public async Task The_detail_reads_a_family_its_version_or_answers_404()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var current = await client.GetAsync($"{Prefix}/Billing/Office-Hours", ct);
        var versioned = await client.GetAsync($"{Prefix}/billing/office-hours?version=2", ct);
        var shared = await client.GetAsync($"{Prefix}/sys/holidays", ct);
        var missing = await client.GetAsync($"{Prefix}/billing/missing", ct);
        var noSuchVersion = await client.GetAsync($"{Prefix}/billing/office-hours?version=7", ct);

        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using var family = JsonDocument.Parse(await current.Content.ReadAsStringAsync(ct));
        Assert.Equal(1, family.RootElement.GetProperty("shownVersion").GetInt32());
        Assert.Equal("""{"kind":"weekly-availability"}""", family.RootElement.GetProperty("definition").GetString());
        Assert.Equal(1, family.RootElement.GetProperty("dependents")[0].GetProperty("pinnedVersion").GetInt32());
        Assert.Contains("\"shownVersion\":2", await versioned.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("Calendar not found.", await missing.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.NotFound, noSuchVersion.StatusCode);
        Assert.Equal(
            [
                (new CalendarLookup("billing", "office-hours"), (int?)null),
                (new CalendarLookup("billing", "office-hours"), 2),
                (new CalendarLookup("sys", "holidays"), null),
                (new CalendarLookup("billing", "missing"), null),
                (new CalendarLookup("billing", "office-hours"), 7),
            ],
            fakes.Calendars.Reads
        );
    }

    [Theory]
    [InlineData("bad!/office-hours")]
    [InlineData("billing/sys.holidays")]
    [InlineData("billing/office-hours?version=two")]
    public async Task The_detail_answers_a_malformed_key_or_version_with_400(string path)
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var response = await client.GetAsync($"{Prefix}/{path}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Calendars.Reads);
    }

    [Fact]
    public async Task The_preview_forwards_its_version_horizon_and_zone()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var defaulted = await client.GetAsync($"{Prefix}/billing/office-hours/preview?version=2", ct);
        var explicitly = await client.GetAsync(
            $"{Prefix}/billing/office-hours/preview?version=1&fromUtc=2027-01-04T00:00:00Z&limit=3&timeZoneId=Europe/Ljubljana",
            ct
        );
        var missing = await client.GetAsync($"{Prefix}/billing/office-hours/preview?version=7", ct);

        Assert.Equal(HttpStatusCode.OK, defaulted.StatusCode);
        Assert.Contains("\"windows\":[{\"startUtc\":", await defaulted.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.OK, explicitly.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(
            [
                (new CalendarLookup("billing", "office-hours"), 2, (DateTime?)null, 10, (string?)null),
                (
                    new CalendarLookup("billing", "office-hours"),
                    1,
                    new DateTime(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc),
                    3,
                    "Europe/Ljubljana"
                ),
                (new CalendarLookup("billing", "office-hours"), 7, null, 10, null),
            ],
            fakes.Calendars.Previews
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("?version=2&timeZoneId=Mars/Olympus_Mons")]
    [InlineData("?version=2&fromUtc=yesterday")]
    public async Task The_preview_answers_a_missing_version_or_malformed_input_with_400(string query)
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var response = await client.GetAsync($"{Prefix}/billing/office-hours/preview{query}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Calendars.Previews);
    }

    [Fact]
    public async Task A_draft_passes_the_definition_through_and_answers_its_version()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.SendAsync(
            Post(
                "/billing/office-hours/drafts",
                new
                {
                    role = "availability",
                    definition = new { kind = "weekly-availability", timeZone = "schedule" },
                    expectedLatestVersion = 2,
                    reasonMessage = "  new hours  ",
                }
            ),
            ct
        );
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        Assert.Contains("\"version\":3", body);
        Assert.Equal(
            (
                new CalendarLookup("billing", "office-hours"),
                CalendarRoleCode.Availability,
                """{"kind":"weekly-availability","timeZone":"schedule"}""",
                2,
                "new hours",
                "ops-lead"
            ),
            Assert.Single(fakes.Calendars.Drafts)
        );
    }

    [Fact]
    public async Task A_rejected_draft_answers_409_with_its_problems()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.SendAsync(
            Post("/billing/rejected/drafts", new { role = "availability", definition = new { kind = "weekly-availability" } }),
            ct
        );
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("\"action\":\"rejected\"", body);
        Assert.Contains("\"problems\":[\"$.weekly[0].days[0]: unknown day.\"]", body);
    }

    [Theory]
    [InlineData("{\"role\":\"availability\",\"expectedLatestVersion\":0}")]
    [InlineData("{\"role\":\"availability\",\"definition\":\"{}\",\"expectedLatestVersion\":0}")]
    [InlineData("{\"role\":\"unspecified\",\"definition\":{},\"expectedLatestVersion\":0}")]
    public async Task A_draft_without_an_object_definition_or_a_known_role_is_400(string json)
    {
        var fakes = new FakeOperations();
        fakes.Calendars.SaveDraft = static (_, role, _, _, _, _, _) =>
            role is CalendarRoleCode.Occurrence or CalendarRoleCode.Availability
                ? ValueTask.FromResult(new SaveCalendarDraftResponse(ControlAction.Applied, 1, "hash-1", []))
                : throw new ArgumentOutOfRangeException(nameof(role), role, "A calendar's role is Occurrence or Availability.");
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Prefix}/billing/office-hours/drafts");
        request.Headers.Add(Confirm, "true");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_publish_passes_its_versions_and_overdue_decision_through()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var applied = await client.SendAsync(
            Post(
                "/billing/office-hours/publish",
                new
                {
                    expectedCurrentVersion = 1,
                    candidateVersion = 2,
                    overdueDecision = "discard",
                }
            ),
            ct
        );
        var defaulted = await client.SendAsync(
            Post("/billing/office-hours/publish", new { expectedCurrentVersion = 1, candidateVersion = 2 }),
            ct
        );

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Contains("\"retimedSchedules\":[\"send-invoice/nightly\"]", await applied.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.OK, defaulted.StatusCode);
        Assert.Equal([OverdueDecisionCode.Discard, OverdueDecisionCode.Refuse], fakes.Calendars.Publications.Select(p => p.Overdue));
        Assert.All(
            fakes.Calendars.Publications,
            p => Assert.Equal((1, 2, "ops-lead"), (p.ExpectedCurrentVersion, p.CandidateVersion, p.Actor))
        );
    }

    [Fact]
    public async Task A_stale_publish_answers_409_with_the_current_version()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.SendAsync(
            Post("/billing/office-hours/publish", new { expectedCurrentVersion = 999, candidateVersion = 2 }),
            ct
        );
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("\"action\":\"versionConflict\"", body);
        Assert.Contains("\"version\":1", body);
    }

    [Theory]
    [InlineData("office-hours", HttpStatusCode.OK, "applied")]
    [InlineData("rejected", HttpStatusCode.Conflict, "rejected")]
    [InlineData("missing", HttpStatusCode.NotFound, "notFound")]
    public async Task An_archive_maps_its_outcome(string calendarName, HttpStatusCode status, string action)
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.SendAsync(
            Post($"/sys/{calendarName}/archive", new { expectedCurrentVersion = 1, reasonMessage = "retired" }),
            ct
        );

        Assert.Equal(status, response.StatusCode);
        Assert.Contains($"\"action\":\"{action}\"", await response.Content.ReadAsStringAsync(ct));
        Assert.Equal((new CalendarLookup("sys", calendarName), 1, "retired", "ops-lead"), Assert.Single(fakes.Calendars.Archives));
    }

    [Theory]
    [InlineData("drafts")]
    [InlineData("publish")]
    [InlineData("archive")]
    public async Task A_verb_without_the_confirmation_header_or_a_body_never_runs(string verb)
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        var bare = new HttpRequestMessage(HttpMethod.Post, $"{Prefix}/billing/office-hours/{verb}");
        bare.Headers.Add(Confirm, "true");

        var unconfirmed = await client.SendAsync(
            Post($"/billing/office-hours/{verb}", new { expectedCurrentVersion = 1 }, confirm: false),
            ct
        );
        var bodiless = await client.SendAsync(bare, ct);

        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, bodiless.StatusCode);
        Assert.Empty(fakes.Calendars.Drafts);
        Assert.Empty(fakes.Calendars.Publications);
        Assert.Empty(fakes.Calendars.Archives);
    }

    [Theory]
    [InlineData("drafts", "calendars.drafts")]
    [InlineData("publish", "calendars.publish")]
    [InlineData("archive", "calendars.archive")]
    public void A_calendar_verb_reaches_the_authorizer_under_its_family(string verb, string expected) =>
        Assert.Equal(
            expected,
            ControlAuthorizationFilter.DeriveVerb($"/acta/api/v1/calendars/{{jobNamespace}}/{{calendarName}}/{verb}", "POST")
        );
}

using System.Net;
using System.Net.Http.Headers;
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
/// versions, the pages of its versions and dependents, the calendar screen, a version's preview, and the draft, publish,
/// and archive verbs with their status mapping (200, 409, 404).
/// </summary>
public sealed class CalendarEndpointTests
{
    private const string Prefix = "/acta/api/v1/calendars";
    private const string Confirm = "X-Acta-Control";

    private static Task<(WebApplication App, HttpClient Client)> StartWithControlsAsync(FakeOperations fakes) =>
        TestDashboardHost.StartAuthenticatedAsync("ops-lead", options => options.EnableControls = true, fakes);

    // Unconfirmed, the JSON goes as text/plain, the body a page on another origin can post without a CORS preflight.
    private static HttpRequestMessage Post(string path, object body, bool confirm = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Prefix + path) { Content = JsonContent.Create(body, body.GetType()) };
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        else
        {
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        }

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
            $"{Prefix}?jobNamespace=billing&role=availability&nameContains=hours&pageSize=5&includeTotal=true",
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
        Assert.Equal("published", family.RootElement.GetProperty("shownStatus").GetString());
        Assert.Equal(2, family.RootElement.GetProperty("latestVersion").GetInt32());
        Assert.DoesNotContain(family.RootElement.EnumerateObject(), member => member.Name is "versions" or "dependents");
        Assert.Equal("""{"kind":"weekly-availability"}""", family.RootElement.GetProperty("definition").GetRawText());
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
    public async Task The_screen_composes_the_head_with_the_first_pages_of_versions_and_dependents_or_answers_404()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var shown = await client.GetAsync($"{Prefix}/billing/office-hours/detail?version=2", ct);
        var missing = await client.GetAsync($"{Prefix}/billing/missing/detail", ct);
        var malformed = await client.GetAsync($"{Prefix}/bad!/office-hours/detail", ct);

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        using var detail = JsonDocument.Parse(await shown.Content.ReadAsStringAsync(ct));
        Assert.Equal(2, detail.RootElement.GetProperty("calendar").GetProperty("shownVersion").GetInt32());
        var versions = detail.RootElement.GetProperty("versions");
        Assert.Equal([2, 1], versions.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));
        Assert.Equal(2, versions.GetProperty("totalCount").GetInt32());
        var dependents = detail.RootElement.GetProperty("dependents");
        Assert.Equal("nightly", dependents.GetProperty("items")[0].GetProperty("scheduleName").GetString());
        Assert.Equal(1, dependents.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("Calendar not found.", await missing.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(
            [(new CalendarLookup("billing", "office-hours"), (int?)2), (new CalendarLookup("billing", "missing"), null)],
            fakes.Calendars.Reads
        );
        Assert.Equal(
            new ListCalendarVersionsQuery(new CalendarLookup("billing", "office-hours"), IncludeTotal: true),
            Assert.Single(fakes.Calendars.VersionQueries)
        );
        Assert.Equal(
            new ListCalendarDependentsQuery(new CalendarLookup("billing", "office-hours"), IncludeTotal: true),
            Assert.Single(fakes.Calendars.DependentQueries)
        );
    }

    [Fact]
    public async Task The_versions_route_answers_404_for_an_unknown_family()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"{Prefix}/billing/missing/versions?pageSize=7", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Calendar not found.", await response.Content.ReadAsStringAsync(ct));
        // Only the existence probe reads; the page itself is never listed.
        Assert.Equal(1, Assert.Single(fakes.Calendars.VersionQueries).PageSize);
    }

    [Fact]
    public async Task The_versions_route_answers_the_page_and_hands_its_paging_to_the_facade()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var paged = await client.GetAsync($"{Prefix}/billing/office-hours/versions?pageSize=7&cursor=opaque-cursor_42", ct);
        var counted = await client.GetAsync($"{Prefix}/sys/holidays/versions?includeTotal=true", ct);

        Assert.Equal(HttpStatusCode.OK, paged.StatusCode);
        Assert.Equal(
            new ListCalendarVersionsQuery(new CalendarLookup("billing", "office-hours"), 7, "opaque-cursor_42", false),
            fakes.Calendars.VersionQueries[1]
        );
        Assert.Equal(HttpStatusCode.OK, counted.StatusCode);
        using var page = JsonDocument.Parse(await counted.Content.ReadAsStringAsync(ct));
        var root = page.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nextCursor").ValueKind);
        Assert.False(root.GetProperty("hasMore").GetBoolean());
        Assert.Equal(50, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, root.GetProperty("totalCount").GetInt64());
        var newest = root.GetProperty("items")[0];
        Assert.Equal(2, newest.GetProperty("version").GetInt32());
        Assert.Equal("draft", newest.GetProperty("status").GetString());
        Assert.Equal("hash-2", newest.GetProperty("contentHash").GetString());
        Assert.Equal(JsonValueKind.Null, newest.GetProperty("publishedAtUtc").ValueKind);
    }

    [Fact]
    public async Task The_versions_route_answers_a_malformed_page_size_with_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var response = await client.GetAsync($"{Prefix}/billing/office-hours/versions?pageSize=abc", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Calendars.VersionQueries);
    }

    [Fact]
    public async Task The_dependents_route_answers_404_for_an_unknown_family_and_lists_nothing()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync($"{Prefix}/billing/missing/dependents", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Calendar not found.", await response.Content.ReadAsStringAsync(ct));
        Assert.Empty(fakes.Calendars.DependentQueries);
    }

    [Fact]
    public async Task The_dependents_route_answers_the_page_and_hands_its_paging_to_the_facade()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var paged = await client.GetAsync($"{Prefix}/billing/office-hours/dependents?pageSize=7&cursor=opaque-cursor_42", ct);
        var counted = await client.GetAsync($"{Prefix}/sys/holidays/dependents?includeTotal=true", ct);

        Assert.Equal(HttpStatusCode.OK, paged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, counted.StatusCode);
        Assert.Equal(
            [
                new ListCalendarDependentsQuery(new CalendarLookup("billing", "office-hours"), 7, "opaque-cursor_42", false),
                new ListCalendarDependentsQuery(new CalendarLookup("sys", "holidays"), null, null, true),
            ],
            fakes.Calendars.DependentQueries
        );
        using var page = JsonDocument.Parse(await counted.Content.ReadAsStringAsync(ct));
        var root = page.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nextCursor").ValueKind);
        Assert.False(root.GetProperty("hasMore").GetBoolean());
        Assert.Equal(1, root.GetProperty("totalCount").GetInt64());
        var dependent = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(
            ("billing", "send-invoice", "nightly", "availability", "active", "permanent"),
            (
                dependent.GetProperty("jobNamespace").GetString(),
                dependent.GetProperty("jobName").GetString(),
                dependent.GetProperty("scheduleName").GetString(),
                dependent.GetProperty("binding").GetString(),
                dependent.GetProperty("status").GetString(),
                dependent.GetProperty("scheduledDelivery").GetString()
            )
        );
    }

    [Fact]
    public async Task The_dependents_route_answers_a_malformed_page_size_with_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var response = await client.GetAsync(
            $"{Prefix}/billing/office-hours/dependents?pageSize=abc",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Calendars.DependentQueries);
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
            Post(
                "/billing/rejected/drafts",
                new
                {
                    role = "availability",
                    definition = new { kind = "weekly-availability" },
                    expectedLatestVersion = 0,
                }
            ),
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
        Assert.Contains("\"action\":\"version-conflict\"", body);
        Assert.Contains("\"version\":1", body);
    }

    [Theory]
    [InlineData("office-hours", HttpStatusCode.OK, "applied")]
    [InlineData("rejected", HttpStatusCode.Conflict, "rejected")]
    [InlineData("missing", HttpStatusCode.NotFound, "not-found")]
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
}

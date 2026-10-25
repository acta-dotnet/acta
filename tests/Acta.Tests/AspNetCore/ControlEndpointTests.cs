using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Acta.Tests.AspNetCore.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// Control endpoint behavior over the in-process host: verb dispatch to <see cref="IJobs"/>,
/// outcome-to-status mapping, the confirmation header guard, body validation, and the opt-in
/// default. The fake applies for FoundJobRef, reports not-found for MissingJobRef, and rejected
/// for RejectedJobRef.
/// </summary>
public sealed class ControlEndpointTests
{
    private const string Confirm = "X-Acta-Control";

    private static readonly string Found = FakeJobs.FoundJobRef.ToString();
    private static readonly string Missing = FakeJobs.MissingJobRef.ToString();
    private static readonly string Rejected = FakeJobs.RejectedJobRef.ToString();

    private static Task<(WebApplication App, HttpClient Client)> StartWithControlsAsync(
        FakeOperations? fakes = null,
        Action<ActaDashboardOptions>? configure = null
    ) =>
        TestDashboardHost.StartAsync(
            options =>
            {
                options.EnableControls = true;
                configure?.Invoke(options);
            },
            fakes: fakes
        );

    private static HttpRequestMessage Post(string path, string? reason = "because", bool confirm = true, string? rawBody = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        request.Content = rawBody is not null
            ? new StringContent(rawBody, Encoding.UTF8, "application/json")
            : JsonContent.Create(new { reasonMessage = reason });
        return request;
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("restart")]
    [InlineData("cancel")]
    public async Task Each_verb_dispatches_to_jobs_and_applies(string verb)
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/api/v1/jobs/{Found}/{verb}"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("\"action\":\"applied\"", body);
        var call = Assert.Single(fakes.Jobs.ControlCalls);
        Assert.Equal((verb, FakeJobs.FoundJobRef, "because", (string?)null, (int?)null), call);
    }

    [Fact]
    public async Task Cancel_records_the_authenticated_principal_as_actor()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAuthenticatedAsync(
            "test-operator",
            options => options.EnableControls = true,
            fakes
        );
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/api/v1/jobs/{Found}/cancel"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(fakes.Jobs.ControlCalls);
        Assert.Equal("test-operator", call.Actor);
    }

    [Fact]
    public async Task Rejected_maps_to_409_with_blocking_status()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/api/v1/jobs/{Rejected}/pause"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("\"action\":\"rejected\"", body);
        Assert.Contains("\"status\":\"succeeded\"", body);
    }

    [Fact]
    public async Task A_rejected_restart_answers_409_with_the_blocking_status()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/api/v1/jobs/{Rejected}/restart"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("\"action\":\"rejected\",\"status\":\"succeeded\"", body);
    }

    [Fact]
    public async Task Unknown_job_maps_to_404()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/api/v1/jobs/{Missing}/cancel"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("\"action\":\"notFound\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_confirmation_header_is_400_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            Post($"/acta/api/v1/jobs/{Found}/pause", confirm: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.ControlCalls);
    }

    [Fact]
    public async Task Confirmation_header_is_optional_when_disabled_in_options()
    {
        var (app, client) = await StartWithControlsAsync(configure: options => options.RequireControlConfirmationHeader = false);
        await using var _ = app;

        var response = await client.SendAsync(
            Post($"/acta/api/v1/jobs/{Found}/pause", confirm: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The header's name is the host's to choose, and its value is always true, which is what the dashboard sends.
    [Theory]
    [InlineData("true", HttpStatusCode.OK)]
    [InlineData("yes", HttpStatusCode.BadRequest)]
    public async Task A_renamed_confirmation_header_still_carries_true(string value, HttpStatusCode expected)
    {
        var (app, client) = await StartWithControlsAsync(configure: options => options.ControlConfirmationHeaderName = "X-Ops-Confirm");
        await using var _ = app;

        var request = Post($"/acta/api/v1/jobs/{Found}/pause", confirm: false);
        request.Headers.Add("X-Ops-Confirm", value);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
        {
            Assert.Contains("X-Ops-Confirm: true", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Controls_are_disabled_by_default_and_never_reach_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/api/v1/jobs/{Found}/pause"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fakes.Jobs.ControlCalls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12345")]
    [InlineData("job_nope")]
    [InlineData("job_2n1t201rmv87aae5j4csam8000")]
    [InlineData("job_01a14f4e-954e-7bf6-8ac6-09f3fcad8e3d")]
    [InlineData("01a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("JOB_01a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("job_01a14f4e954e7bf68ac609f3fcad8e3g")]
    public async Task Malformed_job_ref_is_400(string jobRef)
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/api/v1/jobs/{jobRef}/pause"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // A ref the API cannot parse is caller input, not a miss: 404 means an addressable ref that
        // names no row, which is what lets the 404 carry the family envelope.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("jobRef is not a valid job ref.", body);
    }

    [Theory]
    [InlineData("bad!/send-invoice/only")]
    [InlineData("billing/send%20invoice/only")]
    [InlineData("billing/send-invoice/bad!")]
    public async Task Malformed_schedule_segment_is_400(string schedule)
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/api/v1/schedules/{schedule}/trigger"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Schedules.TriggerCalls);
    }

    [Fact]
    public async Task Oversized_reason_is_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            Post($"/acta/api/v1/jobs/{Found}/pause", reason: new string('x', 600)),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.ControlCalls);
    }

    [Fact]
    public async Task Reason_is_trimmed_and_empty_becomes_null()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        await client.SendAsync(Post($"/acta/api/v1/jobs/{Found}/pause", reason: "  spaced out  "), ct);
        await client.SendAsync(Post($"/acta/api/v1/jobs/{Found}/resume", reason: "   "), ct);

        Assert.Equal("spaced out", fakes.Jobs.ControlCalls[0].Reason);
        Assert.Null(fakes.Jobs.ControlCalls[1].Reason);
    }

    [Fact]
    public async Task Body_is_optional_and_non_json_bodies_are_rejected()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var bare = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{Found}/pause");
        bare.Headers.Add(Confirm, "true");
        var noBody = await client.SendAsync(bare, ct);

        var text = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{Found}/pause");
        text.Headers.Add(Confirm, "true");
        text.Content = new StringContent("reason=oops", Encoding.UTF8, "application/x-www-form-urlencoded");
        var formBody = await client.SendAsync(text, ct);

        var garbage = await client.SendAsync(Post($"/acta/api/v1/jobs/{Found}/pause", rawBody: "{not json"), ct);

        Assert.Equal(HttpStatusCode.OK, noBody.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, formBody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        var call = Assert.Single(fakes.Jobs.ControlCalls);
        Assert.Null(call.Reason);
    }

    [Fact]
    public async Task Undeclared_body_is_415_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        // Neither Content-Length nor Content-Type, but a real reasonMessage in the stream. Read as "no
        // body" the verb still applied and the operator's audit text vanished; the answer is the 415
        // this route already declares for a body it cannot parse.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{Found}/pause")
        {
            Content = TestDashboardHost.UndeclaredContent("{\"reasonMessage\":\"draining the queue\"}"),
        };
        request.Headers.Add(Confirm, "true");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(fakes.Jobs.ControlCalls);
    }

    private static HttpRequestMessage PostReschedule(
        DateTime? nextRunAtUtc,
        string? reason = "because",
        bool confirm = true,
        string? rawBody = null
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{Found}/reschedule");
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        request.Content = rawBody is not null
            ? new StringContent(rawBody, Encoding.UTF8, "application/json")
            : JsonContent.Create(new { nextRunAtUtc, reasonMessage = reason });
        return request;
    }

    [Fact]
    public async Task Reschedule_applies_and_captures_the_requested_instant()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var nextRunAtUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        var response = await client.SendAsync(PostReschedule(nextRunAtUtc), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        var call = Assert.Single(fakes.Jobs.RescheduleCalls);
        Assert.Equal((FakeJobs.FoundJobRef, nextRunAtUtc, "because", (string?)null, (int?)null), call);
    }

    [Fact]
    public async Task Reschedule_missing_next_run_instant_is_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            PostReschedule(null, rawBody: """{"reasonMessage":"because"}"""),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.RescheduleCalls);
    }

    [Fact]
    public async Task Reschedule_default_next_run_instant_is_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostReschedule(DateTime.MinValue), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.RescheduleCalls);
    }

    [Fact]
    public async Task Reschedule_oversized_reason_is_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            PostReschedule(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc), reason: new string('x', 600)),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.RescheduleCalls);
    }

    [Fact]
    public async Task Reschedule_missing_confirmation_header_is_400_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            PostReschedule(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc), confirm: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.RescheduleCalls);
    }

    private static HttpRequestMessage PostReprioritize(
        object? priority,
        string? reason = "because",
        bool confirm = true,
        string? rawBody = null
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{Found}/reprioritize");
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        request.Content = rawBody is not null
            ? new StringContent(rawBody, Encoding.UTF8, "application/json")
            : JsonContent.Create(new { priority, reasonMessage = reason });
        return request;
    }

    [Fact]
    public async Task Reprioritize_applies_and_captures_the_requested_priority()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostReprioritize("high"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        var call = Assert.Single(fakes.Jobs.ReprioritizeCalls);
        Assert.Equal((FakeJobs.FoundJobRef, JobPriorityCode.High, "because", (string?)null, (int?)null), call);
    }

    [Fact]
    public async Task Reprioritize_unknown_priority_name_is_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostReprioritize("nope"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.ReprioritizeCalls);
    }

    [Fact]
    public async Task Reprioritize_oversized_reason_is_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            PostReprioritize("high", reason: new string('x', 600)),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.ReprioritizeCalls);
    }

    [Fact]
    public async Task Reprioritize_missing_confirmation_header_is_400_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostReprioritize("high", confirm: false), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.ReprioritizeCalls);
    }

    private static HttpRequestMessage PostTrigger(
        string scheduleName = "only",
        string? reason = "because",
        bool confirm = true,
        string? rawBody = null
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/schedules/billing/send-invoice/{scheduleName}/trigger");
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        request.Content = rawBody is not null
            ? new StringContent(rawBody, Encoding.UTF8, "application/json")
            : JsonContent.Create(new { reasonMessage = reason });
        return request;
    }

    [Fact]
    public async Task Trigger_applies_and_captures_the_request()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostTrigger(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        var call = Assert.Single(fakes.Schedules.TriggerCalls);
        Assert.Equal(("only", "because", (string?)null), call);
    }

    [Fact]
    public async Task Trigger_unknown_schedule_is_404()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostTrigger(scheduleName: "missing"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fakes.Schedules.TriggerCalls);
    }

    [Fact]
    public async Task Trigger_rejected_target_is_409()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostTrigger(scheduleName: "rejected"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(fakes.Schedules.TriggerCalls);
    }

    [Fact]
    public async Task Trigger_missing_confirmation_header_is_400_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostTrigger(confirm: false), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Schedules.TriggerCalls);
    }

    private static HttpRequestMessage PostOverrides(
        string scheduleName = "only",
        int? version = 1,
        string? expression = "*/5 * * * *",
        string? timeZoneId = null,
        string? reason = "because",
        bool confirm = true,
        string? rawBody = null
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/schedules/billing/send-invoice/{scheduleName}/overrides");
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        request.Content = rawBody is not null
            ? new StringContent(rawBody, Encoding.UTF8, "application/json")
            : JsonContent.Create(
                new
                {
                    expectedVersion = version,
                    expression,
                    timeZoneId,
                    reasonMessage = reason,
                }
            );
        return request;
    }

    [Fact]
    public async Task Overrides_applies_and_captures_the_requested_change()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostOverrides(version: 3), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        var call = Assert.Single(fakes.Schedules.SetOverridesCalls);
        Assert.Equal(("only", 3, "*/5 * * * *", (string?)null, "because", (string?)null), call);
    }

    [Fact]
    public async Task Overrides_missing_version_is_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostOverrides(version: null), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Schedules.SetOverridesCalls);
    }

    [Fact]
    public async Task Overrides_invalid_expression_is_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostOverrides(expression: "bad-expr"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Schedules.SetOverridesCalls);
    }

    [Fact]
    public async Task Overrides_unknown_schedule_is_404()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostOverrides(scheduleName: "missing"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Overrides_stale_version_is_409_carrying_current_version()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostOverrides(version: 999), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("\"action\":\"versionConflict\"", body);
        Assert.Contains("\"version\":7", body);
    }

    [Fact]
    public async Task Overrides_missing_confirmation_header_is_400_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostOverrides(confirm: false), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Schedules.SetOverridesCalls);
    }

    [Fact]
    public async Task Purge_applies_with_an_empty_body()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            Post($"/acta/api/v1/jobs/{Found}/purge", rawBody: "{}"),
            TestContext.Current.CancellationToken
        );
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        var call = Assert.Single(fakes.Jobs.PurgeCalls);
        Assert.Equal((FakeJobs.FoundJobRef, (string?)null), call);
    }

    [Fact]
    public async Task Purge_missing_confirmation_header_is_400_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            Post($"/acta/api/v1/jobs/{Found}/purge", confirm: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.PurgeCalls);
    }

    [Fact]
    public async Task Batch_endpoint_is_not_mapped_when_controls_are_enabled()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(Post("/acta/api/v1/jobs/" + "batch"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static HttpRequestMessage AmendInput(object body, bool confirm = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/jobs/{Found}/input");
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static FakeOperations JobWithInput(JobPayload? stored) => new() { Jobs = { StoredInput = stored } };

    [Fact]
    public async Task Amend_binary_job_with_base64_preserves_the_custom_format_id()
    {
        var fakes = JobWithInput(JobPayload.FromBytes(JobPayloadFormat.Custom(200, "proto"), [9]));
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var bytes = new byte[] { 1, 2, 3, 4 };
        var response = await client.SendAsync(AmendInput(new { base64 = Convert.ToBase64String(bytes) }), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var amended = Assert.Single(fakes.Jobs.InputAmendCalls);
        Assert.Equal(200, amended.Format.Id);
        Assert.Equal(bytes, amended.Data.ToArray());

        var body = await (await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct)).Content.ReadAsStringAsync(ct);
        Assert.Contains("\"formatId\":200", body);
        Assert.Contains($"\"base64\":\"{Convert.ToBase64String(bytes)}\"", body);
    }

    [Fact]
    public async Task Amend_json_job_with_input_still_applies_as_json()
    {
        var fakes = JobWithInput(JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes("{\"a\":1}")));
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.SendAsync(AmendInput(new { input = new { b = 2 } }), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JobPayloadFormat.Json.Id, Assert.Single(fakes.Jobs.InputAmendCalls).Format.Id);

        var body = await (await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct)).Content.ReadAsStringAsync(ct);
        Assert.Contains("\"formatName\":\"json\"", body);
    }

    [Fact]
    public async Task Amend_binary_job_with_input_falls_back_to_json()
    {
        var fakes = JobWithInput(JobPayload.Bytes([1, 2]));

        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var response = await client.SendAsync(AmendInput(new { input = new { b = 2 } }), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JobPayloadFormat.Json.Id, Assert.Single(fakes.Jobs.InputAmendCalls).Format.Id);

        var body = await (await client.GetAsync($"/acta/api/v1/jobs/{Found}/detail", ct)).Content.ReadAsStringAsync(ct);
        Assert.Contains("\"formatName\":\"json\"", body);
    }

    [Fact]
    public async Task Amend_with_two_body_fields_is_400()
    {
        var fakes = JobWithInput(JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes("{}")));
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            AmendInput(new { input = new { a = 1 }, base64 = "AQ==" }),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.InputAmendCalls);
    }

    [Fact]
    public async Task Amend_invalid_base64_is_400()
    {
        var fakes = JobWithInput(JobPayload.FromBytes(JobPayloadFormat.Bytes, [1]));
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(AmendInput(new { base64 = "not valid base64!" }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.InputAmendCalls);
    }

    [Fact]
    public async Task Amend_no_input_job_is_409()
    {
        var fakes = JobWithInput(null);
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(AmendInput(new { input = new { a = 1 } }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(fakes.Jobs.InputAmendCalls);
    }

    private static HttpRequestMessage PostEnqueue(object body, bool confirm = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/acta/api/v1/jobs");
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        request.Content = JsonContent.Create(body);
        return request;
    }

    /// <summary>
    /// 201 is a claim that this request created the row, and Location is the one thing a client reads
    /// a 201 for. A deduplicated enqueue created nothing, so it answers 200 with the ref it matched.
    /// </summary>
    [Fact]
    public async Task Enqueue_answers_201_with_Location_on_insert_and_200_on_a_dedup_match()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        object body = new
        {
            jobNamespace = "billing",
            jobName = "send-invoice",
            deduplicationKey = "invoice-9",
        };

        var created = await client.SendAsync(PostEnqueue(body), ct);
        var matched = await client.SendAsync(PostEnqueue(body), ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var jobRef = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("jobRef").GetString();
        Assert.Equal($"/acta/api/v1/jobs/{jobRef}", created.Headers.Location?.ToString());

        Assert.Equal(HttpStatusCode.OK, matched.StatusCode);
        var second = JsonDocument.Parse(await matched.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.Equal(jobRef, second.GetProperty("jobRef").GetString());
        Assert.Equal("deduplicated", second.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Enqueue_with_base64_and_format_id_creates_and_reads_back_as_bytes()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var bytes = new byte[] { 5, 6, 7 };
        var response = await client.SendAsync(
            PostEnqueue(
                new
                {
                    jobNamespace = "billing",
                    jobName = "send-invoice",
                    base64 = Convert.ToBase64String(bytes),
                    formatId = 2,
                }
            ),
            ct
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var enqueued = Assert.Single(fakes.Jobs.EnqueueRequests);
        Assert.Equal(JobPayloadFormat.Bytes.Id, enqueued.Input.Format.Id);
        Assert.Equal(bytes, enqueued.Input.Data.ToArray());

        var jobRef = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("jobRef").GetString();
        var body = await (await client.GetAsync($"/acta/api/v1/jobs/{jobRef}/detail", ct)).Content.ReadAsStringAsync(ct);
        Assert.Contains($"\"base64\":\"{Convert.ToBase64String(bytes)}\"", body);
    }

    [Fact]
    public async Task Enqueue_base64_without_format_id_is_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            PostEnqueue(
                new
                {
                    jobNamespace = "billing",
                    jobName = "send-invoice",
                    base64 = Convert.ToBase64String(new byte[] { 1 }),
                }
            ),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.EnqueueRequests);
    }

    [Fact]
    public async Task Enqueue_format_id_without_base64_is_400()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            PostEnqueue(
                new
                {
                    jobNamespace = "billing",
                    jobName = "send-invoice",
                    formatId = 2,
                }
            ),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Jobs.EnqueueRequests);
    }

    private static HttpRequestMessage PostTenant(object body, bool confirm = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/acta/api/v1/tenants");
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        request.Content = JsonContent.Create(body);
        return request;
    }

    [Fact]
    public async Task Register_tenant_applies_and_returns_the_canonical_key()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            PostTenant(
                new
                {
                    tenantName = "Cust-001 ",
                    displayName = "Acme Corp",
                    description = "Acme",
                }
            ),
            TestContext.Current.CancellationToken
        );
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("\"tenantId\"", body);
        Assert.Contains("\"tenantName\":\"cust-001\"", body);
        var call = Assert.Single(fakes.Tenants.RegisterCalls);
        Assert.Equal(("cust-001", "Acme Corp", "Acme"), call);
    }

    [Fact]
    public async Task Tenant_register_requires_a_key()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostTenant(new { description = "no key" }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Tenants.RegisterCalls);
    }

    [Fact]
    public async Task Tenant_register_maps_an_invalid_key_to_400()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.SendAsync(PostTenant(new { tenantName = "bad key" }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Tenant_register_without_confirmation_is_400_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            PostTenant(new { tenantName = "cust-001" }, confirm: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Tenants.RegisterCalls);
    }

    [Fact]
    public async Task Tenant_register_is_disabled_by_default_and_never_reaches_jobs()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var response = await client.SendAsync(PostTenant(new { tenantName = "cust-001" }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fakes.Tenants.RegisterCalls);
    }

    [Fact]
    public async Task Standalone_api_mapping_opts_into_controls_and_defaults_off()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var fakes = new FakeOperations();
        builder.Services.AddSingleton<IJobs>(fakes.Jobs);
        builder.Services.AddSingleton<IActaOperations>(fakes);
        var app = builder.Build();
        app.MapActaApi("/ops/api", options => options.EnableControls = true);
        app.MapActaApi("/ops/readonly");
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var _ = app;
        var client = app.GetTestClient();
        var ct = TestContext.Current.CancellationToken;

        var enabled = await client.SendAsync(Post($"/ops/api/v1/jobs/{Found}/pause"), ct);
        var disabled = await client.SendAsync(Post($"/ops/readonly/v1/jobs/{Found}/pause"), ct);

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
    }

    [Fact]
    public async Task Definition_overrides_out_of_range_value_is_400()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Patch, "/acta/api/v1/definitions/billing/send-invoice")
        {
            Content = JsonContent.Create(new { expectedVersion = 1, overrides = new { maxAttempts = 0 } }),
        };
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("send-invoice", 1, HttpStatusCode.OK, "applied")]
    [InlineData("send-invoice", 999, HttpStatusCode.Conflict, "versionConflict")]
    [InlineData("missing", 1, HttpStatusCode.NotFound, "notFound")]
    public async Task Definition_retire_maps_the_verb_outcome(string jobName, int expectedVersion, HttpStatusCode status, string action)
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/definitions/billing/{jobName}/retire")
        {
            Content = JsonContent.Create(new { expectedVersion, reasonMessage = "handler dropped" }),
        };
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, ct);

        Assert.Equal(status, response.StatusCode);
        Assert.Contains($"\"action\":\"{action}\"", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("acknowledge")]
    [InlineData("resolve")]
    public async Task Alert_verb_dispatches_and_applies(string verb)
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var alertRef = AlertRef.New();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/alerts/{alertRef}/" + verb)
        {
            Content = JsonContent.Create(new { reasonMessage = "because" }),
        };
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        Assert.Contains($"\"alertRef\":\"{alertRef}\"", body);
        var calls = verb == "acknowledge" ? fakes.Alerts.AcknowledgeCalls : fakes.Alerts.ResolveCalls;
        var call = Assert.Single(calls);
        Assert.Equal((alertRef, "because", (string?)null), call);
    }

    [Theory]
    [InlineData("acknowledge")]
    [InlineData("resolve")]
    public async Task Alert_verb_unknown_alert_is_404(string verb)
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/alerts/{default(AlertRef)}/" + verb);
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fakes.Alerts.AcknowledgeCalls);
        Assert.Empty(fakes.Alerts.ResolveCalls);
    }

    [Theory]
    [InlineData("acknowledge")]
    [InlineData("resolve")]
    public async Task Alert_verb_with_no_body_is_a_presence_only_request(string verb)
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var alertRef = AlertRef.New();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/alerts/{alertRef}/" + verb);
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var calls = verb == "acknowledge" ? fakes.Alerts.AcknowledgeCalls : fakes.Alerts.ResolveCalls;
        var call = Assert.Single(calls);
        Assert.Equal((alertRef, (string?)null, (string?)null), call);
    }

    [Fact]
    public async Task Input_template_answers_the_type_its_format_name_and_the_skeleton()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.GetAsync(
            "/acta/api/v1/jobs/input-template?jobNamespace=billing&jobName=send-invoice",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"inputTypeName\":\"Billing.SendInvoice\"", body);
        Assert.Contains("\"inputFormatName\":\"json\"", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("""{"invoiceId":0,"note":null}""", json.RootElement.GetProperty("templateJson").GetString());
        Assert.DoesNotContain(json.RootElement.EnumerateObject(), property => property.Name == "inputFormat");
    }

    // A dashboard can point at a ledger whose job assemblies it never loaded; the form reads the 404 as no
    // template and keeps an empty editor.
    [Fact]
    public async Task Input_template_for_a_job_this_host_does_not_know_is_404()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.GetAsync(
            "/acta/api/v1/jobs/input-template?jobNamespace=billing&jobName=unknown",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    // A required query parameter that did not arrive is caller input, the same answer /jobs/by-key
    // gives its own two. This read never reports a miss: a name it has no assembly for is a 200 with
    // a null template.
    public async Task Input_template_without_a_job_name_is_400()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.GetAsync(
            "/acta/api/v1/jobs/input-template?jobNamespace=billing",
            TestContext.Current.CancellationToken
        );
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("jobNamespace and jobName are required.", body);
    }

    [Theory]
    [InlineData("acknowledge")]
    [InlineData("resolve")]
    public async Task Alert_verb_missing_confirmation_header_is_400_and_never_reaches_jobs(string verb)
    {
        var fakes = new FakeOperations();
        var (app, client) = await StartWithControlsAsync(fakes);
        await using var _ = app;

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/acta/api/v1/alerts/{AlertRef.New()}/" + verb),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fakes.Alerts.AcknowledgeCalls);
        Assert.Empty(fakes.Alerts.ResolveCalls);
    }
}

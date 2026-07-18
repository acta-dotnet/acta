using System.Net;
using System.Net.Http.Json;
using System.Text;
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

    private static readonly string Found = TestDashboardHost.FoundJobRef.ToString();
    private static readonly string Missing = TestDashboardHost.MissingJobRef.ToString();
    private static readonly string Rejected = TestDashboardHost.RejectedJobRef.ToString();

    private static Task<(WebApplication App, HttpClient Client)> StartWithControlsAsync(
        TestDashboardHost.FakeJobs? jobs = null,
        Action<ActaDashboardOptions>? configure = null
    ) =>
        TestDashboardHost.StartAsync(
            options =>
            {
                options.EnableControls = true;
                configure?.Invoke(options);
            },
            jobs: jobs
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
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/jobs/api/jobs/{Found}/{verb}"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("\"action\":\"applied\"", body);
        var call = Assert.Single(jobs.ControlCalls);
        Assert.Equal((verb, TestDashboardHost.FoundJobRef, "because", (string?)null), call);
    }

    [Fact]
    public async Task Cancel_records_the_authenticated_principal_as_actor_key()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAuthenticatedAsync(
            "test-operator",
            options => options.EnableControls = true,
            jobs
        );
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/jobs/api/jobs/{Found}/cancel"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(jobs.ControlCalls);
        Assert.Equal("test-operator", call.ActorKey);
    }

    [Fact]
    public async Task Rejected_maps_to_409_with_blocking_status()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/jobs/api/jobs/{Rejected}/pause"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("\"action\":\"rejected\"", body);
        Assert.Contains("\"status\":\"done\"", body);
    }

    [Fact]
    public async Task Unknown_job_maps_to_404()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/jobs/api/jobs/{Missing}/cancel"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("\"action\":\"notFound\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_confirmation_header_is_400_and_never_reaches_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(
            Post($"/acta/jobs/api/jobs/{Found}/pause", confirm: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.ControlCalls);
    }

    [Fact]
    public async Task Confirmation_header_is_optional_when_disabled_in_options()
    {
        var (app, client) = await StartWithControlsAsync(configure: options => options.RequireControlConfirmationHeader = false);
        await using var _ = app;

        var response = await client.SendAsync(
            Post($"/acta/jobs/api/jobs/{Found}/pause", confirm: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Controls_are_disabled_by_default_and_never_reach_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAsync(jobs: jobs);
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/jobs/api/jobs/{Found}/pause"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(jobs.ControlCalls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12345")]
    [InlineData("job_nope")]
    [InlineData("job_zn1t201rmv87aae5j4csam8000")]
    public async Task Malformed_job_ref_is_404(string jobRef)
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/jobs/api/jobs/{jobRef}/pause"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_reason_is_400()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(
            Post($"/acta/jobs/api/jobs/{Found}/pause", reason: new string('x', 600)),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.ControlCalls);
    }

    [Fact]
    public async Task Reason_is_trimmed_and_empty_becomes_null()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        await client.SendAsync(Post($"/acta/jobs/api/jobs/{Found}/pause", reason: "  spaced out  "), ct);
        await client.SendAsync(Post($"/acta/jobs/api/jobs/{Found}/resume", reason: "   "), ct);

        Assert.Equal("spaced out", jobs.ControlCalls[0].Reason);
        Assert.Null(jobs.ControlCalls[1].Reason);
    }

    [Fact]
    public async Task Body_is_optional_and_non_json_bodies_are_rejected()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var bare = new HttpRequestMessage(HttpMethod.Post, $"/acta/jobs/api/jobs/{Found}/pause");
        bare.Headers.Add(Confirm, "true");
        var noBody = await client.SendAsync(bare, ct);

        var text = new HttpRequestMessage(HttpMethod.Post, $"/acta/jobs/api/jobs/{Found}/pause");
        text.Headers.Add(Confirm, "true");
        text.Content = new StringContent("reason=oops", Encoding.UTF8, "application/x-www-form-urlencoded");
        var formBody = await client.SendAsync(text, ct);

        var garbage = await client.SendAsync(Post($"/acta/jobs/api/jobs/{Found}/pause", rawBody: "{not json"), ct);

        Assert.Equal(HttpStatusCode.OK, noBody.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, formBody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        var call = Assert.Single(jobs.ControlCalls);
        Assert.Null(call.Reason);
    }

    private static HttpRequestMessage PostReschedule(
        DateTime? nextRunAtUtc,
        string? reason = "because",
        bool confirm = true,
        string? rawBody = null
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/jobs/api/jobs/{Found}/reschedule");
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
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;
        var nextRunAtUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        var response = await client.SendAsync(PostReschedule(nextRunAtUtc), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        var call = Assert.Single(jobs.RescheduleCalls);
        Assert.Equal((TestDashboardHost.FoundJobRef, nextRunAtUtc, "because", (string?)null), call);
    }

    [Fact]
    public async Task Reschedule_missing_next_run_instant_is_400()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(
            PostReschedule(null, rawBody: """{"reasonMessage":"because"}"""),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.RescheduleCalls);
    }

    [Fact]
    public async Task Reschedule_default_next_run_instant_is_400()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostReschedule(DateTime.MinValue), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.RescheduleCalls);
    }

    [Fact]
    public async Task Reschedule_oversized_reason_is_400()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(
            PostReschedule(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc), reason: new string('x', 600)),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.RescheduleCalls);
    }

    [Fact]
    public async Task Reschedule_missing_confirmation_header_is_400_and_never_reaches_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(
            PostReschedule(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc), confirm: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.RescheduleCalls);
    }

    private static HttpRequestMessage PostReprioritize(
        object? priority,
        string? reason = "because",
        bool confirm = true,
        string? rawBody = null
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/acta/jobs/api/jobs/{Found}/reprioritize");
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
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostReprioritize("high"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        var call = Assert.Single(jobs.ReprioritizeCalls);
        Assert.Equal((TestDashboardHost.FoundJobRef, JobPriorityCode.High, "because", (string?)null), call);
    }

    [Fact]
    public async Task Reprioritize_unknown_priority_name_is_400()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostReprioritize("nope"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.ReprioritizeCalls);
    }

    [Fact]
    public async Task Reprioritize_oversized_reason_is_400()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(
            PostReprioritize("high", reason: new string('x', 600)),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.ReprioritizeCalls);
    }

    [Fact]
    public async Task Reprioritize_missing_confirmation_header_is_400_and_never_reaches_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostReprioritize("high", confirm: false), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.ReprioritizeCalls);
    }

    private static HttpRequestMessage PostTrigger(
        string scheduleName = "only",
        string? reason = "because",
        bool confirm = true,
        string? rawBody = null
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/acta/jobs/api/schedules/trigger");
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        request.Content = rawBody is not null
            ? new StringContent(rawBody, Encoding.UTF8, "application/json")
            : JsonContent.Create(
                new
                {
                    jobNamespace = "billing",
                    jobName = "send-invoice",
                    scheduleName,
                    note = reason,
                }
            );
        return request;
    }

    [Fact]
    public async Task Trigger_applies_and_captures_the_request()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostTrigger(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        var call = Assert.Single(jobs.TriggerCalls);
        Assert.Equal(("only", "because", (string?)null), call);
    }

    [Fact]
    public async Task Trigger_unknown_schedule_is_404()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostTrigger(scheduleName: "missing"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(jobs.TriggerCalls);
    }

    [Fact]
    public async Task Trigger_rejected_target_is_409()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostTrigger(scheduleName: "rejected"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(jobs.TriggerCalls);
    }

    [Fact]
    public async Task Trigger_missing_confirmation_header_is_400_and_never_reaches_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostTrigger(confirm: false), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.TriggerCalls);
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
        var request = new HttpRequestMessage(HttpMethod.Post, "/acta/jobs/api/schedules/overrides");
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        request.Content = rawBody is not null
            ? new StringContent(rawBody, Encoding.UTF8, "application/json")
            : JsonContent.Create(
                new
                {
                    jobNamespace = "billing",
                    jobName = "send-invoice",
                    scheduleName,
                    version,
                    expression,
                    timeZoneId,
                    note = reason,
                }
            );
        return request;
    }

    [Fact]
    public async Task Overrides_applies_and_captures_the_requested_change()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostOverrides(version: 3), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        var call = Assert.Single(jobs.SetOverridesCalls);
        Assert.Equal(("only", 3, "*/5 * * * *", (string?)null, "because", (string?)null), call);
    }

    [Fact]
    public async Task Overrides_missing_version_is_400()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostOverrides(version: null), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.SetOverridesCalls);
    }

    [Fact]
    public async Task Overrides_invalid_expression_is_400()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostOverrides(expression: "bad-expr"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.SetOverridesCalls);
    }

    [Fact]
    public async Task Overrides_unknown_schedule_is_404()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostOverrides(scheduleName: "missing"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Overrides_stale_version_is_409_carrying_current_version()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostOverrides(scheduleName: "rejected"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("\"version\":7", body);
    }

    [Fact]
    public async Task Overrides_missing_confirmation_header_is_400_and_never_reaches_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostOverrides(confirm: false), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.SetOverridesCalls);
    }

    [Fact]
    public async Task Purge_applies_and_ignores_the_reason()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(Post($"/acta/jobs/api/jobs/{Found}/purge"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        var call = Assert.Single(jobs.PurgeCalls);
        Assert.Equal((TestDashboardHost.FoundJobRef, (string?)null), call);
    }

    [Fact]
    public async Task Purge_missing_confirmation_header_is_400_and_never_reaches_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(
            Post($"/acta/jobs/api/jobs/{Found}/purge", confirm: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.PurgeCalls);
    }

    [Fact]
    public async Task Batch_endpoint_is_not_mapped_when_controls_are_enabled()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(Post("/acta/jobs/api/jobs/" + "batch"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static HttpRequestMessage PostTenant(object body, bool confirm = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/acta/jobs/api/tenants");
        if (confirm)
        {
            request.Headers.Add(Confirm, "true");
        }
        request.Content = JsonContent.Create(body);
        return request;
    }

    [Fact]
    public async Task Register_tenant_applies_and_returns_the_assigned_id()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(
            PostTenant(
                new
                {
                    tenantKey = "Cust-001 ",
                    displayName = "Acme Corp",
                    description = "Acme",
                }
            ),
            TestContext.Current.CancellationToken
        );
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("\"tenantId\":7", body);
        Assert.Contains("\"tenantKey\":\"cust-001\"", body);
        var call = Assert.Single(jobs.TenantCalls);
        Assert.Equal(("cust-001", "Acme Corp", "Acme", TenantStatusCode.Active), call);
    }

    [Fact]
    public async Task Suspend_is_a_register_with_suspended_status()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(
            PostTenant(new { tenantKey = "cust-001", status = "suspended" }),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TenantStatusCode.Suspended, Assert.Single(jobs.TenantCalls).Status);
    }

    [Fact]
    public async Task Tenant_register_requires_a_key()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostTenant(new { description = "no key" }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.TenantCalls);
    }

    [Fact]
    public async Task Tenant_register_maps_an_invalid_key_to_400()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var response = await client.SendAsync(PostTenant(new { tenantKey = "bad key" }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Tenant_register_without_confirmation_is_400_and_never_reaches_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(
            PostTenant(new { tenantKey = "cust-001" }, confirm: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.TenantCalls);
    }

    [Fact]
    public async Task Tenant_register_is_disabled_by_default_and_never_reaches_jobs()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAsync(jobs: jobs);
        await using var _ = app;

        var response = await client.SendAsync(PostTenant(new { tenantKey = "cust-001" }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(jobs.TenantCalls);
    }

    [Fact]
    public async Task Standalone_api_mapping_opts_into_controls_and_defaults_off()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IJobs>(new TestDashboardHost.FakeJobs());
        var app = builder.Build();
        app.MapActaApi("/ops/api", options => options.EnableControls = true);
        app.MapActaApi("/ops/readonly");
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var _ = app;
        var client = app.GetTestClient();
        var ct = TestContext.Current.CancellationToken;

        var enabled = await client.SendAsync(Post($"/ops/api/jobs/{Found}/pause"), ct);
        var disabled = await client.SendAsync(Post($"/ops/readonly/jobs/{Found}/pause"), ct);

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
    }

    [Fact]
    public async Task Definition_overrides_out_of_range_value_is_400()
    {
        var (app, client) = await StartWithControlsAsync();
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Patch, "/acta/jobs/api/definitions/1")
        {
            Content = JsonContent.Create(new { version = 1, overrides = new { maxAttempts = 0 } }),
        };
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("acknowledge")]
    [InlineData("resolve")]
    public async Task Alert_verb_dispatches_and_applies(string verb)
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Post, "/acta/jobs/api/alerts/7/" + verb)
        {
            Content = JsonContent.Create(new { note = "because" }),
        };
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"action\":\"applied\"", body);
        Assert.Contains("\"alertId\":7", body);
        var calls = verb == "acknowledge" ? jobs.AcknowledgeCalls : jobs.ResolveCalls;
        var call = Assert.Single(calls);
        Assert.Equal((7L, "because", (string?)null), call);
    }

    [Theory]
    [InlineData("acknowledge")]
    [InlineData("resolve")]
    public async Task Alert_verb_unknown_alert_is_404(string verb)
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Post, "/acta/jobs/api/alerts/0/" + verb);
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(jobs.AcknowledgeCalls);
        Assert.Empty(jobs.ResolveCalls);
    }

    [Theory]
    [InlineData("acknowledge")]
    [InlineData("resolve")]
    public async Task Alert_verb_with_no_body_is_a_presence_only_request(string verb)
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var request = new HttpRequestMessage(HttpMethod.Post, "/acta/jobs/api/alerts/7/" + verb);
        request.Headers.Add(Confirm, "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var calls = verb == "acknowledge" ? jobs.AcknowledgeCalls : jobs.ResolveCalls;
        var call = Assert.Single(calls);
        Assert.Equal((7L, (string?)null, (string?)null), call);
    }

    [Theory]
    [InlineData("acknowledge")]
    [InlineData("resolve")]
    public async Task Alert_verb_missing_confirmation_header_is_400_and_never_reaches_jobs(string verb)
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await StartWithControlsAsync(jobs);
        await using var _ = app;

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/acta/jobs/api/alerts/7/" + verb),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(jobs.AcknowledgeCalls);
        Assert.Empty(jobs.ResolveCalls);
    }
}

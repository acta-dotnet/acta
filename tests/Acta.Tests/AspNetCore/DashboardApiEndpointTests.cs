using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// API endpoint behavior over the in-process host: success shape, caching headers, predictable
/// 400s for malformed input and cursors, 404 for missing jobs, and no payload fields in JSON.
/// </summary>
public sealed class DashboardApiEndpointTests
{
    private static readonly string Found = TestDashboardHost.FoundJobRef.ToString();
    private static readonly string Missing = TestDashboardHost.MissingJobRef.ToString();

    [Fact]
    public async Task Jobs_endpoint_returns_page_with_no_store_and_no_payload_fields()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/jobs/api/jobs", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains($"\"jobRef\":\"{Found}\"", body);
        Assert.DoesNotContain("\"jobId\"", body);
        Assert.Contains("\"status\":\"ready\"", body);
        Assert.DoesNotContain("\"input\"", body);
        Assert.DoesNotContain("\"payload\"", body);
        Assert.DoesNotContain("input_text", body);
    }

    [Fact]
    public async Task Invalid_cursor_maps_to_400()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/jobs/api/jobs?cursor=bogus", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unhandled_api_exceptions_map_to_generic_503_without_leaking_message()
    {
        var jobs = new TestDashboardHost.FakeJobs
        {
            ListJobsException = new InvalidOperationException("Server=prod-db;Password=secret;connect failed"),
        };
        var (app, client) = await TestDashboardHost.StartAsync(
            configureBuilder: builder =>
                builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Clear()),
            jobs: jobs
        );
        await using var _ = app;

        var response = await client.GetAsync("/acta/jobs/api/jobs", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("The Acta API is temporarily unavailable.", body);
        Assert.DoesNotContain("prod-db", body);
        Assert.DoesNotContain("secret", body);
    }

    [Fact]
    public async Task Database_failures_map_to_503_with_sanitized_unreachable_reason()
    {
        var jobs = new TestDashboardHost.FakeJobs
        {
            ListJobsException = new TimeoutException("Server=prod-db;Password=secret;connect failed"),
        };
        var (app, client) = await TestDashboardHost.StartAsync(jobs: jobs);
        await using var _ = app;

        var response = await client.GetAsync("/acta/jobs/api/jobs", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("database is unreachable", body);
        Assert.DoesNotContain("prod-db", body);
        Assert.DoesNotContain("secret", body);
    }

    [Fact]
    public async Task Invalid_enum_and_integer_values_map_to_400()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var badEnum = await client.GetAsync("/acta/jobs/api/jobs?status=nope", TestContext.Current.CancellationToken);
        var badInt = await client.GetAsync("/acta/jobs/api/jobs?pageSize=abc", TestContext.Current.CancellationToken);
        var badBool = await client.GetAsync("/acta/jobs/api/alerts?unresolvedOnly=maybe", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, badEnum.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badInt.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badBool.StatusCode);
    }

    [Fact]
    public async Task Events_endpoint_binds_event_code_job_id_and_tenant_id_filters()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAsync(jobs: jobs);
        await using var _ = app;

        var ok = await client.GetAsync(
            "/acta/jobs/api/events?eventCode=namespace.metadata-changed&jobId=7&tenantId=3&workerId=9",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.NotNull(jobs.LastEventsQuery);
        Assert.Equal(Acta.JobEventCode.NamespaceMetadataChanged, jobs.LastEventsQuery!.EventCode);
        Assert.Equal(7L, jobs.LastEventsQuery.JobId);
        Assert.Equal(3, jobs.LastEventsQuery.TenantId);
        Assert.Equal(9, jobs.LastEventsQuery.WorkerId);

        // A divergent wire code (member JobDefinitionPolicyChanged) binds via its [Code] string, not the member name.
        var divergent = await client.GetAsync(
            "/acta/jobs/api/events?eventCode=definition.policy-changed",
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, divergent.StatusCode);
        Assert.Equal(Acta.JobEventCode.JobDefinitionPolicyChanged, jobs.LastEventsQuery!.EventCode);

        var badCode = await client.GetAsync("/acta/jobs/api/events?eventCode=nope", TestContext.Current.CancellationToken);
        var badJobId = await client.GetAsync("/acta/jobs/api/events?jobId=abc", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, badCode.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badJobId.StatusCode);
    }

    [Fact]
    public async Task Job_detail_returns_snapshot_or_404()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var known = await client.GetAsync($"/acta/jobs/api/jobs/{Found}", TestContext.Current.CancellationToken);
        var missing = await client.GetAsync($"/acta/jobs/api/jobs/{Missing}", TestContext.Current.CancellationToken);
        var malformed = await client.GetAsync("/acta/jobs/api/jobs/42", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    // Explain, lineage, and the per-panel input/result/checkpoint reads folded into GET /jobs/{ref}/detail
    // (JobDetailEndpointTests) and were removed per the pre-1.0 no-deprecated-code rule; the standalone
    // routes no longer exist. The snapshot, /detail, and events routes remain.
    [Fact]
    public async Task Folded_in_per_panel_read_routes_are_gone()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        foreach (var segment in new[] { "explain", "lineage", "input", "result", "checkpoints" })
        {
            var response = await client.GetAsync($"/acta/jobs/api/jobs/{Found}/{segment}", ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Numeric_id_lookup_is_404_when_disabled_by_default()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var byId = await client.GetAsync("/acta/jobs/api/jobs/id:42", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
    }

    [Fact]
    public async Task Numeric_id_lookup_resolves_when_enabled()
    {
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableNumericIdLookup = true);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var known = await client.GetAsync("/acta/jobs/api/jobs/id:42", ct);
        var missing = await client.GetAsync("/acta/jobs/api/jobs/id:99", ct);
        var bare = await client.GetAsync("/acta/jobs/api/jobs/42", ct);
        var events = await client.GetAsync("/acta/jobs/api/jobs/id:42/events", ct);

        var body = await known.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Contains($"\"jobRef\":\"{Found}\"", body);
        Assert.DoesNotContain("\"jobId\"", body);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
    }

    [Fact]
    public async Task All_list_endpoints_respond()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        foreach (var path in new[] { "jobs", $"jobs/{Found}/events", "definitions", "schedules", "workers", "alerts", "tenants" })
        {
            var response = await client.GetAsync($"/acta/jobs/api/{path}", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Worker_detail_returns_worker_or_404()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var known = await client.GetAsync("/acta/jobs/api/workers/42", ct);
        var missing = await client.GetAsync("/acta/jobs/api/workers/404", ct);
        var malformed = await client.GetAsync("/acta/jobs/api/workers/nope", ct);
        var body = await known.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal("no-store", known.Headers.CacheControl?.ToString());
        Assert.Contains("\"workerId\":42", body);
        Assert.Contains("\"jobNamespace\":\"billing\"", body);
        Assert.Contains("\"lastHeartbeatAtUtc\":", body);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    [Fact]
    public async Task Overview_returns_health_counters()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/jobs/api/overview", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"readyCount\":3", body);
        Assert.Contains("\"oldestReadyAgeSeconds\":120", body);
        Assert.Contains("\"dueSoonScheduleCount\":5", body);
    }

    [Fact]
    public async Task Overview_rejects_invalid_namespace()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/jobs/api/overview?jobNamespace=Not%20Kebab", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Guard_logs_argument_exceptions_at_warning_before_returning_400()
    {
        var provider = new RecordingLoggerProvider();
        var (app, client) = await TestDashboardHost.StartAsync(configureBuilder: b => b.Logging.AddProvider(provider));
        await using var _ = app;

        var response = await client.GetAsync("/acta/jobs/api/overview?jobNamespace=Not%20Kebab", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            provider.Entries,
            e => e.Category == "Acta.AspNetCore.Web" && e.Level == LogLevel.Warning && e.Message.Contains("Argument exception")
        );
    }

    [Fact]
    public async Task Client_abort_produces_no_error_log_and_no_response()
    {
        var provider = new RecordingLoggerProvider();
        var jobs = new TestDashboardHost.FakeJobs { ListJobsAwaitCancellation = true };
        var (app, client) = await TestDashboardHost.StartAsync(jobs: jobs, configureBuilder: b => b.Logging.AddProvider(provider));
        await using var _ = app;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var requestTask = client.GetAsync("/acta/jobs/api/jobs", cts.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);

        Assert.DoesNotContain(provider.Entries, e => e.Category == "Acta.AspNetCore.Web" && e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Aborted_overview_is_absorbed_at_the_api_boundary_without_an_error_log()
    {
        var provider = new RecordingLoggerProvider();
        var jobs = new TestDashboardHost.FakeJobs { OverviewAwaitCancellation = true };
        using var requestAborted = new CancellationTokenSource();
        var (app, client) = await TestDashboardHost.StartAsync(
            jobs: jobs,
            configureBuilder: b => b.Logging.AddProvider(provider),
            configureApp: app =>
                app.Use(
                    async (context, next) =>
                    {
                        context.RequestAborted = requestAborted.Token;
                        await next(context);
                    }
                )
        );
        await using var _ = app;

        var request = client.GetAsync("/acta/jobs/api/overview", TestContext.Current.CancellationToken);
        await jobs.OverviewStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        requestAborted.Cancel();
        var response = await request;

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, (int)response.StatusCode);
        Assert.DoesNotContain(provider.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Cancellation_unrelated_to_request_abort_remains_a_service_error()
    {
        var provider = new RecordingLoggerProvider();
        var jobs = new TestDashboardHost.FakeJobs { ListJobsException = new TaskCanceledException("provider timeout") };
        var (app, client) = await TestDashboardHost.StartAsync(jobs: jobs, configureBuilder: b => b.Logging.AddProvider(provider));
        await using var _ = app;

        var response = await client.GetAsync("/acta/jobs/api/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(provider.Entries, e => e.Category == "Acta.AspNetCore.Web" && e.Level == LogLevel.Error);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<(string Category, LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, this);

        public void Dispose() { }

        private void Record(string category, LogLevel level, string message)
        {
            lock (_entries)
            {
                _entries.Add((category, level, message));
            }
        }

        private sealed class RecordingLogger(string category, RecordingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) => owner.Record(category, logLevel, formatter(state, exception));
        }
    }

    [Fact]
    public async Task Namespaces_returns_names()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/jobs/api/namespaces", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("billing", body);
        Assert.Contains("reports", body);
    }

    [Fact]
    public async Task Tenants_returns_page_with_no_store()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/jobs/api/tenants", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("\"tenantKey\":\"cust-001\"", body);
        Assert.Contains("\"status\":\"active\"", body);
    }

    [Fact]
    public async Task Jobs_accepts_tenantId_filter_and_rejects_a_malformed_one()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var ok = await client.GetAsync("/acta/jobs/api/jobs?tenantId=1", ct);
        var bad = await client.GetAsync("/acta/jobs/api/jobs?tenantId=abc", ct);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task JobByKey_resolves_to_snapshot()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync(
            "/acta/jobs/api/jobs/by-key?jobNamespace=billing&deduplicationKey=ck-1",
            TestContext.Current.CancellationToken
        );
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"\"jobRef\":\"{Found}\"", body);
        Assert.DoesNotContain("\"jobId\"", body);
    }

    [Fact]
    public async Task JobByKey_unknown_returns_404()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync(
            "/acta/jobs/api/jobs/by-key?jobNamespace=billing&deduplicationKey=nope",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JobByKey_missing_params_returns_400()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/jobs/api/jobs/by-key?deduplicationKey=ck-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Schedule_preview_returns_shape_or_404()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var known = await client.GetAsync(
            "/acta/jobs/api/schedules/preview?jobNamespace=billing&jobName=send-invoice&scheduleName=daily",
            ct
        );
        var missing = await client.GetAsync(
            "/acta/jobs/api/schedules/preview?jobNamespace=billing&jobName=send-invoice&scheduleName=missing",
            ct
        );
        var body = await known.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal("no-store", known.Headers.CacheControl?.ToString());
        Assert.Contains("\"expression\":\"0 9 * * *\"", body);
        Assert.Contains("\"timeZoneId\":\"UTC\"", body);
        Assert.Contains("\"nextRunsUtc\":[", body);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Standalone_api_mapping_works_without_dashboard()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IJobs>(new TestDashboardHost.FakeJobs());
        var app = builder.Build();
        app.MapActaApi("/ops/api");
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var _ = app;
        var client = app.GetTestClient();

        var response = await client.GetAsync("/ops/api/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

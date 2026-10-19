using System.Diagnostics;
using System.Net;
using Acta.Tests.AspNetCore.Fakes;
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
    private static readonly string Found = FakeJobs.FoundJobRef.ToString();
    private static readonly string Missing = FakeJobs.MissingJobRef.ToString();

    [Fact]
    public async Task Jobs_endpoint_returns_page_with_no_store_and_no_payload_fields()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);
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

        var response = await client.GetAsync("/acta/api/v1/jobs?cursor=bogus", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unhandled_api_exceptions_map_to_generic_500_without_leaking_message()
    {
        var fakes = new FakeOperations();
        fakes.Ledger.ListJobs = (_, _) => throw new InvalidOperationException("Server=prod-db;Password=secret;connect failed");
        var (app, client) = await TestDashboardHost.StartAsync(
            configureBuilder: builder =>
                builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Clear()),
            fakes: fakes
        );
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("The Acta API failed to process the request", body);
        Assert.DoesNotContain("prod-db", body);
        Assert.DoesNotContain("secret", body);
    }

    [Fact]
    public async Task Database_failures_map_to_503_with_sanitized_unreachable_reason()
    {
        var fakes = new FakeOperations();
        fakes.Ledger.ListJobs = (_, _) => throw new TimeoutException("Server=prod-db;Password=secret;connect failed");
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);
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

        var badEnum = await client.GetAsync("/acta/api/v1/jobs?status=nope", TestContext.Current.CancellationToken);
        var badInt = await client.GetAsync("/acta/api/v1/jobs?pageSize=abc", TestContext.Current.CancellationToken);
        var badBool = await client.GetAsync("/acta/api/v1/alerts?unresolvedOnly=maybe", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, badEnum.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badInt.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badBool.StatusCode);
    }

    [Fact]
    public async Task Events_endpoint_binds_event_code_and_the_ref_filters()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var ok = await client.GetAsync(
            "/acta/api/v1/events?eventCode=namespace.updated"
                + $"&jobRef={FakeJobs.FoundJobRef}"
                + $"&workerRef={FakeWorkers.KnownWorkerRef}"
                + "&tenantName=acme",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.NotNull(fakes.Ledger.LastEventsQuery);
        Assert.Equal(Acta.EventCode.NamespaceUpdated, fakes.Ledger.LastEventsQuery!.EventCode);
        // Each ref is edge-resolved to the internal id the ledger indexes on.
        Assert.Equal(42L, fakes.Ledger.LastEventsQuery.JobId);
        Assert.Equal(42, fakes.Ledger.LastEventsQuery.WorkerId);
        Assert.Equal("acme", fakes.Ledger.LastEventsQuery.TenantName);
        Assert.Null(fakes.Ledger.LastEventsQuery.TenantId);

        // A divergent wire code (member JobDefinitionOverridesUpdated) binds via its [Code] string, not the member name.
        var divergent = await client.GetAsync(
            "/acta/api/v1/events?eventCode=definition.overrides-updated",
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, divergent.StatusCode);
        Assert.Equal(Acta.EventCode.JobDefinitionOverridesUpdated, fakes.Ledger.LastEventsQuery!.EventCode);

        var badCode = await client.GetAsync("/acta/api/v1/events?eventCode=nope", TestContext.Current.CancellationToken);
        var badJobRef = await client.GetAsync("/acta/api/v1/events?jobRef=abc", TestContext.Current.CancellationToken);
        var badWorkerRef = await client.GetAsync("/acta/api/v1/events?workerRef=abc", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, badCode.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badJobRef.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badWorkerRef.StatusCode);
    }

    /// <summary>
    /// A well-formed ref that names no row is a 404, the same answer the other ref-valued query
    /// filters (/jobs parentJobRef, /alerts jobRef) give; the read never runs.
    /// </summary>
    [Theory]
    [InlineData("jobRef")]
    [InlineData("lineageRootJobRef")]
    public async Task Events_endpoint_maps_a_job_ref_that_names_no_row_to_404(string parameter)
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var response = await client.GetAsync($"/acta/api/v1/events?{parameter}={JobRef.New()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(fakes.Ledger.LastEventsQuery);
    }

    [Fact]
    public async Task Events_endpoint_maps_a_worker_ref_that_names_no_row_to_404()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var response = await client.GetAsync($"/acta/api/v1/events?workerRef={WorkerRef.New()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(fakes.Ledger.LastEventsQuery);
    }

    /// <summary>The three ref filters answer exactly like their siblings on both inputs.</summary>
    [Fact]
    public async Task Events_ref_filters_answer_the_same_way_the_sibling_endpoints_do()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        var unknown = JobRef.New();

        var siblingMalformed = await client.GetAsync("/acta/api/v1/jobs?parentJobRef=abc", ct);
        var eventsMalformed = await client.GetAsync("/acta/api/v1/events?jobRef=abc", ct);
        var siblingUnknown = await client.GetAsync($"/acta/api/v1/alerts?jobRef={unknown}", ct);
        var eventsUnknown = await client.GetAsync($"/acta/api/v1/events?jobRef={unknown}", ct);

        Assert.Equal(HttpStatusCode.BadRequest, siblingMalformed.StatusCode);
        Assert.Equal(siblingMalformed.StatusCode, eventsMalformed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, siblingUnknown.StatusCode);
        Assert.Equal(siblingUnknown.StatusCode, eventsUnknown.StatusCode);
    }

    [Fact]
    public async Task Alert_detail_returns_the_alert_or_404()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var known = await client.GetAsync($"/acta/api/v1/alerts/{FakeAlerts.KnownAlertRef}", ct);
        var body = await known.Content.ReadAsStringAsync(ct);
        var unknown = await client.GetAsync($"/acta/api/v1/alerts/{AlertRef.New()}", ct);
        var malformed = await client.GetAsync("/acta/api/v1/alerts/9001", ct);

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Contains($"\"alertRef\":\"{FakeAlerts.KnownAlertRef}\"", body);
        Assert.Contains("\"severity\":\"critical\"", body);
        Assert.Contains($"\"jobRef\":\"{Found}\"", body);
        // The numeric alert id and its subject job id are engine internals, not wire identity.
        Assert.DoesNotContain("\"alertId\"", body);
        Assert.DoesNotContain("\"jobId\"", body);
        // An unknown ref is addressable and simply absent; a malformed one is caller input. They
        // answer differently so a client tells the two apart by the status code alone.
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task Definition_detail_is_addressed_by_its_natural_key_and_404s_for_an_unknown_one()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var known = await client.GetAsync("/acta/api/v1/definitions/billing/send-invoice", ct);
        var body = await known.Content.ReadAsStringAsync(ct);
        var unknownName = await client.GetAsync("/acta/api/v1/definitions/billing/no-such-job", ct);
        var unknownNamespace = await client.GetAsync("/acta/api/v1/definitions/no-such-ns/send-invoice", ct);

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Contains("\"jobNamespace\":\"billing\"", body);
        Assert.Contains("\"jobName\":\"send-invoice\"", body);
        Assert.DoesNotContain("\"definitionId\"", body);
        Assert.Equal(HttpStatusCode.NotFound, unknownName.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownNamespace.StatusCode);
    }

    /// <summary>
    /// The audit trail keys on the catalog id the natural key resolves to, so a key that names no
    /// definition answers exactly like the definition read itself rather than an empty page.
    /// </summary>
    [Fact]
    public async Task Definition_events_read_answers_404_for_a_definition_that_does_not_exist()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var known = await client.GetAsync(
            "/acta/api/v1/definitions/billing/send-invoice/events?eventCode=definition.overrides-updated",
            ct
        );
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        // The read ran scoped to the resolved catalog id, which never reaches the wire.
        Assert.Equal(5, fakes.Ledger.LastEventsQuery!.DefinitionId);
        // definition_id alone would also match every execution event for the definition's jobs
        // (deliberate, per ListEventsFilterMatrixSpec); the dashboard's Change history panel
        // narrows to the one definition-change event code so it doesn't flood with job noise.
        Assert.Equal(Acta.EventCode.JobDefinitionOverridesUpdated, fakes.Ledger.LastEventsQuery.EventCode);

        var unknown = await client.GetAsync("/acta/api/v1/definitions/billing/no-such-job/events", ct);
        var detail = await client.GetAsync("/acta/api/v1/definitions/billing/no-such-job", ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(detail.StatusCode, unknown.StatusCode);
    }

    [Fact]
    public async Task Job_detail_returns_snapshot_or_404()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var known = await client.GetAsync($"/acta/api/v1/jobs/{Found}", TestContext.Current.CancellationToken);
        var missing = await client.GetAsync($"/acta/api/v1/jobs/{Missing}", TestContext.Current.CancellationToken);
        var malformed = await client.GetAsync("/acta/api/v1/jobs/42", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    // Explain, lineage, and the per-panel result/checkpoint reads folded into GET /jobs/{ref}/detail
    // (JobDetailEndpointTests) and were removed per the pre-1.0 no-deprecated-code rule; the standalone
    // routes no longer exist. The snapshot, /detail, and events routes remain. `input` is the one
    // deliberate exception: the enqueue screen's clone prefill wants the payload and nothing else.
    [Fact]
    public async Task Folded_in_per_panel_read_routes_are_gone()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        foreach (var segment in new[] { "explain", "lineage", "result", "checkpoints" })
        {
            var response = await client.GetAsync($"/acta/api/v1/jobs/{Found}/{segment}", ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var input = await client.GetAsync($"/acta/api/v1/jobs/{Found}/input", ct);
        Assert.Equal(HttpStatusCode.OK, input.StatusCode);
    }

    // Off by default, an id: target is not a form this API addresses, so it is rejected as malformed
    // rather than answered as a miss - the numeric id still never becomes a second identity, and now
    // the refusal says so instead of impersonating an absent row.
    [Fact]
    public async Task Numeric_id_lookup_is_rejected_when_disabled_by_default()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var byId = await client.GetAsync("/acta/api/v1/jobs/id:42", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, byId.StatusCode);
    }

    [Fact]
    public async Task Numeric_id_lookup_resolves_when_enabled()
    {
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableNumericIdLookup = true);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var known = await client.GetAsync("/acta/api/v1/jobs/id:42", ct);
        var missing = await client.GetAsync("/acta/api/v1/jobs/id:99", ct);
        var bare = await client.GetAsync("/acta/api/v1/jobs/42", ct);
        var events = await client.GetAsync("/acta/api/v1/jobs/id:42/events", ct);

        var body = await known.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Contains($"\"jobRef\":\"{Found}\"", body);
        Assert.DoesNotContain("\"jobId\"", body);
        // id:99 is a form this host addresses and names no row, so it is the miss; a bare integer is
        // not a form it addresses at all, whatever the option says, so it stays malformed input.
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
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
            var response = await client.GetAsync($"/acta/api/v1/{path}", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Worker_detail_returns_worker_or_404()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var knownRef = FakeWorkers.KnownWorkerRef;
        var known = await client.GetAsync($"/acta/api/v1/workers/{knownRef}", ct);
        var missing = await client.GetAsync($"/acta/api/v1/workers/{WorkerRef.New()}", ct);
        var malformed = await client.GetAsync("/acta/api/v1/workers/not-a-ref", ct);
        var body = await known.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal("no-store", known.Headers.CacheControl?.ToString());
        Assert.Contains($"\"workerRef\":\"{knownRef}\"", body);
        Assert.Contains("\"jobNamespace\":\"billing\"", body);
        Assert.Contains("\"lastSeenAtUtc\":", body);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    /// <summary>
    /// A ref route reads one form, the exact prefix and 32 hex digits. The Crockford form, the dashed Guid, the bare
    /// Guid, and an uppercase prefix name nothing, so each is a 400; uppercase hex is the same ref, so it reaches the
    /// read and answers 404 for a row that is not there.
    /// </summary>
    [Theory]
    [InlineData("workers/", "", WorkerRef.Prefix)]
    [InlineData("alerts/", "", AlertRef.Prefix)]
    [InlineData("jobs/", "/detail", JobRef.Prefix)]
    public async Task Ref_routes_read_only_the_prefix_and_hex_form(string before, string after, string prefix)
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;
        const string Hex = "01a14f4e954e7bf68ac609f3fcad8e3d";
        string[] malformed =
        [
            prefix + "2n1t201rmv87aae5j4csam8000",
            prefix + "01a14f4e-954e-7bf6-8ac6-09f3fcad8e3d",
            Hex,
            prefix.ToUpperInvariant() + Hex,
        ];

        foreach (var value in malformed)
        {
            var response = await client.GetAsync($"/acta/api/v1/{before}{value}{after}", ct);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{value} answered {response.StatusCode}.");
        }

        var uppercase = await client.GetAsync($"/acta/api/v1/{before}{prefix}{Hex.ToUpperInvariant()}{after}", ct);
        Assert.Equal(HttpStatusCode.NotFound, uppercase.StatusCode);
    }

    [Fact]
    public async Task Overview_returns_health_counters()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/overview", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"readyCount\":3", body);
        Assert.Contains("\"oldestReadyAgeSeconds\":120", body);
        Assert.Contains("\"dueSoonScheduleCount\":5", body);
    }

    [Fact]
    public async Task Outbox_sources_report_the_relay_line_with_parsed_counters()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var body = await client.GetStringAsync("/acta/api/v1/outbox/sources", TestContext.Current.CancellationToken);

        Assert.Contains("\"jobNamespace\":\"billing\"", body);
        Assert.Contains("claimed=2", body);
        Assert.Contains("\"backlog\":5", body);
        Assert.Contains("\"quarantineTotal\":1", body);
        Assert.Contains("\"isLocal\":true", body);
    }

    [Fact]
    public async Task Outbox_sources_scope_to_a_namespace_without_a_relay_as_an_empty_page()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var body = await client.GetStringAsync("/acta/api/v1/outbox/sources?jobNamespace=reports", TestContext.Current.CancellationToken);

        Assert.Contains("\"items\":[]", body);
    }

    [Fact]
    public async Task Outbox_quarantined_lists_the_local_source_rows()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var body = await client.GetStringAsync(
            "/acta/api/v1/outbox/billing/quarantined?includeTotal=true",
            TestContext.Current.CancellationToken
        );

        Assert.Contains($"\"outboxId\":\"{FakeOutbox.QuarantinedId}\"", body);
        Assert.Contains("\"lastError\":\"route rejected: unknown job\"", body);
        Assert.Contains("\"totalCount\":1", body);
    }

    [Fact]
    public async Task Outbox_quarantined_answers_409_where_the_source_is_not_registered()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var nonLocal = await client.GetAsync("/acta/api/v1/outbox/reports/quarantined", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, nonLocal.StatusCode);
        Assert.Contains("no outbox relay registered", await nonLocal.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Overview_rejects_invalid_namespace()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/overview?jobNamespace=Not%20Kebab", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Internal_argument_exception_is_a_sanitized_500_not_a_400()
    {
        // Only the typed validation exceptions map to 400. A plain ArgumentException thrown by a
        // server-side bug must reach the sanitized 500 handler and must not echo its message.
        var fakes = new FakeOperations();
        fakes.Ledger.ListJobs = (_, _) => throw new ArgumentException("secret internal detail");
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("secret internal detail", body);
    }

    [Fact]
    public async Task Client_abort_produces_no_error_log_and_no_response()
    {
        var provider = new RecordingLoggerProvider();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakes = new FakeOperations();
        // The read holds until the request's token cancels, so the abort surfaces as a real
        // OperationCanceledException carrying RequestAborted, as a client disconnecting mid-read does.
        fakes.Ledger.ListJobs = async (_, ct) =>
        {
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new UnreachableException();
        };
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes, configureBuilder: b => b.Logging.AddProvider(provider));
        await using var _ = app;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var requestTask = client.GetAsync("/acta/api/v1/jobs", cts.Token);
        // The abort has to land while the read is in flight, so wait for the read to say it is rather
        // than sleeping a guess at how long the request takes to reach the handler.
        await readStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);

        Assert.DoesNotContain(provider.Entries, e => e.Category == "Acta.AspNetCore" && e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task Aborted_overview_is_absorbed_at_the_api_boundary_without_an_error_log()
    {
        var provider = new RecordingLoggerProvider();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakes = new FakeOperations();
        fakes.Ledger.GetOverview = async (_, ct) =>
        {
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new UnreachableException();
        };
        using var requestAborted = new CancellationTokenSource();
        var (app, client) = await TestDashboardHost.StartAsync(
            fakes: fakes,
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

        var request = client.GetAsync("/acta/api/v1/overview", TestContext.Current.CancellationToken);
        await readStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        requestAborted.Cancel();
        var response = await request;

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, (int)response.StatusCode);
        Assert.DoesNotContain(provider.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Cancellation_unrelated_to_request_abort_remains_a_service_error()
    {
        var provider = new RecordingLoggerProvider();
        var fakes = new FakeOperations();
        fakes.Ledger.ListJobs = (_, _) => throw new TaskCanceledException("provider timeout");
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes, configureBuilder: b => b.Logging.AddProvider(provider));
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(provider.Entries, e => e.Category == "Acta.AspNetCore" && e.Level == LogLevel.Error);
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
    /// <summary>
    /// One namespace representation, not two. The route used to answer with bare names and a second
    /// route carried the row; the row is now the only answer, so the assertion is on the fields only
    /// the row has.
    /// </summary>
    public async Task Namespaces_returns_rows_not_bare_names()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/namespaces", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"jobNamespace\":\"billing\"", body);
        Assert.Contains("\"status\":\"active\"", body);
        Assert.Contains("\"ownerTeam\":\"payments\"", body);
    }

    /// <summary>
    /// The 200 path serializes through the source-generated context, so the response type must be
    /// registered there: an unregistered type fails only at runtime, which is exactly how the
    /// GetTenantResponse introduction shipped a 500 no test saw.
    /// </summary>
    [Fact]
    public async Task Tenant_point_read_serializes_the_detail()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/tenants/cust-001", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"tenantName\":\"cust-001\"", body);
        Assert.Contains("\"displayName\":\"Acme\"", body);
    }

    /// <summary>
    /// A 404 names the thing that was not found. The two routes below share one problem-details
    /// helper, which used to hardcode "Job not found." and answered a missing tenant with it.
    /// </summary>
    [Fact]
    public async Task A_missing_row_is_reported_with_its_own_noun()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var missingTenant = await client.GetAsync("/acta/api/v1/tenants/nobody", TestContext.Current.CancellationToken);
        var missingJob = await client.GetAsync($"/acta/api/v1/jobs/{Missing}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, missingTenant.StatusCode);
        Assert.Contains(
            "Tenant not found.",
            await missingTenant.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal
        );

        Assert.Equal(HttpStatusCode.NotFound, missingJob.StatusCode);
        Assert.Contains(
            "Job not found.",
            await missingJob.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task Tenants_returns_page_with_no_store()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/tenants", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("\"tenantName\":\"cust-001\"", body);
        Assert.Contains("\"status\":\"active\"", body);
    }

    [Fact]
    public async Task Jobs_accepts_a_tenantName_filter_and_ignores_the_retired_tenantId()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var ok = await client.GetAsync("/acta/api/v1/jobs?tenantName=acme", ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("acme", fakes.Ledger.LastJobsQuery!.TenantName);

        // tenantId left the wire: it is no longer bound, so it neither filters nor 400s.
        var retired = await client.GetAsync("/acta/api/v1/jobs?tenantId=abc", ct);
        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
        Assert.Null(fakes.Ledger.LastJobsQuery!.TenantId);
    }

    [Fact]
    public async Task Jobs_accepts_a_lane_filter()
    {
        var fakes = new FakeOperations();
        var (app, client) = await TestDashboardHost.StartAsync(fakes: fakes);
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var ok = await client.GetAsync("/acta/api/v1/jobs?lane=customer-42", ct);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("customer-42", fakes.Ledger.LastJobsQuery!.Lane);
    }

    [Fact]
    public async Task JobByKey_resolves_to_snapshot()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync(
            "/acta/api/v1/jobs/by-key?jobNamespace=billing&deduplicationKey=ck-1",
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
            "/acta/api/v1/jobs/by-key?jobNamespace=billing&deduplicationKey=nope",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JobByKey_missing_params_returns_400()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/acta/api/v1/jobs/by-key?deduplicationKey=ck-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("jobNamespace=bad!&deduplicationKey=ck-1")]
    [InlineData("jobNamespace=billing&deduplicationKey=ck%201")]
    public async Task JobByKey_malformed_namespace_or_key_returns_400(string query)
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync($"/acta/api/v1/jobs/by-key?{query}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Schedule_preview_returns_shape_or_404()
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;
        var ct = TestContext.Current.CancellationToken;

        var known = await client.GetAsync("/acta/api/v1/schedules/billing/send-invoice/daily/preview", ct);
        var missing = await client.GetAsync("/acta/api/v1/schedules/billing/send-invoice/missing/preview", ct);
        var body = await known.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal("no-store", known.Headers.CacheControl?.ToString());
        Assert.Contains("\"expression\":\"0 9 * * *\"", body);
        Assert.Contains("\"timeZoneId\":\"UTC\"", body);
        Assert.Contains("\"nextRunsUtc\":[", body);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("bad!/send-invoice/daily")]
    [InlineData("billing/send%20invoice/daily")]
    [InlineData("billing/send-invoice/bad!")]
    public async Task Schedule_preview_with_a_malformed_segment_returns_400(string schedule)
    {
        var (app, client) = await TestDashboardHost.StartAsync();
        await using var _ = app;

        var response = await client.GetAsync($"/acta/api/v1/schedules/{schedule}/preview", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Standalone_api_mapping_works_without_dashboard()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var fakes = new FakeOperations();
        builder.Services.AddSingleton<IJobs>(fakes.Jobs);
        builder.Services.AddSingleton<IActaOperations>(fakes);
        var app = builder.Build();
        app.MapActaApi("/ops/api");
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var _ = app;
        var client = app.GetTestClient();

        var response = await client.GetAsync("/ops/api/v1/jobs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

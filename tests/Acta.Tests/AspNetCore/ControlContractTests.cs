using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acta.AspNetCore.Features.Alerts;
using Acta.AspNetCore.Features.Definitions;
using Acta.AspNetCore.Features.Jobs;
using Acta.AspNetCore.Features.Outbox;
using Acta.AspNetCore.Features.Schedules;
using Acta.AspNetCore.Features.Tenants;
using Acta.AspNetCore.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// The control contract as a table of real requests: every reachable outcome of every control verb,
/// issued against the in-process host and read back through the declared response type. The other
/// endpoint tests assert what one verb does; this one asserts that what the endpoint graph
/// <em>declares</em> and what the server <em>sends</em> are the same document, which is what
/// <c>openapi.json</c> is generated from and what a 1.0 client is entitled to.
/// </summary>
/// <remarks>
/// Three properties hold together, and each is a separate fact:
/// every row's real response carries the declared type and nothing else; every control route is in
/// the table; and every declared JSON response on a control route is reached by some row. So a new
/// verb fails the second fact until it is listed, a new status fails the third until it is exercised,
/// and a declaration that does not match the wire fails the first.
/// </remarks>
public sealed class ControlContractTests
{
    private const string Prefix = "/acta/api/v1";
    private const string Confirm = "X-Acta-Control";

    private static readonly string Found = TestDashboardHost.FoundJobRef.ToString();
    private static readonly string Missing = TestDashboardHost.MissingJobRef.ToString();
    private static readonly string Blocked = TestDashboardHost.RejectedJobRef.ToString();
    private static readonly string KnownAlert = TestDashboardHost.FakeJobs.KnownAlertRef.ToString();
    private static readonly string UnknownAlert = default(AlertRef).ToString();
    private static readonly string KnownWorker = TestDashboardHost.FakeJobs.KnownWorkerRef.ToString();

    /// <summary>The shared error model every route under the API group declares.</summary>
    private static readonly Type Problem = typeof(Microsoft.AspNetCore.Mvc.ProblemDetails);

    /// <summary>The control statuses this contract covers: the ones that carry a family envelope.</summary>
    private static readonly int[] OutcomeStatuses =
    [
        StatusCodes.Status200OK,
        StatusCodes.Status201Created,
        StatusCodes.Status202Accepted,
        StatusCodes.Status404NotFound,
        StatusCodes.Status409Conflict,
    ];

    /// <summary>
    /// One row: a real request, the outcome it stages, and the (status, type) pair the endpoint has
    /// to declare for it. <paramref name="Action"/> is null for the two verbs whose response is not
    /// an action envelope (tenant registration answers with the canonical key).
    /// </summary>
    private sealed record Case(
        string Family,
        string Route,
        string Method,
        string Outcome,
        string Path,
        int Status,
        Type Declared,
        string? Action,
        object? Body = null,
        int? Version = null,
        Action<TestDashboardHost.FakeJobs>? Stage = null
    );

    private static readonly object Reason = new { reasonMessage = "because" };

    // ---- the table ----

    private static IReadOnlyList<Case> Table { get; } =
    [
        .. JobVerb("pause"),
        .. JobVerb("resume"),
        .. JobVerb("restart"),
        .. JobVerb("cancel"),
        .. JobVerb("purge"),
        .. JobVerb("reschedule", new { nextRunAtUtc = "2030-01-01T00:00:00Z", reasonMessage = "because" }),
        .. JobVerb("reprioritize", new { priority = "high", reasonMessage = "because" }),
        .. JobSignal(),
        .. JobInput(),
        .. Enqueue(),
        .. ScheduleVerb("pause"),
        .. ScheduleVerb("resume"),
        .. ScheduleVerb("trigger"),
        .. ScheduleOverrides(),
        .. DefinitionOverrides(),
        .. AlertVerb("acknowledge"),
        .. AlertVerb("resolve"),
        .. OutboxVerb("requeue"),
        .. OutboxVerb("discard"),
        .. AdminVerb("namespaces", "/namespaces/{jobNamespace}", "billing", "suspend"),
        .. AdminVerb("namespaces", "/namespaces/{jobNamespace}", "billing", "resume"),
        .. AdminPatch("namespaces", "/namespaces/{jobNamespace}", "billing", "ownerTeam", "payments"),
        .. AdminVerb("tenants", "/tenants/{tenantKey}", "cust-001", "suspend"),
        .. AdminVerb("tenants", "/tenants/{tenantKey}", "cust-001", "resume"),
        .. AdminPatch("tenants", "/tenants/{tenantKey}", "cust-001", "displayName", "Acme"),
        .. TenantRegistration(),
        .. Tags("/jobs/{jobRef}", $"/jobs/{Found}"),
        .. Tags("/definitions/{jobNamespace}/{jobName}", "/definitions/billing/send-invoice"),
        .. Tags("/schedules/{jobNamespace}/{jobName}/{scheduleName}", "/schedules/billing/send-invoice/nightly"),
        .. Tags("/workers/{workerRef}", $"/workers/{KnownWorker}"),
        .. Tags("/alerts/{alertRef}", $"/alerts/{KnownAlert}"),
        .. Tags("/namespaces/{jobNamespace}", "/namespaces/billing"),
        .. Tags("/tenants/{tenantKey}", "/tenants/cust-001"),
        .. MalformedRefs(),
    ];

    /// <summary>
    /// The other half of the split, one row per branch that parses a ref-typed route segment. A ref
    /// this API cannot parse names nothing, and it is caller input, so it is a 400 problem document -
    /// which is what leaves 404 free to mean an addressable ref that is simply not there, and lets
    /// the families above declare one envelope for their whole 404.
    /// </summary>
    private static IEnumerable<Case> MalformedRefs()
    {
        static Case Row(string family, string route, string method, string path, object? body) =>
            new(family, route, method, "malformedRef", path, StatusCodes.Status400BadRequest, Problem, null, body);

        object upsert = new { name = "env", value = "prod" };
        return
        [
            Row("jobs", "/jobs/{jobRef}/pause", "POST", "/jobs/not-a-ref/pause", Reason),
            Row("jobs", "/jobs/{jobRef}/reschedule", "POST", "/jobs/not-a-ref/reschedule", Reason),
            Row("jobs", "/jobs/{jobRef}/reprioritize", "POST", "/jobs/not-a-ref/reprioritize", Reason),
            Row("jobs", "/jobs/{jobRef}/input", "POST", "/jobs/not-a-ref/input", Reason),
            Row("jobs", "/jobs/{jobRef}/signals/{signalName}", "POST", "/jobs/not-a-ref/signals/approval", null),
            Row("alerts", "/alerts/{alertRef}/acknowledge", "POST", "/alerts/not-a-ref/acknowledge", Reason),
            Row("alerts", "/alerts/{alertRef}/resolve", "POST", "/alerts/not-a-ref/resolve", Reason),
            Row("tags", "/jobs/{jobRef}/tags", "POST", "/jobs/not-a-ref/tags", upsert),
            Row("tags", "/workers/{workerRef}/tags", "POST", "/workers/not-a-ref/tags", upsert),
            Row("tags", "/alerts/{alertRef}/tags", "POST", "/alerts/not-a-ref/tags", upsert),
        ];
    }

    // Applied, rejected, and not-found at one job route: the fake answers by ref, so the three
    // outcomes are three refs against the same verb.
    private static IEnumerable<Case> JobVerb(string verb, object? body = null)
    {
        var route = $"/jobs/{{jobRef}}/{verb}";
        return
        [
            Job(route, "applied", $"/jobs/{Found}/{verb}", StatusCodes.Status200OK, "applied", body ?? Reason),
            Job(route, "rejected", $"/jobs/{Blocked}/{verb}", StatusCodes.Status409Conflict, "rejected", body ?? Reason),
            Job(route, "notFound", $"/jobs/{Missing}/{verb}", StatusCodes.Status404NotFound, "notFound", body ?? Reason),
        ];
    }

    // A signal raise carries the caller's own JSON verbatim, so its body is a raw value rather than a
    // reason envelope; an absent body is a presence-only signal, which is the simplest of the three.
    private static IEnumerable<Case> JobSignal()
    {
        const string route = "/jobs/{jobRef}/signals/{signalName}";
        return
        [
            Job(route, "applied", $"/jobs/{Found}/signals/approval", StatusCodes.Status200OK, "applied"),
            Job(route, "rejected", $"/jobs/{Blocked}/signals/approval", StatusCodes.Status409Conflict, "rejected"),
            Job(route, "notFound", $"/jobs/{Missing}/signals/approval", StatusCodes.Status404NotFound, "notFound"),
        ];
    }

    // The amend endpoint decides two of its three outcomes itself, before the verb runs: an
    // unresolvable ref is the not-found, and a job whose stored input is none is the rejection. Both
    // are staged on the fake's StoredInput rather than by ref.
    private static IEnumerable<Case> JobInput()
    {
        const string route = "/jobs/{jobRef}/input";
        object body = new { input = new { invoiceId = 1 }, reasonMessage = "because" };
        return
        [
            Job(route, "applied", $"/jobs/{Found}/input", StatusCodes.Status200OK, "applied", body, WithStoredInput),
            Job(route, "rejected", $"/jobs/{Found}/input", StatusCodes.Status409Conflict, "rejected", body, WithoutStoredInput),
            Job(route, "notFound", $"/jobs/{Missing}/input", StatusCodes.Status404NotFound, "notFound", body),
        ];
    }

    private static void WithStoredInput(TestDashboardHost.FakeJobs jobs) =>
        jobs.StoredInput = JobPayload.FromBytes(JobPayloadFormat.Json, System.Text.Encoding.UTF8.GetBytes("{}"));

    private static void WithoutStoredInput(TestDashboardHost.FakeJobs jobs) => jobs.StoredInput = null;

    private static Case Job(
        string route,
        string outcome,
        string path,
        int status,
        string action,
        object? body = null,
        Action<TestDashboardHost.FakeJobs>? stage = null
    ) => new("jobs", route, "POST", outcome, path, status, typeof(JobControlResponse), action, body, Stage: stage);

    // Enqueue is control-gated but creates a job rather than transitioning one, so its two outcomes
    // are inserted and deduplicated. Its rejection stays a problem document (the guard reports why the
    // namespace or tenant refuses the work), which is why no 409 row appears here.
    private static IEnumerable<Case> Enqueue()
    {
        object body = new
        {
            jobNamespace = "billing",
            jobName = "send-invoice",
            deduplicationKey = "invoice-9",
        };
        return
        [
            new("jobs", "/jobs", "POST", "inserted", "/jobs", StatusCodes.Status201Created, typeof(JobEnqueueResponse), "inserted", body),
            // Second request, same deduplication key: the fake matches the first exactly as the store does.
            new(
                "jobs",
                "/jobs",
                "POST",
                "deduplicated",
                "/jobs",
                StatusCodes.Status200OK,
                typeof(JobEnqueueResponse),
                "deduplicated",
                body
            ),
        ];
    }

    // A schedule name of "missing" reads as absent and "rejected" as a forbidden transition, so all
    // four schedule verbs stage their three outcomes by name.
    private static IEnumerable<Case> ScheduleVerb(string verb)
    {
        var route = $"/schedules/{{jobNamespace}}/{{jobName}}/{{scheduleName}}/{verb}";
        string Path(string scheduleName) => $"/schedules/billing/send-invoice/{scheduleName}/{verb}";
        return
        [
            Schedule(route, "applied", Path("nightly"), StatusCodes.Status200OK, "applied", Reason),
            Schedule(route, "rejected", Path("rejected"), StatusCodes.Status409Conflict, "rejected", Reason),
            Schedule(route, "notFound", Path("missing"), StatusCodes.Status404NotFound, "notFound", Reason),
        ];
    }

    // The one schedule verb with a CAS token: its rejection is a version conflict, and the response
    // carries the row's current version so the caller retries without a re-read.
    private static IEnumerable<Case> ScheduleOverrides()
    {
        const string route = "/schedules/{jobNamespace}/{jobName}/{scheduleName}/overrides";
        object body = new { expectedVersion = 1, reasonMessage = "because" };
        static string Path(string scheduleName) => $"/schedules/billing/send-invoice/{scheduleName}/overrides";
        return
        [
            Schedule(route, "applied", Path("nightly"), StatusCodes.Status200OK, "applied", body, version: 2),
            Schedule(route, "rejected", Path("rejected"), StatusCodes.Status409Conflict, "rejected", body, version: 7),
            Schedule(route, "notFound", Path("missing"), StatusCodes.Status404NotFound, "notFound", body),
        ];
    }

    private static Case Schedule(string route, string outcome, string path, int status, string action, object body, int? version = null) =>
        new("schedules", route, "POST", outcome, path, status, typeof(ScheduleControlResponse), action, body, version);

    private static IEnumerable<Case> DefinitionOverrides()
    {
        const string route = "/definitions/{jobNamespace}/{jobName}";
        static Case Row(string outcome, string path, int status, string action, int expectedVersion) =>
            new(
                "definitions",
                route,
                "PATCH",
                outcome,
                path,
                status,
                typeof(DefinitionControlResponse),
                action,
                new { expectedVersion, reasonMessage = "because" }
            );
        return
        [
            Row("applied", "/definitions/billing/send-invoice", StatusCodes.Status200OK, "applied", 1),
            Row("rejected", "/definitions/billing/send-invoice", StatusCodes.Status409Conflict, "rejected", 999),
            Row("notFound", "/definitions/billing/missing", StatusCodes.Status404NotFound, "notFound", 1),
        ];
    }

    // Acknowledge and resolve are idempotent, so they have no rejection: only applied and not-found.
    private static IEnumerable<Case> AlertVerb(string verb)
    {
        var route = $"/alerts/{{alertRef}}/{verb}";
        Case Row(string outcome, string alertRef, int status, string action) =>
            new("alerts", route, "POST", outcome, $"/alerts/{alertRef}/{verb}", status, typeof(AlertControlResponse), action, Reason);
        return
        [
            Row("applied", KnownAlert, StatusCodes.Status200OK, "applied"),
            Row("notFound", UnknownAlert, StatusCodes.Status404NotFound, "notFound"),
        ];
    }

    // The outbox verbs park a durable command the next relay pass applies, so their success is 202
    // rather than 200; the other two outcomes are the usual pair.
    private static IEnumerable<Case> OutboxVerb(string verb)
    {
        var route = $"/outbox/{{jobNamespace}}/{verb}";
        Case Row(string outcome, string jobNamespace, int status, string action) =>
            new("outbox", route, "POST", outcome, $"/outbox/{jobNamespace}/{verb}", status, typeof(OutboxControlResponse), action, Reason);
        return
        [
            Row("accepted", "billing", StatusCodes.Status202Accepted, "accepted"),
            Row("rejected", "rejected", StatusCodes.Status409Conflict, "rejected"),
            Row("notFound", "missing", StatusCodes.Status404NotFound, "notFound"),
        ];
    }

    // Suspend and resume take no expected version, so a version conflict is not among their outcomes.
    private static IEnumerable<Case> AdminVerb(string family, string routePrefix, string key, string verb)
    {
        var route = $"{routePrefix}/{verb}";
        return
        [
            Admin(family, route, "POST", "applied", $"{Path(routePrefix, key)}/{verb}", StatusCodes.Status200OK, "applied", Reason, 2),
            Admin(
                family,
                route,
                "POST",
                "notFound",
                $"{Path(routePrefix, "missing")}/{verb}",
                StatusCodes.Status404NotFound,
                "notFound",
                Reason
            ),
        ];
    }

    // The patch is the one admin verb with a CAS token, so it is the one that can version-conflict; a
    // stale expectedVersion of 999 is how both admin fakes stage that outcome.
    private static IEnumerable<Case> AdminPatch(string family, string route, string key, string field, string value)
    {
        JsonObject Body(int expectedVersion) => new() { [field] = value, ["expectedVersion"] = expectedVersion };
        return
        [
            Admin(family, route, "PATCH", "applied", Path(route, key), StatusCodes.Status200OK, "applied", Body(1), 2),
            Admin(
                family,
                route,
                "PATCH",
                "versionConflict",
                Path(route, key),
                StatusCodes.Status409Conflict,
                "versionConflict",
                Body(999),
                5
            ),
            Admin(family, route, "PATCH", "notFound", Path(route, "missing"), StatusCodes.Status404NotFound, "notFound", Body(1)),
        ];
    }

    private static Case Admin(
        string family,
        string route,
        string method,
        string outcome,
        string path,
        int status,
        string action,
        object body,
        int? version = null
    ) => new(family, route, method, outcome, path, status, typeof(AdminControlResponse), action, body, version);

    // Registration is insert-or-get and answers with the canonical key rather than an action, so this
    // row asserts the shape and leaves the action unchecked.
    private static IEnumerable<Case> TenantRegistration() =>
        [
            new(
                "tenants",
                "/tenants",
                "POST",
                "registered",
                "/tenants",
                StatusCodes.Status200OK,
                typeof(TenantRegistrationResponse),
                null,
                new { tenantKey = "cust-002" }
            ),
        ];

    // Every taggable entity carries the same two mutations answering the same two ways, so the target
    // is the only thing that varies across the fourteen tag routes.
    private static IEnumerable<Case> Tags(string routePrefix, string pathPrefix)
    {
        Case Row(string method, string routeSuffix, string pathSuffix, int status, string action, object? body) =>
            new(
                "tags",
                routePrefix + routeSuffix,
                method,
                action,
                pathPrefix + pathSuffix,
                status,
                typeof(AdminControlResponse),
                action,
                body,
                Stage: status == StatusCodes.Status200OK ? TagApplies : TagTargetMissing
            );

        object upsert = new { name = "env", value = "prod" };
        return
        [
            Row("POST", "/tags", "/tags", StatusCodes.Status200OK, "applied", upsert),
            Row("POST", "/tags", "/tags", StatusCodes.Status404NotFound, "notFound", upsert),
            Row("DELETE", "/tags/{tagName}", "/tags/env", StatusCodes.Status200OK, "applied", null),
            Row("DELETE", "/tags/{tagName}", "/tags/env", StatusCodes.Status404NotFound, "notFound", null),
        ];
    }

    private static void TagApplies(TestDashboardHost.FakeJobs jobs) =>
        jobs.TagsFake.MutationResult = new TagMutationResult(TagMutationAction.Applied);

    private static void TagTargetMissing(TestDashboardHost.FakeJobs jobs) =>
        jobs.TagsFake.MutationResult = new TagMutationResult(TagMutationAction.NotFound);

    private static string Path(string route, string key) => route[..route.IndexOf('{', StringComparison.Ordinal)] + key;

    // ---- the facts ----

    [Fact]
    public async Task Every_control_outcome_answers_with_its_declared_type()
    {
        var jobs = new TestDashboardHost.FakeJobs();
        var (app, client) = await TestDashboardHost.StartAsync(options => options.EnableControls = true, jobs: jobs);
        await using var _ = app;
        var declared = Declarations(app);
        var failures = new List<string>();

        // A table that lost its rows, or a graph read that found no declarations, would pass every
        // check below by having nothing to check. Both counts only grow, so they pin the traversal.
        Assert.True(Table.Count > 80, $"the contract table holds only {Table.Count} rows; it lost its families.");
        Assert.True(declared.Count > 50, $"the endpoint graph declared only {declared.Count} JSON responses; the read is broken.");

        foreach (var row in Table)
        {
            row.Stage?.Invoke(jobs);
            using var request = Request(row);
            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var where = $"{row.Method} {row.Route} ({row.Outcome})";

            if ((int)response.StatusCode != row.Status)
            {
                failures.Add($"{where}: expected {row.Status}, got {(int)response.StatusCode}. Body: {body}");
            }
            else if (!declared.Contains(new Declaration(row.Route, row.Method, row.Status, row.Declared)))
            {
                failures.Add($"{where}: the endpoint does not declare {row.Declared.Name} for {row.Status}.");
            }
            else if (Mismatch(body, row) is { } problem)
            {
                failures.Add($"{where}: {problem}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task Every_control_route_is_in_the_table()
    {
        var (controls, reads) = await BothHostsAsync();
        await using var _ = controls;
        await using var __ = reads;

        var readSurface = Signatures(reads);
        var covered = new HashSet<string>(Table.Select(row => $"{row.Method} {row.Route}"), StringComparer.Ordinal);
        var control = Signatures(controls)
            .Where(signature => !readSurface.Contains(signature) && !NotAnOutcome.Contains(signature))
            .ToList();
        var uncovered = control.Where(signature => !covered.Contains(signature)).OrderBy(s => s, StringComparer.Ordinal).ToList();

        // Two hosts that mapped the same surface would leave nothing to cover and pass vacuously.
        Assert.True(control.Count > 30, $"only {control.Count} routes appear under EnableControls; the host difference is broken.");
        Assert.True(
            uncovered.Count == 0,
            "A control route with no row in the contract table. Add its outcomes to the table, or, if it "
                + "answers no control outcome at all, name it in NotAnOutcome with the reason.\n\n"
                + string.Join("\n", uncovered)
        );
    }

    [Fact]
    public async Task Every_declared_control_response_is_exercised()
    {
        var (controls, reads) = await BothHostsAsync();
        await using var _ = controls;
        await using var __ = reads;

        var readSurface = Signatures(reads);
        var exercised = new HashSet<Declaration>(Table.Select(row => new Declaration(row.Route, row.Method, row.Status, row.Declared)));
        var control = Declarations(controls)
            .Where(d => !readSurface.Contains($"{d.Method} {d.Route}") && !NotAnOutcome.Contains($"{d.Method} {d.Route}"))
            // ProblemDetails is the shared error model, declared once at the API group for every
            // route at once (400, 403, 413, 500, 503) rather than as a family's own outcome, so
            // requiring a request per route for each would be requiring a fault per route. The
            // malformed-ref rows exercise the one problem status this table does own.
            .Where(d => OutcomeStatuses.Contains(d.Status) && d.Type != Problem)
            .ToList();
        var unexercised = control
            .Where(d => !exercised.Contains(d))
            .Select(d => $"{d.Method} {d.Route}: {d.Status} declares {d.Type.Name}, which no request in the table produces")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        Assert.True(control.Count > 40, $"only {control.Count} control responses were declared; the graph read is broken.");
        Assert.True(
            unexercised.Count == 0,
            "Every declared control response has to be reached by a real request, or the committed "
                + "contract promises a body nothing sends.\n\n"
                + string.Join("\n", unexercised)
        );
    }

    /// <summary>
    /// The one control-gated route that answers no control outcome: the input-template read is a read,
    /// behind the gate only because it exposes the compile-time input contract.
    /// </summary>
    private static readonly HashSet<string> NotAnOutcome = new(StringComparer.Ordinal) { "GET /jobs/input-template" };

    // ---- reading the endpoint graph ----

    /// <summary>One declared response: the route, verb, status, and body type the graph promises.</summary>
    private sealed record Declaration(string Route, string Method, int Status, Type Type);

    /// <summary>
    /// Two hosts, because a control route is exactly a route the read-only host does not serve. Taking
    /// the difference rather than matching a name or a prefix means a family mapped somewhere new is
    /// still seen as control.
    /// </summary>
    private static async Task<(WebApplication Controls, WebApplication Reads)> BothHostsAsync()
    {
        var (controls, _) = await TestDashboardHost.StartAsync(options => options.EnableControls = true);
        var (reads, _) = await TestDashboardHost.StartAsync();
        return (controls, reads);
    }

    /// <summary>Every "VERB /route" the host serves under the API prefix.</summary>
    private static HashSet<string> Signatures(WebApplication app) =>
        [.. Mapped(app).SelectMany(e => e.Methods.Select(method => $"{method} {e.Route}"))];

    private static HashSet<Declaration> Declarations(WebApplication app) =>
        [
            .. Mapped(app)
                .SelectMany(e =>
                    e.Endpoint.Metadata.OfType<IProducesResponseTypeMetadata>()
                        .Where(m => m.Type is not null)
                        .SelectMany(m => e.Methods.Select(method => new Declaration(e.Route, method, m.StatusCode, m.Type!)))
                ),
        ];

    private static IEnumerable<(string Route, IReadOnlyList<string> Methods, Endpoint Endpoint)> Mapped(WebApplication app) =>
        ((IEndpointRouteBuilder)app)
            .DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(Prefix, StringComparison.Ordinal) == true)
            .Select(endpoint =>
                (Route: endpoint.RoutePattern.RawText![Prefix.Length..], Methods: Methods(endpoint), Endpoint: (Endpoint)endpoint)
            );

    private static IReadOnlyList<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { } methods ? [.. methods] : [];

    // ---- one request, one answer ----

    private static HttpRequestMessage Request(Case row)
    {
        var request = new HttpRequestMessage(new HttpMethod(row.Method), Prefix + row.Path);
        request.Headers.Add(Confirm, "true");
        if (row.Body is not null)
        {
            request.Content = JsonContent.Create(row.Body, row.Body.GetType());
        }

        return request;
    }

    /// <summary>
    /// What the wire and the declared type disagree about, or null when they agree. Deserializing is
    /// not enough on its own - a problem document deserializes into a control response as a record of
    /// defaults - so this round-trips through the server's own contract and compares member names:
    /// every member on the wire has to be one the declared type declares. Not the reverse, because
    /// ProblemDetails writes only the members it was given while the source-generated envelopes write
    /// all of theirs; one direction is the property that matters anyway, since it is what makes a body
    /// describable by what the document promised.
    /// </summary>
    private static string? Mismatch(string body, Case row)
    {
        var typeInfo = DashboardJsonContext.Default.GetTypeInfo(row.Declared);
        if (typeInfo is null)
        {
            return $"{row.Declared.Name} is not serializable by DashboardJsonContext.";
        }

        object? value;
        try
        {
            value = JsonSerializer.Deserialize(body, typeInfo);
        }
        catch (JsonException ex)
        {
            return $"the body does not read as {row.Declared.Name}: {ex.Message}. Body: {body}";
        }

        if (value is null)
        {
            return $"the body read as a null {row.Declared.Name}. Body: {body}";
        }

        if (JsonNode.Parse(body) is not JsonObject sent)
        {
            return $"the body is not a JSON object. Body: {body}";
        }

        var declared = Names((JsonObject)JsonNode.Parse(JsonSerializer.Serialize(value, typeInfo))!);
        var onTheWire = Names(sent);
        if (!onTheWire.IsSubsetOf(declared))
        {
            return $"the body carries [{Listed(onTheWire)}] where {row.Declared.Name} carries [{Listed(declared)}]. Body: {body}";
        }

        if (row.Declared == Problem && sent["status"]?.GetValue<int>() != row.Status)
        {
            return $"the problem document reports status '{sent["status"]}', expected {row.Status}.";
        }

        if (row.Action is { } action && sent["action"]?.GetValue<string>() != action)
        {
            return $"action is '{sent["action"]}', expected '{action}'.";
        }

        return row.Version is { } version && sent["version"]?.GetValue<int>() != version
            ? $"version is '{sent["version"]}', expected {version}."
            : null;
    }

    private static HashSet<string> Names(JsonObject node) => [.. node.Select(member => member.Key)];

    private static string Listed(IEnumerable<string> names) => string.Join(", ", names.OrderBy(name => name, StringComparer.Ordinal));
}

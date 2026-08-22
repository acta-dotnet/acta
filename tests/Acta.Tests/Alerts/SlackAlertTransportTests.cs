using System.Net;
using System.Text.Json;
using Acta.Runtime.Modules.Alerting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Alerts;

/// <summary>
/// The Slack webhook transport, driven over a stub <see cref="HttpMessageHandler"/> so the whole
/// request/response contract is exercised without a network. Four things are pinned: what goes on the
/// wire (one POST of the <see cref="SlackAlertFormatter"/> payload as UTF-8 JSON to the channel's
/// endpoint), how an HTTP status maps to <see cref="AlertDeliveryOutcome"/> (2xx delivered, 429 and 5xx
/// retryable, other 4xx permanent - the difference between an alert that retries and one that is marked
/// terminally Failed), what an unreachable Slack does (retryable, with a warning that names the channel
/// and the alert), and that shutdown cancellation propagates instead of being reported as a delivery
/// failure. The sibling <see cref="LogAlertTransport"/> is covered by
/// <see cref="LogAlertTransportTests"/>; this is the only transport that talks to a real endpoint.
/// </summary>
public sealed class SlackAlertTransportTests
{
    private const string Endpoint = "https://hooks.slack.example/services/T000/B000/xxx";

    [Fact]
    public async Task A_channel_with_no_endpoint_is_permanent_and_never_reaches_the_wire()
    {
        // An operator-configured Slack channel with a blank endpoint can never be delivered by retrying,
        // so the alert must be marked terminally Failed rather than retried forever - and the warning has
        // to name both the channel to fix and the alert that was lost.
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var log = new RecordingLogger();
        var transport = new SlackAlertTransport(new HttpClient(handler), log);

        var outcome = await transport.SendAsync(Notification(), Target(endpoint: "   "), TestContext.Current.CancellationToken);

        Assert.Equal(AlertDeliveryOutcome.Permanent, outcome);
        Assert.Empty(handler.Requests);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("has no endpoint", entry.Message, StringComparison.Ordinal);
        Assert.Contains("ops-critical", entry.Message, StringComparison.Ordinal);
        Assert.Contains(TestAlertRef.ToString(), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_delivered_alert_posts_the_formatter_payload_as_utf8_json_to_the_channel_endpoint()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var log = new RecordingLogger();
        var transport = new SlackAlertTransport(new HttpClient(handler), log);
        var notification = Notification();

        var outcome = await transport.SendAsync(notification, Target(), TestContext.Current.CancellationToken);

        Assert.Equal(AlertDeliveryOutcome.Delivered, outcome);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Endpoint, request.Uri);

        // Slack rejects anything but a JSON body, and a non-UTF-8 charset mangles the severity emoji the
        // formatter puts in the header line, so the media type and charset are part of the contract.
        Assert.Equal("application/json; charset=utf-8", request.ContentType);

        // The body is the formatter's payload verbatim: the transport composes nothing of its own.
        var sent = JsonSerializer.Deserialize(request.Body, AlertSlackJsonContext.Default.SlackMessage);
        Assert.NotNull(sent);
        Assert.Equal(SlackAlertFormatter.Build(notification).Text, sent!.Text);
        var fields = Assert.Single(sent.Attachments).Fields;
        Assert.Contains(fields, f => f.Title == "Alert" && f.Value == TestAlertRef.ToString());
        Assert.Contains(fields, f => f.Title == "Job" && f.Value == TestJobRef.ToString());

        // Nothing is logged on the happy path: a storm of delivered alerts must not also be a log storm.
        Assert.Empty(log.Entries);
    }

    [Theory]
    // Slack's own rate limit is the one a storm hits first, and it must not burn the alert.
    [InlineData(HttpStatusCode.TooManyRequests, AlertDeliveryOutcome.Retryable)]
    [InlineData(HttpStatusCode.InternalServerError, AlertDeliveryOutcome.Retryable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AlertDeliveryOutcome.Retryable)]
    [InlineData((HttpStatusCode)599, AlertDeliveryOutcome.Retryable)]
    // A revoked or mistyped webhook answers 404/403/400; retrying that forever only burns the queue.
    [InlineData(HttpStatusCode.NotFound, AlertDeliveryOutcome.Permanent)]
    [InlineData(HttpStatusCode.Forbidden, AlertDeliveryOutcome.Permanent)]
    [InlineData(HttpStatusCode.BadRequest, AlertDeliveryOutcome.Permanent)]
    // 2xx other than 200: Slack answers 200 today, but IsSuccessStatusCode is the stated rule.
    [InlineData(HttpStatusCode.Accepted, AlertDeliveryOutcome.Delivered)]
    public async Task An_http_status_maps_to_the_retry_semantics_the_projector_acts_on(HttpStatusCode status, AlertDeliveryOutcome expected)
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(status));
        var transport = new SlackAlertTransport(new HttpClient(handler), new RecordingLogger());

        var outcome = await transport.SendAsync(Notification(), Target(), TestContext.Current.CancellationToken);

        Assert.Equal(expected, outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task An_unreachable_slack_is_retryable_and_warns_with_the_channel_and_the_alert()
    {
        // DNS failure, refused connection, TLS error: HttpClient surfaces all of them as
        // HttpRequestException. None is the alert's fault, so the alert stays retryable, and the warning
        // carries the exception so an operator can tell a bad endpoint from a network outage.
        var failure = new HttpRequestException("No such host is known.");
        var handler = new StubHandler((_, _) => throw failure);
        var log = new RecordingLogger();
        var transport = new SlackAlertTransport(new HttpClient(handler), log);

        var outcome = await transport.SendAsync(Notification(), Target(), TestContext.Current.CancellationToken);

        Assert.Equal(AlertDeliveryOutcome.Retryable, outcome);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("failed transiently", entry.Message, StringComparison.Ordinal);
        Assert.Contains("ops-critical", entry.Message, StringComparison.Ordinal);
        Assert.Contains(TestAlertRef.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Same(failure, entry.Exception);
    }

    [Fact]
    public async Task A_send_cancelled_by_shutdown_propagates_instead_of_being_reported_as_a_failure()
    {
        // Shutdown landing mid-send is not a Slack failure: swallowing it as Retryable would let the
        // drain record a delivery attempt that never happened. The handler cancels the caller's token
        // from inside the send and then throws, which is deterministically the shutdown window.
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(
            (_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
        );
        var log = new RecordingLogger();
        var transport = new SlackAlertTransport(new HttpClient(handler), log);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(Notification(), Target(), cts.Token));

        // Not logged as a transient delivery failure: the cancellation is the caller's to interpret.
        Assert.Empty(log.Entries);
    }

    // ---------- fixtures ----------

    private static readonly AlertRef TestAlertRef = new(new Guid("019826f0-0000-7000-8000-0000000005a1"));
    private static readonly JobRef TestJobRef = new(new Guid("019826f0-0000-7000-8000-0000000005a2"));

    private static AlertNotification Notification() =>
        new(
            AlertRef: TestAlertRef,
            JobNamespace: "orders",
            JobRef: TestJobRef,
            Severity: AlertSeverityCode.Critical,
            Kind: AlertKindCode.FinalFailure,
            Title: "Job 'charge' failed",
            Message: "Terminal failure: boom.",
            RunbookUrl: "https://runbook.example/charge",
            OccurrenceCount: 4,
            CreatedAtUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        );

    private static AlertTarget Target(string endpoint = Endpoint) =>
        new(
            ChannelName: "ops-critical",
            TransportKind: SlackAlertTransport.Kind,
            Endpoint: endpoint,
            ConfigFormatId: 0,
            Config: ReadOnlyMemory<byte>.Empty
        );

    // Records what reached the wire and answers with whatever the test scripted. Reading the body here
    // (rather than holding the request) keeps it readable after the transport disposes its content.
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(
                new SentRequest(request.Method, request.RequestUri?.ToString(), body, request.Content?.Headers.ContentType?.ToString())
            );
            return respond(request, ct);
        }
    }

    private sealed record SentRequest(HttpMethod Method, string? Uri, string Body, string? ContentType);

    private sealed class RecordingLogger : ILogger<SlackAlertTransport>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
    }

    private sealed record Entry(LogLevel Level, string Message, Exception? Exception);
}

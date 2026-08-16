using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Acta.Runtime.Modules.Alerting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Alerts;

/// <summary>
/// The third No_integer_identities gate: an alert leaves Acta addressed by its public ref. Structural
/// first (reflection over the transport contract, so a numeric identity cannot be added without failing
/// here), then rendered (both shipped transports print <c>alr_</c> / <c>job_</c> and never the stored
/// uuid the ref decodes to, which is the internal form an operator can neither read nor address).
/// </summary>
public sealed class AlertTransportContractTests
{
    private static readonly Guid AlertRefValue = new("019826f0-0000-7000-8000-0000000004a1");
    private static readonly Guid JobRefValue = new("019826f0-0000-7000-8000-0000000004a2");
    private static readonly AlertRef TestAlertRef = new(AlertRefValue);
    private static readonly JobRef TestJobRef = new(JobRefValue);

    // Every CLR integral spelling, so a member typed short or byte is caught the same as a long.
    private static readonly HashSet<Type> IntegerTypes =
    [
        typeof(byte),
        typeof(sbyte),
        typeof(short),
        typeof(ushort),
        typeof(int),
        typeof(uint),
        typeof(long),
        typeof(ulong),
    ];

    // The one integer a notification may carry: how many times the condition fired inside the dedupe
    // window. A count, not an identity - and the only reason this is a list rather than a flat ban.
    private static readonly HashSet<string> AllowedIntegerMembers = new(StringComparer.Ordinal) { "OccurrenceCount" };

    [Fact]
    public void Alert_notification_carries_refs_and_no_integer_identity()
    {
        var properties = typeof(AlertNotification).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var integers = properties
            .Where(p => IntegerTypes.Contains(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType))
            .Where(p => !AllowedIntegerMembers.Contains(p.Name))
            .Select(p => $"{p.Name} ({p.PropertyType.Name})")
            .ToList();
        Assert.True(
            integers.Count == 0,
            "AlertNotification is the transport contract: a database integer on it would be delivered to "
                + "Slack and the operator log as an identity nobody can address. Carry the ref instead. "
                + string.Join(", ", integers)
        );

        // A bare Guid is the other way an unaddressable identity gets here, and the one this codebase is
        // actually prone to: alert_ref, worker_ref, and events.actor_key all STORE the uuid, and only
        // AlertRef / JobRef render it as the alr_ / job_ handle an operator can paste. A Guid-typed
        // member would pass the integer check and still deliver the stored form.
        var rawGuids = properties
            .Where(p => (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) == typeof(Guid))
            .Select(p => $"{p.Name} ({p.PropertyType.Name})")
            .ToList();
        Assert.True(
            rawGuids.Count == 0,
            "AlertNotification must carry the ref types, not the uuid they wrap: a Guid member renders as "
                + "the stored form, which no operator can address. Use AlertRef / JobRef. "
                + string.Join(", ", rawGuids)
        );

        // An id smuggled back as a string would pass the integer check, so the naming rule is pinned too:
        // the identity members are the two ref types, and nothing else claims to be an identity.
        Assert.Equal(typeof(AlertRef), properties.Single(p => p.Name == nameof(AlertNotification.AlertRef)).PropertyType);
        Assert.Equal(typeof(JobRef?), properties.Single(p => p.Name == nameof(AlertNotification.JobRef)).PropertyType);
        Assert.Empty(properties.Where(p => p.Name.EndsWith("Id", StringComparison.Ordinal)).Select(p => p.Name));
    }

    [Fact]
    public async Task Log_transport_renders_both_refs_and_never_their_stored_uuid()
    {
        var logger = new RecordingLogger();

        var outcome = await new LogAlertTransport(logger).SendAsync(Notification(), Target(), TestContext.Current.CancellationToken);

        Assert.Equal(AlertDeliveryOutcome.Delivered, outcome);
        var entry = Assert.Single(logger.Entries);
        AssertRendersRefsOnly(entry.Message, TestAlertRef.ToString(), TestJobRef.ToString());

        // Structured logging keeps the values typed, so a sink that renders properties rather than the
        // formatted message gets the refs too rather than falling back to some numeric surrogate.
        Assert.Contains(entry.Properties, p => p.Key == "AlertRef" && p.Value is AlertRef);
        Assert.Contains(entry.Properties, p => p.Key == "JobRef" && p.Value is JobRef);
    }

    [Fact]
    public void Slack_payload_renders_both_refs_and_never_their_stored_uuid()
    {
        var payload = SlackAlertFormatter.Build(Notification());

        var json = JsonSerializer.Serialize(payload, AlertSlackJsonContext.Default.SlackMessage);

        var fields = Assert.Single(payload.Attachments).Fields;
        Assert.Contains(fields, f => f.Title == "Alert" && f.Value == TestAlertRef.ToString());
        Assert.Contains(fields, f => f.Title == "Job" && f.Value == TestJobRef.ToString());
        AssertRendersRefsOnly(json, TestAlertRef.ToString(), TestJobRef.ToString());
    }

    // A job-less alert is the case that made this a gap rather than a nicety: with the job field absent
    // and no alert field, the delivered notification named nothing the reader could open.
    [Fact]
    public void Slack_payload_for_a_job_less_alert_still_carries_the_alert_ref()
    {
        var payload = SlackAlertFormatter.Build(Notification() with { JobRef = null });

        var json = JsonSerializer.Serialize(payload, AlertSlackJsonContext.Default.SlackMessage);

        Assert.DoesNotContain(Assert.Single(payload.Attachments).Fields, f => f.Title == "Job");
        AssertRendersRefsOnly(json, TestAlertRef.ToString());
    }

    // The sentinel is the ref's stored form, in both spellings a leak could take. events.actor_key and
    // workers.worker_ref hold canonical uuid text, so "renders the ref" and "renders whatever the row
    // holds" look alike in a fixture unless the uuid itself is banned. There is no numeric check here
    // because there is no numeric member to leak: the reflection gate above proves AlertNotification
    // carries no integer identity at all, which is what makes this pair of checks sufficient.
    private static void AssertRendersRefsOnly(string rendered, params string[] expectedRefs)
    {
        foreach (var stored in new[] { AlertRefValue, JobRefValue })
        {
            Assert.DoesNotContain(stored.ToString("D", CultureInfo.InvariantCulture), rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(stored.ToString("N", CultureInfo.InvariantCulture), rendered, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var expected in expectedRefs)
        {
            Assert.Contains(expected, rendered, StringComparison.Ordinal);
        }
    }

    private static AlertNotification Notification() =>
        new(
            AlertRef: TestAlertRef,
            JobNamespace: "orders",
            JobRef: TestJobRef,
            Severity: AlertSeverityCode.Error,
            Kind: AlertKindCode.FinalFailure,
            Title: "Job 'charge' failed",
            Message: "Terminal failure: boom.",
            RunbookUrl: "https://runbook.example/charge",
            OccurrenceCount: 3,
            CreatedAtUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        );

    private static AlertTarget Target() =>
        new(
            ChannelName: "default",
            TransportKind: LogAlertTransport.Kind,
            Endpoint: "",
            ConfigFormatId: 0,
            Config: ReadOnlyMemory<byte>.Empty
        );

    private sealed class RecordingLogger : ILogger<LogAlertTransport>
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
        )
        {
            var properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            Entries.Add(new Entry(logLevel, formatter(state, exception), [.. properties]));
        }
    }

    private sealed record Entry(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Properties);
}

using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// <see cref="MeterConsistencyCheck"/>: a namespace's shared meters can still split by design (a
/// manifest move or a startup/retune race), so this warns instead of rejecting, and only about a
/// meter whose participants actually carry different effective rates.
/// </summary>
public sealed class MeterConsistencyCheckTests
{
    private const string Namespace = "billing";

    private static StoredDefinitionContract Definition(string name, string? rateKey, string? effectiveRate) =>
        new(
            Name: name,
            ManifestGenerationAtUtc: new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc),
            Contract: new DefinitionContract("Input", null, 0, "none", 0, "none"),
            Id: name.GetHashCode(StringComparison.Ordinal),
            DefinitionHash: "hash",
            Status: JobDefinitionStatusCode.Active,
            ModifiedAtUtc: new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc),
            Effective: new EffectiveJobPolicy(
                JobPriorityCode.Normal,
                MaxAttempts: 15,
                ConcurrencyLimit: null,
                RateLimit: effectiveRate,
                RateKey: rateKey,
                Backoff: "1m..8h x2",
                ExecutionTimeoutSeconds: 30,
                DeadlineSeconds: 0,
                DeadlineBehavior: DeadlineBehaviorCode.Strict,
                JobRetentionSeconds: 3600,
                AuditLevel: JobAuditLevelCode.Audit,
                AlertProfile: AlertProfileCode.OnFailure,
                AlertChannelName: null,
                RunbookUrl: null
            )
        );

    [Fact]
    public void A_split_meter_logs_exactly_one_warning_naming_both_participants()
    {
        var logger = new RecordingLogger();

        MeterConsistencyCheck.Check(Namespace, [Definition("charge", "stripe", "10/s"), Definition("invoice", "stripe", "5/s")], logger);

        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("charge", record.Message, StringComparison.Ordinal);
        Assert.Contains("invoice", record.Message, StringComparison.Ordinal);
        Assert.Contains("stripe", record.Message, StringComparison.Ordinal);
        Assert.Contains(Namespace, record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_consistent_meter_logs_nothing()
    {
        var logger = new RecordingLogger();

        MeterConsistencyCheck.Check(Namespace, [Definition("charge", "stripe", "10/s"), Definition("invoice", "stripe", "10/s")], logger);

        Assert.Empty(logger.Records);
    }

    [Fact]
    public void A_definition_without_a_rate_on_a_shared_key_name_is_not_a_participant()
    {
        var logger = new RecordingLogger();

        // "stripe" names the meter "charge" spends from but declares no rate of its own: alone with
        // "charge", the meter has exactly one participant, so nothing can disagree.
        MeterConsistencyCheck.Check(Namespace, [Definition("stripe", null, null), Definition("charge", "stripe", "10/s")], logger);

        Assert.Empty(logger.Records);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Records.Add((logLevel, formatter(state, exception)));
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acta.Tests.Runtime;

public class ContractDriftPolicyTests
{
    private static readonly ContractDrift Drift = new(
        "job",
        new DefinitionContract("A", null, 1, "json", 0, "none"),
        new DefinitionContract("B", null, 1, "json", 0, "none")
    );

    [Fact]
    public void Fail_mode_with_drift_throws()
    {
        Assert.Throws<PayloadContractDriftException>(() =>
            ContractDriftPolicy.Apply(PayloadContractDriftMode.Fail, [Drift], "ns", NullLogger.Instance)
        );
    }

    [Fact]
    public void Warn_mode_with_drift_does_not_throw()
    {
        ContractDriftPolicy.Apply(PayloadContractDriftMode.Warn, [Drift], "ns", NullLogger.Instance);
    }

    [Fact]
    public void Fail_mode_with_no_drift_does_not_throw()
    {
        ContractDriftPolicy.Apply(PayloadContractDriftMode.Fail, [], "ns", NullLogger.Instance);
    }

    // The warning names each part of the contract that changed, with its stored and its new value, and no other part.
    [Theory]
    [InlineData("Shipping.ShipOrder", null, "json", "none", "input type ShipOrder -> Shipping.ShipOrder")]
    [InlineData("ShipOrder", "Shipping.Receipt", "json", "json", "result type none -> Shipping.Receipt, result format none -> json")]
    [InlineData("ShipOrder", null, "msgpack", "none", "input format json -> msgpack")]
    public void Warn_mode_names_only_what_changed(
        string inputType,
        string? resultType,
        string inputFormat,
        string resultFormat,
        string expected
    )
    {
        var stored = new DefinitionContract("ShipOrder", null, 1, "json", 0, "none");
        var incoming = new DefinitionContract(
            inputType,
            resultType,
            inputFormat == "json" ? (byte)1 : (byte)3,
            inputFormat,
            resultFormat == "none" ? (byte)0 : (byte)1,
            resultFormat
        );
        var logger = new RecordingLogger();

        ContractDriftPolicy.Apply(PayloadContractDriftMode.Warn, [new ContractDrift("ship-order", stored, incoming)], "shipping", logger);

        Assert.Equal(expected, Assert.Single(logger.Details));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string?> Details { get; } = [];

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
            Details.Add(properties.Single(p => p.Key == "ActaDetail").Value as string);
        }
    }
}

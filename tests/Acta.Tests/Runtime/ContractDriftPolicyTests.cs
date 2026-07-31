using Acta.Runtime.Modules.Execution.Definitions;
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
}

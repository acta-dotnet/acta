using System.Collections.Immutable;
using Xunit;

namespace Acta.Tests.Runtime;

public class ContractDriftDetectorTests
{
    private static JobDescriptor Desc(string name, Type inputType) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: inputType,
            ResultType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            ResultPayloadFormat: null,
            Priority: default,
            MaxAttempts: 1,
            EventRecording: default,
            AlertProfile: default,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeResult: null
        );

    private static StoredDefinitionContract Stored(string name, Type inputType) =>
        new(
            name,
            JobDefinitionRegistration.ContractOf(Desc(name, inputType)),
            DefinitionId: 0,
            DefinitionHash: "",
            Status: DefinitionStatusCode.Active,
            Effective: null! // drift detection ignores effective policy
        );

    [Fact]
    public void Contract_change_is_reported()
    {
        var stored = new[] { Stored("job", typeof(int)) };
        var incoming = ImmutableArray.Create(Desc("job", typeof(string)));

        var drifts = ContractDriftDetector.Detect(incoming, stored);

        var drift = Assert.Single(drifts);
        Assert.Equal("job", drift.JobName);
    }

    [Fact]
    public void Unchanged_contract_is_not_reported()
    {
        var stored = new[] { Stored("job", typeof(int)) };
        var incoming = ImmutableArray.Create(Desc("job", typeof(int)));

        var drifts = ContractDriftDetector.Detect(incoming, stored);

        Assert.Empty(drifts);
    }

    [Fact]
    public void New_definition_with_no_stored_row_is_not_drift()
    {
        var incoming = ImmutableArray.Create(Desc("brand-new", typeof(string)));

        var drifts = ContractDriftDetector.Detect(incoming, Array.Empty<StoredDefinitionContract>());

        Assert.Empty(drifts);
    }
}

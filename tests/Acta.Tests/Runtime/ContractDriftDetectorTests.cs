using System.Collections.Immutable;
using Acta.Modules.Execution.Definitions;
using Acta.Payloads;
using Xunit;

namespace Acta.Tests.Runtime;

public class ContractDriftDetectorTests
{
    private static readonly DateTime Older = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static JobDescriptor Desc(string name, Type inputType) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: inputType,
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            InvocationKind: default,
            RequiresJobContextParameter: false,
            RequiresCancellationToken: false,
            Priority: default,
            MaxAttempts: 1,
            AuditLevel: default,
            AlertProfile: default,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );

    private static StoredDefinitionContract Stored(string name, DateTime gen, Type inputType) =>
        new(
            name,
            gen,
            DefinitionsService.ContractOf(Desc(name, inputType)),
            Id: 0,
            DefinitionHash: "",
            Status: JobDefinitionStatusCode.Active,
            ModifiedAtUtc: gen,
            Effective: null! // drift detection ignores effective policy
        );

    [Fact]
    public void Eligible_contract_change_is_reported()
    {
        var stored = new[] { Stored("job", Older, typeof(int)) };
        var incoming = ImmutableArray.Create(Desc("job", typeof(string)));

        var drifts = ContractDriftDetector.Detect(Newer, incoming, stored);

        var drift = Assert.Single(drifts);
        Assert.Equal("job", drift.JobName);
    }

    [Fact]
    public void Unchanged_contract_is_not_reported()
    {
        var stored = new[] { Stored("job", Older, typeof(int)) };
        var incoming = ImmutableArray.Create(Desc("job", typeof(int)));

        var drifts = ContractDriftDetector.Detect(Newer, incoming, stored);

        Assert.Empty(drifts);
    }

    [Fact]
    public void Older_incoming_is_not_reported_even_when_contract_differs()
    {
        var stored = new[] { Stored("job", Newer, typeof(int)) };
        var incoming = ImmutableArray.Create(Desc("job", typeof(string)));

        var drifts = ContractDriftDetector.Detect(Older, incoming, stored);

        Assert.Empty(drifts);
    }

    [Fact]
    public void New_definition_with_no_stored_row_is_not_drift()
    {
        var incoming = ImmutableArray.Create(Desc("brand-new", typeof(string)));

        var drifts = ContractDriftDetector.Detect(Newer, incoming, Array.Empty<StoredDefinitionContract>());

        Assert.Empty(drifts);
    }
}

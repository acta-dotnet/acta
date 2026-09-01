using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Boundary validation in <see cref="DefinitionsService.UpdateOverridesAsync"/> for the override
/// values that would otherwise fail later and worse: an execution timeout past CancelAfter's
/// ceiling crashes the attempt after the claim, a non-ASCII or over-length runbook URL fails or
/// mangles per provider, and free-form display text truncates to its column instead of surfacing
/// a provider write error.
/// </summary>
public sealed class DefinitionOverrideValidationTests
{
    private const string Namespace = "billing";

    private static JobDefinitionListItem Row(int id, string name) =>
        new(
            DefinitionId: id,
            JobNamespace: Namespace,
            JobName: name,
            Status: JobDefinitionStatusCode.Active,
            InputTypeName: "Input",
            OutputTypeName: null,
            PriorityOverride: null,
            PriorityEffective: JobPriorityCode.Normal,
            MaxAttemptsOverride: null,
            MaxAttemptsEffective: 3,
            ModifiedAtUtc: new DateTime(2026, 8, 15, 8, 0, 0, DateTimeKind.Utc),
            Version: 1
        );

    private static Task<DefinitionControlResult> UpdateAsync(DefinitionsService service, JobDefinitionPolicyOverrides overrides) =>
        service
            .UpdateOverridesAsync(Namespace, "invoice", expectedVersion: 1, overrides, actorKey: "tester", reasonMessage: null, TestContext.Current.CancellationToken)
            .AsTask();

    private static (DefinitionsService Service, RecordingDefinitionStore Store) Build()
    {
        var store = new RecordingDefinitionStore([Row(1, "invoice")]);
        return (new DefinitionsService(store), store);
    }

    [Fact]
    public async Task An_execution_timeout_past_the_CancelAfter_ceiling_is_rejected()
    {
        var (service, store) = Build();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            UpdateAsync(service, new JobDefinitionPolicyOverrides(ExecutionTimeoutSeconds: JobDefinitionRegistration.MaxExecutionTimeoutSeconds + 1))
        );
        Assert.Empty(store.OverrideWrites);

        // The ceiling itself is a legal value.
        var outcome = await UpdateAsync(service, new JobDefinitionPolicyOverrides(ExecutionTimeoutSeconds: JobDefinitionRegistration.MaxExecutionTimeoutSeconds));
        Assert.Equal(ControlAction.Applied, outcome.Action);
    }

    [Theory]
    [InlineData("https://rb/\u00e9")] // non-ASCII in an ASCII column
    [InlineData("https://rb/\U0001F600")] // astral characters are equally out of range
    public async Task A_non_ascii_runbook_url_is_rejected(string url)
    {
        var (service, store) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() => UpdateAsync(service, new JobDefinitionPolicyOverrides(RunbookUrl: url)));
        Assert.Empty(store.OverrideWrites);
    }

    [Fact]
    public async Task An_over_length_runbook_url_is_rejected_not_cut()
    {
        var (service, store) = Build();

        var url = "https://rb/" + new string('a', 600);
        await Assert.ThrowsAsync<ArgumentException>(() => UpdateAsync(service, new JobDefinitionPolicyOverrides(RunbookUrl: url)));
        Assert.Empty(store.OverrideWrites);
    }

    [Fact]
    public async Task Display_text_overrides_truncate_to_their_columns()
    {
        var (service, store) = Build();

        var outcome = await UpdateAsync(
            service,
            new JobDefinitionPolicyOverrides(DisplayName: new string('d', 200), Description: new string('x', 600))
        );

        Assert.Equal(ControlAction.Applied, outcome.Action);
        var written = Assert.Single(store.OverrideWrites).Overrides;
        Assert.Equal(128, written.DisplayName!.Length);
        Assert.Equal(512, written.Description!.Length);
    }

    /// <summary>Grid read returns the catalog regardless of filter; override writes are recorded.</summary>
    private sealed class RecordingDefinitionStore(IReadOnlyList<JobDefinitionListItem> rows) : IDefinitionStore
    {
        public List<SetDefinitionOverridesCommand> OverrideWrites { get; } = [];

        public Task<DefinitionPage> ListDefinitionsAsync(DefinitionPageRequest request, CancellationToken ct) =>
            Task.FromResult(new DefinitionPage(rows, rows.Count));

        public Task<bool> DefinitionHasSchedulesAsync(int definitionId, CancellationToken ct) => Task.FromResult(false);

        public Task<DefinitionOverrideOutcome> SetDefinitionOverridesAsync(SetDefinitionOverridesCommand command, CancellationToken ct)
        {
            OverrideWrites.Add(command);
            return Task.FromResult(new DefinitionOverrideOutcome(DefinitionOverrideAction.Applied));
        }

        public ValueTask<JobDefinitionDetail?> GetDefinitionAsync(int definitionId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredDefinitionContract>> GetDefinitionContractsAsync(int namespaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, int>> RegisterDefinitionsAsync(RegisterDefinitionsCommand command, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}

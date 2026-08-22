using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Natural-key resolution in <see cref="DefinitionsService"/>: the catalog id behind
/// <c>(jobNamespace, jobName)</c> comes from the grid read, whose name filter is a SQL LIKE pattern.
/// The service passes the bare validated name (no <c>%</c>: <see cref="ListDefinitionsQuery"/>'s
/// wrapping lives in <c>ListAsync</c>, and the validator rejects wildcards), so the store already
/// matches exactly - and then the service filters the returned rows on ordinal name equality anyway.
/// These tests pin that second, independent defense: the store fake here deliberately over-matches,
/// returning every sibling for any query, so only the ordinal filter can keep the answer right. A
/// prefix or suffix sibling must never resolve in another's place - picking the wrong row would edit
/// or read the wrong definition.
/// </summary>
public sealed class DefinitionKeyResolutionTests
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

    // invoice sits between a suffix sibling (invoice-retry) and a prefix sibling (bulk-invoice), so a
    // resolver that over- or under-matched in either direction lands on the wrong row.
    private static readonly JobDefinitionListItem[] Catalog = [Row(1, "invoice"), Row(2, "invoice-retry"), Row(3, "bulk-invoice")];

    [Theory]
    [InlineData("invoice", 1)]
    [InlineData("invoice-retry", 2)]
    [InlineData("bulk-invoice", 3)]
    public async Task A_name_resolves_to_its_own_definition_even_when_the_store_over_matches(string jobName, int expectedId)
    {
        var service = new DefinitionsService(new OverMatchingDefinitionStore(Catalog));

        var definition = await service.GetAsync(Namespace, jobName, TestContext.Current.CancellationToken);

        Assert.NotNull(definition);
        Assert.Equal(expectedId, definition!.DefinitionId);
    }

    [Fact]
    public async Task A_name_no_definition_carries_resolves_to_null_rather_than_a_sibling()
    {
        var service = new DefinitionsService(new OverMatchingDefinitionStore(Catalog));
        var ct = TestContext.Current.CancellationToken;

        // "invoic" is a prefix of a registered name and "invoice-retry-2" extends one; neither exists.
        Assert.Null(await service.GetAsync(Namespace, "invoic", ct));
        Assert.Null(await service.GetAsync(Namespace, "invoice-retry-2", ct));
    }

    [Fact]
    public async Task An_override_write_addressed_by_a_sibling_prefix_is_not_found_and_writes_nothing()
    {
        var store = new OverMatchingDefinitionStore(Catalog);
        var service = new DefinitionsService(store);

        var outcome = await service.UpdateOverridesAsync(
            Namespace,
            "invoic",
            expectedVersion: 1,
            new JobDefinitionPolicyOverrides(MaxAttempts: 9),
            actorKey: "tester",
            reasonMessage: "typo",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ControlAction.NotFound, outcome.Action);
        Assert.Empty(store.OverrideWrites);
    }

    /// <summary>
    /// A store whose grid read ignores the name filter entirely and returns the whole catalog. Nothing
    /// but the service's own ordinal match can produce the right row, which is exactly what is pinned.
    /// </summary>
    private sealed class OverMatchingDefinitionStore(IReadOnlyList<JobDefinitionListItem> rows) : IDefinitionStore
    {
        public List<SetDefinitionOverridesCommand> OverrideWrites { get; } = [];

        public Task<DefinitionPage> ListDefinitionsAsync(DefinitionPageRequest request, CancellationToken ct) =>
            Task.FromResult(new DefinitionPage(rows, rows.Count));

        public ValueTask<JobDefinitionDetail?> GetDefinitionAsync(int definitionId, CancellationToken ct)
        {
            var row = rows.FirstOrDefault(r => r.DefinitionId == definitionId);
            return ValueTask.FromResult(row is null ? null : Detail(row));
        }

        public Task<DefinitionOverrideOutcome> SetDefinitionOverridesAsync(SetDefinitionOverridesCommand command, CancellationToken ct)
        {
            OverrideWrites.Add(command);
            return Task.FromResult(new DefinitionOverrideOutcome(DefinitionOverrideAction.Applied));
        }

        public Task<IReadOnlyList<StoredDefinitionContract>> GetDefinitionContractsAsync(int namespaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, int>> RegisterDefinitionsAsync(RegisterDefinitionsCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        private static JobDefinitionDetail Detail(JobDefinitionListItem row) =>
            new(
                DefinitionId: row.DefinitionId,
                JobNamespace: row.JobNamespace,
                JobName: row.JobName,
                Status: row.Status,
                DefinitionHash: "hash",
                ManifestGenerationAtUtc: row.ModifiedAtUtc,
                InputTypeName: row.InputTypeName,
                InputFormatId: 1,
                InputFormatName: "json",
                OutputTypeName: null,
                OutputFormatId: 0,
                OutputFormatName: "none",
                Priority: JobPriorityCode.Normal,
                PriorityOverride: null,
                PriorityEffective: JobPriorityCode.Normal,
                MaxAttempts: 3,
                MaxAttemptsOverride: null,
                MaxAttemptsEffective: 3,
                Backoff: "1s..1m x2",
                BackoffOverride: null,
                BackoffEffective: "1s..1m x2",
                ExecutionTimeoutSeconds: 60,
                ExecutionTimeoutSecondsOverride: null,
                ExecutionTimeoutSecondsEffective: 60,
                DeadlineSeconds: 0,
                DeadlineSecondsOverride: null,
                DeadlineSecondsEffective: 0,
                DeadlineBehavior: DeadlineBehaviorCode.Strict,
                DeadlineBehaviorOverride: null,
                DeadlineBehaviorEffective: DeadlineBehaviorCode.Strict,
                JobRetentionSeconds: 0,
                JobRetentionSecondsOverride: null,
                JobRetentionSecondsEffective: 0,
                AuditLevel: JobAuditLevelCode.Audit,
                AuditLevelOverride: null,
                AuditLevelEffective: JobAuditLevelCode.Audit,
                AlertProfile: AlertProfileCode.None,
                AlertProfileOverride: null,
                AlertProfileEffective: AlertProfileCode.None,
                AlertChannelName: null,
                AlertChannelNameOverride: null,
                AlertChannelNameEffective: null,
                RunbookUrl: null,
                RunbookUrlOverride: null,
                RunbookUrlEffective: null,
                DisplayName: null,
                DisplayNameOverride: null,
                DisplayNameEffective: null,
                Description: null,
                DescriptionOverride: null,
                DescriptionEffective: null,
                CreatedAtUtc: row.ModifiedAtUtc,
                ModifiedAtUtc: row.ModifiedAtUtc,
                Version: row.Version
            );
    }
}

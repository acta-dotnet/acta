using System.Data.Common;
using System.Globalization;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Acta.Runtime.Modules.Execution.Definitions;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="IDefinitionStore"/> over <see cref="IDbSession"/>: inline reads, the
/// whole-namespace registration upsert via the provider-native definition batch, and the operator
/// override write. Provider mechanics (routine vs inline, bulk-shape binding) live behind the session
/// and the dialect.
/// </summary>
internal sealed class RelationalDefinitionStore(IDbSession session, ISqlDialect dialect) : IDefinitionStore
{
    public Task<IReadOnlyList<StoredDefinitionContract>> GetDefinitionContractsAsync(int namespaceId, CancellationToken ct) =>
        session.QueryAsync(
            "Sql/Execution/Definitions/GetDefinitionContracts.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.NamespaceId, namespaceId)),
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<StoredDefinitionContractRow>();
                var contracts = new List<StoredDefinitionContract>();
                while (await reader.ReadAsync(token))
                {
                    contracts.Add(read(reader).ToContract());
                }

                return (IReadOnlyList<StoredDefinitionContract>)contracts;
            },
            ct
        );

    public async ValueTask<JobDefinitionDetail?> GetDefinitionAsync(int definitionId, CancellationToken ct) =>
        await session.QueryAsync<JobDefinitionDetail?>(
            "Sql/Execution/Definitions/GetJobDefinition.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.Id, definitionId)),
            async (reader, token) =>
                await reader.ReadAsync(token) ? DbProjectionResolver.Resolve<JobDefinitionDetailRow>()(reader).ToDetail() : null,
            ct
        );

    public Task<DefinitionPage> ListDefinitionsAsync(DefinitionPageRequest request, CancellationToken ct) =>
        session.QueryAsync(
            "Sql/Execution/Definitions/ListJobDefinitions.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, request.JobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NameSearchFilter, request.NameSearch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.StatusCode, request.Status));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, request.TagFiltersJson));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorNamespaceName, request.CursorNamespaceName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorJobName, request.CursorJobName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorIntId, request.CursorId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, request.Take));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, request.IncludeTotal ? true : (bool?)null));
            },
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<JobDefinitionListRow>();
                var rows = new List<JobDefinitionListItem>(request.Take);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader).ToItem());
                }

                long? total = null;
                if (await reader.NextResultAsync(token) && await reader.ReadAsync(token) && !reader.IsDBNull(0))
                {
                    total = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                }

                return new DefinitionPage(rows, total);
            },
            ct
        );

    public async Task<IReadOnlyDictionary<string, int>> RegisterDefinitionsAsync(RegisterDefinitionsCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("Execution", "Definitions/RegisterJobDefinitions"),
            cmd =>
                dialect.BindRegisterJobDefinitions(cmd, command.NamespaceId, command.ManifestGenerationUtc, command.Rows, session.Schema),
            DbProjectionResolver.Resolve<RegisteredJobDefinition>(),
            ct
        );

        var map = new Dictionary<string, int>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            map[row.Name] = row.Id;
        }

        return map;
    }

    public async Task<DefinitionOverrideOutcome> SetDefinitionOverridesAsync(SetDefinitionOverridesCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("Execution", "Definitions/SetJobDefinitionOverrides"),
            cmd => AddOverrideParameters(cmd, command),
            DbProjectionResolver.Resolve<DefinitionOverrideOutcome>(),
            ct
        );

        return rows.Count > 0
            ? rows[^1]
            : throw new InvalidOperationException(
                "set_job_definition_overrides returned no rows; it must return exactly one (action) row."
            );
    }

    private void AddOverrideParameters(DbCommand cmd, SetDefinitionOverridesCommand command)
    {
        var o = command.Overrides;
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.Id, command.DefinitionId));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.Version, command.ExpectedVersion));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.PriorityCodeOverride, o.Priority));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.MaxAttemptsOverride, o.MaxAttempts));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.BackoffOverride, o.Backoff));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.ExecutionTimeoutSecondsOverride, o.ExecutionTimeoutSeconds));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.DeadlineSecondsOverride, o.DeadlineSeconds));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.DeadlineBehaviorCodeOverride, o.DeadlineBehavior));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.RetentionSecondsOverride, o.JobRetentionSeconds));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.AuditLevelCodeOverride, o.AuditLevel));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.AlertProfileCodeOverride, o.AlertProfile));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.AlertChannelNameOverride, o.AlertChannelName));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.RunbookUrlOverride, o.RunbookUrl));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.DisplayNameOverride, o.DisplayName));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.DescriptionOverride, o.Description));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, command.Actor.ActorCode));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, command.Actor.ActorKey));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonCode, JobEventReasonCode.JobControlManual));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, command.ReasonMessage));
    }
}

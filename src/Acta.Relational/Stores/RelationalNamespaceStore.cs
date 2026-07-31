using System.Globalization;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Namespaces;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="INamespaceStore"/> over <see cref="IDbSession"/>. One implementation for
/// every SQL provider: the inline name list and the operator control verbs are written once, and provider
/// differences live behind the session (routine vs inline, result-set selection) and the dialect (parameter creation).
/// </summary>
internal sealed class RelationalNamespaceStore(IDbSession session, ISqlDialect dialect) : INamespaceStore
{
    public Task<NamespacePage> ListNamespacesAsync(NamespacePageRequest request, CancellationToken ct) =>
        session.QueryAsync(
            "Sql/Execution/Namespaces/ListNamespaces.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NameSearchFilter, request.NameSearch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, request.TagFiltersJson));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorNamespaceName, request.CursorName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, request.Take));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, request.IncludeTotal ? true : (bool?)null));
            },
            async (reader, token) =>
            {
                var rows = new List<string>(request.Take);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(reader.GetString(0));
                }

                long? total = null;
                if (await reader.NextResultAsync(token) && await reader.ReadAsync(token) && !reader.IsDBNull(0))
                {
                    total = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                }

                return new NamespacePage(rows, total);
            },
            ct
        );

    public Task<NamespaceItemPage> ListNamespaceItemsAsync(NamespacePageRequest request, CancellationToken ct) =>
        session.QueryAsync(
            "Sql/Execution/Namespaces/ListNamespaceItems.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NameSearchFilter, request.NameSearch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.StatusCode, request.Status));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, request.TagFiltersJson));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorNamespaceName, request.CursorName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, request.Take));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, request.IncludeTotal ? true : (bool?)null));
            },
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<NamespaceListRow>();
                var rows = new List<NamespaceListItem>(request.Take);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader).ToItem());
                }

                long? total = null;
                if (await reader.NextResultAsync(token) && await reader.ReadAsync(token) && !reader.IsDBNull(0))
                {
                    total = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                }

                return new NamespaceItemPage(rows, total);
            },
            ct
        );

    public Task<AdminControlOutcome> SuspendNamespaceAsync(NamespaceControlCommand command, CancellationToken ct) =>
        ControlAsync("Namespaces/SuspendNamespace", command, ct);

    public Task<AdminControlOutcome> ResumeNamespaceAsync(NamespaceControlCommand command, CancellationToken ct) =>
        ControlAsync("Namespaces/ResumeNamespace", command, ct);

    public async Task<AdminControlOutcome> UpdateNamespaceMetadataAsync(UpdateNamespaceMetadataCommand command, CancellationToken ct) =>
        await session.ExecuteSingleAsync(
            new StoreCommand("Execution", "Namespaces/UpdateNamespaceMetadata"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.OwnerTeam, command.OwnerTeam));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.Description, command.Description));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedRowVersion, command.ExpectedVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, command.Actor.ActorCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, command.Actor.ActorKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, command.ReasonMessage));
            },
            DbProjectionResolver.Resolve<AdminControlOutcome>(),
            ct
        )
        ?? throw new InvalidOperationException(
            "Control command 'UpdateNamespaceMetadata' returned no rows; it must return exactly one (action, version) row."
        );

    private async Task<AdminControlOutcome> ControlAsync(string operation, NamespaceControlCommand command, CancellationToken ct) =>
        await session.ExecuteSingleAsync(
            new StoreCommand("Execution", operation),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, command.Key));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, command.Actor.ActorCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, command.Actor.ActorKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, command.ReasonMessage));
            },
            DbProjectionResolver.Resolve<AdminControlOutcome>(),
            ct
        )
        ?? throw new InvalidOperationException(
            $"Control command '{operation}' returned no rows; it must return exactly one (action, version) row."
        );
}

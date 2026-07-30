using System.Globalization;
using Acta.Kernel;
using Acta.Modules.Execution.Tenants;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="ITenantStore"/> over <see cref="IDbSession"/>. One implementation for
/// every SQL provider: the idempotent registration upsert, the inline key-ordered list, and the operator
/// control verbs are written once, and provider differences live behind the session (routine vs inline,
/// result-set selection) and the dialect (parameter creation).
/// </summary>
internal sealed class RelationalTenantStore(IDbSession session, ISqlDialect dialect) : ITenantStore
{
    public async Task<int> RegisterTenantAsync(RegisterTenantCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("Execution", "Tenants/RegisterTenant"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.TenantKey, command.TenantKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.DisplayName, command.DisplayName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.Description, command.Description));
            },
            reader => Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            ct
        );

        return rows.Count > 0 ? rows[^1] : throw new InvalidOperationException("register_tenant returned no tenant id row.");
    }

    public Task<TenantListItem?> GetTenantAsync(TenantPointLookup lookup, CancellationToken ct) =>
        session.QueryAsync(
            "Sql/Execution/Tenants/GetTenant.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.TenantKey, lookup.TenantKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.TenantId, lookup.TenantId));
            },
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<TenantListRow>();
                return await reader.ReadAsync(token) ? read(reader).ToItem() : null;
            },
            ct
        );

    public Task<TenantPage> ListTenantsAsync(TenantPageRequest request, CancellationToken ct) =>
        session.QueryAsync(
            "Sql/Execution/Tenants/ListTenants.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TenantSearch, request.SearchPattern));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.StatusCode, request.Status));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, request.TagFiltersJson));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorTenantKey, request.CursorTenantKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, request.Take));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, request.IncludeTotal ? true : (bool?)null));
            },
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<TenantListRow>();
                var rows = new List<TenantListItem>(request.Take);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader).ToItem());
                }

                long? total = null;
                if (await reader.NextResultAsync(token) && await reader.ReadAsync(token) && !reader.IsDBNull(0))
                {
                    total = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                }

                return new TenantPage(rows, total);
            },
            ct
        );

    public Task<AdminControlOutcome> SuspendTenantAsync(TenantControlCommand command, CancellationToken ct) =>
        ControlAsync("Tenants/SuspendTenant", command, ct);

    public Task<AdminControlOutcome> ResumeTenantAsync(TenantControlCommand command, CancellationToken ct) =>
        ControlAsync("Tenants/ResumeTenant", command, ct);

    public async Task<AdminControlOutcome> UpdateTenantMetadataAsync(UpdateTenantMetadataCommand command, CancellationToken ct) =>
        await session.ExecuteSingleAsync(
            new StoreCommand("Execution", "Tenants/UpdateTenantMetadata"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.TenantKey, command.TenantKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.DisplayName, command.DisplayName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.Description, command.Description));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedRowVersion, command.ExpectedVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, command.Actor.ActorCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, command.Actor.ActorKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, command.ReasonMessage));
            },
            DbProjectionResolver.Resolve<AdminControlOutcome>(),
            ct
        )
        ?? throw new InvalidOperationException(
            "Control command 'UpdateTenantMetadata' returned no rows; it must return exactly one (action, version) row."
        );

    private async Task<AdminControlOutcome> ControlAsync(string operation, TenantControlCommand command, CancellationToken ct) =>
        await session.ExecuteSingleAsync(
            new StoreCommand("Execution", operation),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.TenantKey, command.Key));
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

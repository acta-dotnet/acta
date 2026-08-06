using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Settings;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="ISettingStore"/> over <see cref="IDbSession"/>: the inline global
/// point read and the upsert write with its setting.updated evidence event. One implementation for
/// every SQL provider; differences live behind the session (routine vs inline) and the dialect.
/// </summary>
internal sealed class RelationalSettingStore(IDbSession session, ISqlDialect dialect) : ISettingStore
{
    public Task<SettingRow?> GetSettingAsync(SettingPointLookup lookup, CancellationToken ct) =>
        session.QueryAsync(
            "Sql/Execution/Settings/GetSetting.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Setting.Name, lookup.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ScopeNamespaceName, lookup.NamespaceName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ScopeJobName, lookup.JobName));
            },
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<SettingRow>();
                return await reader.ReadAsync(token) ? read(reader) : null;
            },
            ct
        );

    public async Task<AdminControlOutcome> SetSettingAsync(SetSettingCommand command, CancellationToken ct) =>
        await session.ExecuteSingleAsync(
            new StoreCommand("Execution", "Settings/SetSetting"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Setting.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Setting.ValueFormatId, command.ValueFormatId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Setting.Value, command.Value));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Setting.Description, command.Description));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ScopeNamespaceName, command.NamespaceName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ScopeJobName, command.JobName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, command.Actor.ActorCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, command.Actor.ActorKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, command.ReasonMessage));
            },
            DbProjectionResolver.Resolve<AdminControlOutcome>(),
            ct
        )
        ?? throw new InvalidOperationException(
            "Control command 'SetSetting' returned no rows; it must return exactly one (action, version) row."
        );
}

using System.Globalization;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Acta.Runtime.Modules.Operations.Events;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="IEventStore"/> over <see cref="IDbSession"/>: the keyset page and the
/// opt-in total come back as two result sets of one command over the provider-owned SQL resource.
/// </summary>
internal sealed class RelationalEventStore(IDbSession session, ISqlDialect dialect) : IEventStore
{
    public Task<EventPage> ListEventsAsync(EventPageRequest request, CancellationToken ct) =>
        session.QueryAsync(
            "Sql/Operations/Events/ListJobEvents.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.JobId, request.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.LineageRootId, request.LineageRootId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, request.JobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.EventCode, request.EventCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.DefinitionId, request.JobDefinitionId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.TenantId, request.TenantId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TenantKeyFilter, request.TenantKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.WorkerId, request.WorkerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, request.ActorCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonCode, request.ReasonCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.EventCreatedFromUtc, request.CreatedFromUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.EventCreatedToUtc, request.CreatedToUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, request.TagFiltersJson));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorCreatedAtUtc, request.CursorCreatedAtUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorId, request.CursorId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, request.Take));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, request.IncludeTotal ? true : (bool?)null));
            },
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<EventListProjectionRow>();
                var rows = new List<JobEventListItem>(request.Take);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader).ToListItem());
                }

                long? total = null;
                if (await reader.NextResultAsync(token) && await reader.ReadAsync(token) && !reader.IsDBNull(0))
                {
                    total = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                }

                return new EventPage(rows, total);
            },
            ct
        );
}

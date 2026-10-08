using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The Outbox routes. Sources compose cross-peer from each namespace's <c>sys.outbox</c> slot result, so any host with
/// ledger access answers them; the quarantine listing opens the producer's source database and answers only where the
/// namespace's relay is registered. Requeue and discard park a command the next relay pass applies.
/// </summary>
internal static class OutboxEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder? controls, ActaEndpointOptions options)
    {
        reads.MapList(
            "/outbox/sources",
            "List outbox relay sources with backlog and quarantine totals.",
            DashboardJsonContext.Default.ListOutboxSourcesResponse,
            static query => new ListOutboxSourcesQuery(
                JobNamespace: query.Text("jobNamespace"),
                PageSize: query.Int("pageSize"),
                Cursor: query.Text("cursor")
            ),
            static (call, query) => call.Operations.Outbox.ListSourcesAsync(query, call.Ct),
            [
                new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Scope to one relay source's namespace; omit for all."),
                .. QueryParameterDocExtensions.PagingCore,
            ]
        );
        reads
            .MapList(
                "/outbox/{jobNamespace}/quarantined",
                "List a relay source's quarantined staging rows.",
                DashboardJsonContext.Default.ListOutboxQuarantinedResponse,
                static query => new ListOutboxQuarantinedQuery(
                    query.Route("jobNamespace"),
                    PageSize: query.Int("pageSize"),
                    Cursor: query.Text("cursor"),
                    IncludeTotal: query.Bool("includeTotal") ?? false
                ),
                static async (call, query) =>
                {
                    try
                    {
                        return await call.Operations.Outbox.ListQuarantinedAsync(query, call.Ct);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // The source is not registered on this host: a placement constraint, not a fault.
                        throw new RouteConflictException("Quarantine listing unavailable on this host.", ex.Message);
                    }
                },
                [.. QueryParameterDocExtensions.PagingCore, .. QueryParameterDocExtensions.IncludeTotal]
            )
            .ProducesProblem(StatusCodes.Status409Conflict);
        if (controls is null)
        {
            return;
        }

        controls.MapCommand(
            "/outbox/{jobNamespace}/requeue",
            "Return quarantined outbox rows to pending; the next relay pass applies it.",
            options,
            DashboardJsonContext.Default.RequeueOutboxRequest,
            "Invalid outbox command.",
            ControlStatus.Outbox<RequeueOutboxResponse>(),
            Namespace,
            static (call, jobNamespace) =>
                call.Operations.Outbox.RequeueAsync(jobNamespace, call.Body?.OutboxIds, call.ReasonMessage, call.ActorKey, call.Ct)
        );
        controls.MapCommand(
            "/outbox/{jobNamespace}/discard",
            "Delete quarantined outbox rows, keeping the ids as audit evidence.",
            options,
            DashboardJsonContext.Default.DiscardOutboxRequest,
            "Invalid outbox command.",
            ControlStatus.Outbox<DiscardOutboxResponse>(),
            Namespace,
            static (call, jobNamespace) =>
                call.Operations.Outbox.DiscardAsync(jobNamespace, call.Body?.OutboxIds, call.ReasonMessage, call.ActorKey, call.Ct)
        );
    }

    private static string Namespace(HttpContext http) => RouteKeys.Value(http, "jobNamespace");
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// The Alerts routes: the list, one alert, its tags, and the acknowledge and resolve verbs. An alert is addressed by
/// its public ref; the verbs are idempotent, so they answer 200 or 404 and never 409.
/// </summary>
internal static class AlertsEndpoints
{
    public static void Map(RouteGroupBuilder reads, RouteGroupBuilder? controls, ActaEndpointOptions options)
    {
        reads
            .MapList(
                "/alerts",
                "List alerts.",
                DashboardJsonContext.Default.ListAlertsResponse,
                query =>
                    (
                        Query: new ListAlertsQuery(
                            JobNamespace: query.Text("jobNamespace"),
                            UnresolvedOnly: query.Bool("unresolvedOnly"),
                            SeverityAtLeast: query.Enum<AlertSeverityCode>("severityAtLeast"),
                            DeliveryStatus: query.Enum<AlertDeliveryStatusCode>("deliveryStatus"),
                            AcknowledgedOnly: query.Bool("acknowledgedOnly"),
                            PageSize: query.Int("pageSize"),
                            Cursor: query.Text("cursor"),
                            IncludeTotal: query.Bool("includeTotal") ?? false,
                            Tags: query.Tags()
                        ),
                        Job: query.Job("jobRef", options)
                    ),
                static async (call, bound) =>
                    await call.Operations.Alerts.ListAsync(bound.Query with { JobId = await call.JobIdAsync(bound.Job) }, call.Ct),
                [
                    new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Only alerts raised in this namespace."),
                    new QueryParameterDoc("jobRef", QueryParameterKind.String, "Only alerts attached to this job ref."),
                    new QueryParameterDoc(
                        "severityAtLeast",
                        QueryParameterKind.String,
                        "Severity floor as a kebab code; lower severities are excluded."
                    ),
                    new QueryParameterDoc("unresolvedOnly", QueryParameterKind.Bool, "True restricts to alerts with no resolved instant."),
                    new QueryParameterDoc(
                        "acknowledgedOnly",
                        QueryParameterKind.Bool,
                        "True restricts to acknowledged alerts, false to unacknowledged; omit for both."
                    ),
                    new QueryParameterDoc(
                        "deliveryStatus",
                        QueryParameterKind.String,
                        "Exact delivery status as a kebab code.",
                        CodeKind: "alert-delivery-status"
                    ),
                    .. QueryParameterDocExtensions.PagingCore,
                    .. QueryParameterDocExtensions.IncludeTotal,
                    .. QueryParameterDocExtensions.TagFilter,
                ]
            )
            .ProducesProblem(StatusCodes.Status404NotFound);
        reads.MapDetail(
            "/alerts/{alertRef}",
            "Read one alert.",
            "Alert",
            DashboardJsonContext.Default.GetAlertResponse,
            static (http, call) => call.Operations.Alerts.GetAsync(RouteKeys.Alert(http), call.Ct)
        );
        reads.MapTags(
            controls,
            "/alerts/{alertRef}/tags",
            "alert",
            options,
            DashboardJsonContext.Default.UpsertAlertTagsRequest,
            static http => TagTarget.ForAlert(RouteKeys.Alert(http))
        );
        if (controls is null)
        {
            return;
        }

        controls.MapCommand(
            "/alerts/{alertRef}/acknowledge",
            "Acknowledge the alert: an operator has seen it.",
            options,
            DashboardJsonContext.Default.AcknowledgeAlertRequest,
            "Invalid alert command.",
            ControlStatus.Alert<AcknowledgeAlertResponse>(),
            RouteKeys.Alert,
            static (call, alertRef) => call.Operations.Alerts.AcknowledgeAsync(alertRef, call.ReasonMessage, call.Actor, call.Ct)
        );
        controls.MapCommand(
            "/alerts/{alertRef}/resolve",
            "Resolve the alert: its cause is handled.",
            options,
            DashboardJsonContext.Default.ResolveAlertRequest,
            "Invalid alert command.",
            ControlStatus.Alert<ResolveAlertResponse>(),
            RouteKeys.Alert,
            static (call, alertRef) => call.Operations.Alerts.ResolveAsync(alertRef, call.ReasonMessage, call.Actor, call.Ct)
        );
    }
}

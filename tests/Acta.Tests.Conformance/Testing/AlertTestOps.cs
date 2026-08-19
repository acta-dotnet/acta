using Acta.Runtime.Modules.Alerting;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support entry point into the Alerts feature: the raise upsert through the store port with
/// production channel canonicalization, deduplication-key normalization, and prose truncation.
/// </summary>
internal static class AlertTestOps
{
    /// <summary>
    /// Raises one alert with no projected event behind it, the shape a manual
    /// <c>ctx.AlertAsync</c> takes: the raise always applies and never moves the row's projection
    /// high-water mark. Specs that need the projector's event-scoped behavior drive
    /// <c>AlertsJob</c> itself.
    /// </summary>
    public static Task<int> RaiseAsync(
        IServiceProvider services,
        string jobNamespace,
        long? jobId,
        AlertOriginCode origin,
        AlertSeverityCode severity,
        AlertKindCode kind,
        string title,
        string message,
        string channelName,
        AlertDeliveryStatusCode deliveryStatus,
        string? deduplicationKey,
        DateTime? dedupeWindowStartUtc,
        CancellationToken ct
    ) =>
        services
            .GetRequiredService<IAlertStore>()
            .RaiseJobAlertAsync(
                RaiseJobAlertCommand.Create(
                    jobNamespace,
                    jobId,
                    origin,
                    severity,
                    kind,
                    title,
                    message,
                    channelName,
                    deliveryStatus,
                    deduplicationKey,
                    dedupeWindowStartUtc,
                    sourceEventId: null
                ),
                ct
            );
}

using Acta.Features.Alerts;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support entry point into the Alerts feature: the raise upsert through the store port with
/// production channel canonicalization, deduplication-key normalization, and prose truncation.
/// </summary>
internal static class AlertTestOps
{
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
                    dedupeWindowStartUtc
                ),
                ct
            );
}

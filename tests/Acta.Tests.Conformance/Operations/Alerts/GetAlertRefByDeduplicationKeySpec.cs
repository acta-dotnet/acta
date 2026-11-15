using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Alerts;

/// <summary>
/// Conformance for <c>GetAlertRefByDeduplicationKey</c>: the namespace's open alert holding a key, the incident a
/// handler's <c>ResolveAlertAsync(key)</c> resolves.
/// </summary>
[ConformanceSpec(
    "get-alert-ref-by-deduplication-key.lookup",
    "GetAlertRefByDeduplicationKey finds the open incident holding a key",
    Area = "Alerts",
    Contract = "GetAlertRefByDeduplicationKey answers the namespace's open alert holding a key, and null once it is resolved or when none holds it."
)]
public sealed class GetAlertRefByDeduplicationKeySpec : ActaStorageTestBase<ConformanceFixture>
{
    [Fact(DisplayName = "An open incident resolves to its ref, and a resolved or unknown key to null")]
    public async Task Open_incident_resolves_and_a_resolved_or_unknown_key_is_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var raised = await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            null,
            AlertSeverityCode.Warning,
            AlertKindCode.Manual,
            "t",
            "m",
            "default",
            AlertDeliveryStatusCode.Pending,
            "lookup-key",
            ct
        );
        var alerts = Services.GetRequiredService<AlertsOperations>();
        var open = new AlertRef(raised.AlertRef);

        Assert.Equal(open, await alerts.GetAlertRefByDeduplicationKeyAsync(TestNamespaceId, "lookup-key", ct));
        Assert.Null(await alerts.GetAlertRefByDeduplicationKeyAsync(TestNamespaceId, "no-such-key", ct));

        await alerts.ResolveAsync(open, ct: ct);
        Assert.Null(await alerts.GetAlertRefByDeduplicationKeyAsync(TestNamespaceId, "lookup-key", ct));
    }
}

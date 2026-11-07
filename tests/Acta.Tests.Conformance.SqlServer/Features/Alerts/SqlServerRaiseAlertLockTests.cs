using Acta.Tests.Conformance.SqlServer.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Alerts;

/// <summary>
/// SQL Server <c>raise_alert</c> serializes an alert identity on the identity's own application lock, so an open raise
/// holds its identity's rows and no gap of <c>ix_alerts_deduplication_key_identity</c> around them.
/// </summary>
public sealed class SqlServerRaiseAlertLockTests : ActaStorageTestBase<SqlServerConformanceFixture>
{
    [Fact(DisplayName = "A raise held open on one alert identity leaves the next identity in its key gap free to raise")]
    public async Task Held_raise_does_not_block_the_neighbouring_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        // The namespace holds no other identity, so the neighbour's open incident is the key right after the held one.
        await RaiseAsync("gap.b", ct);

        await using var holder = await Db.OpenConnectionAsync(ct);
        await using var hold = await holder.BeginTransactionAsync(ct);
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var held = Command("gap.a");
        await Db.ExecuteInTransactionAsync(
            hold,
            new StoreCommand("RaiseAlert"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, held.JobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.JobId, held.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.AlertOriginCode, (short)held.Origin));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.AlertSeverityCode, (short)held.Severity));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.AlertKindCode, (short)held.Kind));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.Title, held.Title));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.AlertMessage, held.Message));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.ChannelName, held.ChannelName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.DeliveryStatusCode, (short)held.DeliveryStatus));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.DeduplicationKey, held.DeduplicationKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SourceEventId, held.SourceEventId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.AlertRef, held.AlertRef));
            },
            static _ => 0,
            ct
        );

        var neighbour = RaiseAsync("gap.b", ct);
        // The held raise stays open until the fact ends, so a neighbour that waited on it would never return.
        var first = await Task.WhenAny(neighbour, Task.Delay(SpecWaits.Gate, ct));
        Assert.True(first == neighbour, "the neighbouring identity's raise waited on the open raise");
        Assert.Equal(2, (await neighbour).OccurrenceCount);
    }

    private Task<RaiseAlertResponse> RaiseAsync(string key, CancellationToken ct) =>
        Services.GetRequiredService<AlertsOperations>().RaiseAlertAsync(Command(key), ct);

    private RaiseAlertCommand Command(string key) =>
        RaiseAlertCommand.Create(
            TestNamespace,
            null,
            AlertOriginCode.Manual,
            AlertSeverityCode.Warning,
            AlertKindCode.Manual,
            "gap",
            "gap",
            "default",
            AlertDeliveryStatusCode.Pending,
            key,
            sourceEventId: null
        );
}

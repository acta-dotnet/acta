using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tenants;

/// <summary>
/// Pins the audit trail's fidelity for an operator whose name is not ASCII: the actor an
/// operator control stamps on its event reads back byte-identical on every provider. The tenant
/// verb is the carrier because it needs no job; every control surface stamps the same column
/// through the same actor type.
/// </summary>
[ConformanceSpec(
    "admin.unicode-actor",
    "A non-ASCII operator name survives the audit event intact",
    Area = "Admin",
    Contract = "The actor stamped on a control event round-trips byte-identical through every provider, including non-ASCII and astral characters."
)]
public sealed class UnicodeActorSpec : ActaStorageTestBase<ConformanceFixture>
{
    // A diacritic (two bytes in UTF-8, one UTF-16 unit) and an emoji (four bytes, a surrogate pair):
    // the two shapes a varchar column or an ASCII parameter would each mangle differently.
    private const string OperatorName = "Žiga Novak \U0001F4A5";

    [Fact(DisplayName = "The tenant.suspended event carries the operator name exactly as supplied")]
    public async Task Operator_name_round_trips_intact()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("adm-unicode-actor");
        var id = (await Services.GetRequiredService<ITenants>().RegisterAsync(key, null, null, ct)).TenantId;

        var outcome = await Services.GetRequiredService<ITenants>().SuspendAsync(key, "hold", OperatorName, ct);

        Assert.Equal(ControlAction.Applied, outcome.Action);
        var ev = await Db.From<EventEntity>()
            .Where(e => e.TenantId == id && e.EventCode == EventCode.TenantSuspended)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(ev);
        Assert.Equal(OperatorName, ev.Actor);
    }
}

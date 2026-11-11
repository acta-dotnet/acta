using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tenants;

/// <summary>
/// Conformance for who a tenant verb records: the operator it was given is its event's <c>actor</c> column, and the
/// event carries no details.
/// </summary>
[ConformanceSpec(
    "tenant.actor",
    "A tenant verb records its operator as the event's actor",
    Area = "Admin",
    Contract = "The operator a tenant verb names is its event's actor, byte-identical on every provider, and the event carries no details."
)]
public sealed class TenantControlActorSpec : ActaStorageTestBase<ConformanceFixture>
{
    private const string Operator = "Jože Kovač";

    [Theory(DisplayName = "The operator who applied a tenant verb is the event's actor, and the event carries no details")]
    [InlineData("suspend", EventCode.TenantSuspended)]
    [InlineData("resume", EventCode.TenantResumed)]
    [InlineData("update", EventCode.TenantUpdated)]
    public async Task Operator_is_the_events_actor(string verb, EventCode recorded)
    {
        var ct = TestContext.Current.CancellationToken;
        var tenants = Services.GetRequiredService<ITenants>();
        var key = Unique("actor");
        var id = (await tenants.RegisterAsync(key, null, null, ct)).TenantId;
        if (verb == "resume")
        {
            Assert.Equal(ControlAction.Applied, (await tenants.SuspendAsync(key, ct: ct)).Action);
        }
        var version = (await Db.From<TenantEntity>().Where(t => t.Id == id).SingleOrDefaultAsync(ct))!.Version;

        var action = verb switch
        {
            "suspend" => (await tenants.SuspendAsync(key, "why", Operator, ct)).Action,
            "resume" => (await tenants.ResumeAsync(key, "why", Operator, ct)).Action,
            _ => (await tenants.UpdateAsync(key, version, "Tenant", "desc", "why", Operator, ct)).Action,
        };

        Assert.Equal(ControlAction.Applied, action);
        var evt = Assert.Single(
            await Db.From<EventEntity>()
                .Where(e => e.EntityCode == EntityCode.Tenant && e.EntityId == id && e.EventCode == recorded)
                .ToListAsync(ct)
        );
        Assert.Equal(Operator, evt.Actor);
        Assert.Null(evt.Details);
    }
}

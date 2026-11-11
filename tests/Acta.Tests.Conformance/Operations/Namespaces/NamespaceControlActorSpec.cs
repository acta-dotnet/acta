using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Namespaces;

/// <summary>
/// Conformance for who a namespace verb records: the operator it was given is its event's <c>actor</c> column, and the
/// event carries no details.
/// </summary>
[ConformanceSpec(
    "namespace.actor",
    "A namespace verb records its operator as the event's actor",
    Area = "Admin",
    Contract = "The operator a namespace verb names is its event's actor, byte-identical on every provider, and the event carries no details."
)]
public sealed class NamespaceControlActorSpec : ActaStorageTestBase<ConformanceFixture>
{
    private const string Operator = "Jože Kovač";

    [Theory(DisplayName = "The operator who applied a namespace verb is the event's actor, and the event carries no details")]
    [InlineData("suspend", EventCode.NamespaceSuspended)]
    [InlineData("resume", EventCode.NamespaceResumed)]
    [InlineData("update", EventCode.NamespaceUpdated)]
    public async Task Operator_is_the_events_actor(string verb, EventCode recorded)
    {
        var ct = TestContext.Current.CancellationToken;
        var namespaces = Services.GetRequiredService<IActaOperations>().Namespaces;
        if (verb == "resume")
        {
            Assert.Equal(ControlAction.Applied, (await namespaces.SuspendAsync(TestNamespace, ct: ct)).Action);
        }
        var version = (await Db.From<NamespaceEntity>().Where(n => n.Id == TestNamespaceId).SingleOrDefaultAsync(ct))!.Version;

        var action = verb switch
        {
            "suspend" => (await namespaces.SuspendAsync(TestNamespace, "why", Operator, ct)).Action,
            "resume" => (await namespaces.ResumeAsync(TestNamespace, "why", Operator, ct)).Action,
            _ => (await namespaces.UpdateAsync(TestNamespace, version, "team-a", "desc", "why", Operator, ct)).Action,
        };

        Assert.Equal(ControlAction.Applied, action);
        var evt = Assert.Single(
            await Db.From<EventEntity>().Where(e => e.NamespaceId == TestNamespaceId && e.EventCode == recorded).ToListAsync(ct)
        );
        Assert.Equal(Operator, evt.Actor);
        Assert.Null(evt.Details);
    }
}

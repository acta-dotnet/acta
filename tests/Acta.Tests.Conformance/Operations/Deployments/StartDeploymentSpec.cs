using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Deployments;

/// <summary>
/// Operation-level conformance for <c>StartDeployment</c>, the first statement of a deploy's transaction: it inserts a
/// namespace only when the name is new, answers for each namespace the version it stands at with whether the deploying
/// version has a row there, and holds every namespace's schedule lock until the transaction ends.
/// </summary>
[ConformanceSpec(
    "deployment.start",
    "StartDeployment opens a deploy's namespaces and answers where each stands",
    Area = "Catalog",
    Contract = "StartDeployment inserts absent namespaces, answers each one's current version and whether this one deployed before, and makes a second deploy wait."
)]
public sealed class StartDeploymentSpec : ActaStorageTestBase<ConformanceFixture>
{
    private DeploymentsOperations Deployments => Services.GetRequiredService<DeploymentsOperations>();

    private async Task<IReadOnlyList<StartDeploymentRow>> StartAsync(string version, CancellationToken ct, params string[] namespaceNames)
    {
        await using var connection = await Db.OpenConnectionAsync(ct);
        return await InCallerTransactionAsync(
            connection,
            async transaction =>
            {
                var start = await Deployments.StartDeploymentAsync(transaction, namespaceNames, version, ct);
                await transaction.CommitAsync(ct);
                return start.Namespaces;
            },
            ct
        );
    }

    private async Task<StartDeploymentRow> StartOneAsync(string namespaceName, string version, CancellationToken ct) =>
        Assert.Single(await StartAsync(version, ct, namespaceName));

    private async Task RecordAsync(int namespaceId, string version, CancellationToken ct)
    {
        await using var connection = await Db.OpenConnectionAsync(ct);
        await InCallerTransactionAsync(
            connection,
            async transaction =>
            {
                await Deployments.RecordDeploymentAsync(new(transaction, namespaceId, version, "test", null, null, false, [], []), ct);
                await transaction.CommitAsync(ct);
            },
            ct
        );
    }

    [Fact(DisplayName = "A start in a new namespace inserts it Active and answers no current version")]
    public async Task A_start_in_a_new_namespace_inserts_it_and_answers_no_current_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = Unique("deploy-new");

        var start = await StartOneAsync(name, "1.0.0", ct);

        Assert.Null(start.CurrentDeploymentVersion);
        Assert.False(start.IsDeployed);
        var ns = await Db.From<NamespaceEntity>().Where(n => n.NamespaceName == name).SingleOrDefaultAsync(ct);
        Assert.NotNull(ns);
        Assert.Equal(start.NamespaceId, ns!.Id);
        Assert.Equal(NamespaceStatusCode.Active, ns.Status);
        Assert.Equal(0, ns.Version);
        Assert.Null(ns.DeploymentVersion);
    }

    [Fact(DisplayName = "A start in an existing namespace answers its id and writes nothing")]
    public async Task A_start_in_an_existing_namespace_answers_its_id_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Db.From<NamespaceEntity>().Where(n => n.Id == TestNamespaceId).SingleOrDefaultAsync(ct);

        var start = await StartOneAsync(TestNamespace, "1.0.0", ct);

        Assert.Equal(TestNamespaceId, start.NamespaceId);
        var after = await Db.From<NamespaceEntity>().Where(n => n.Id == TestNamespaceId).SingleOrDefaultAsync(ct);
        Assert.Equal(before!.Version, after!.Version);
        Assert.Equal(before.ModifiedAtUtc, after.ModifiedAtUtc);
        Assert.Equal(0, await Db.From<DeploymentEntity>().Where(d => d.NamespaceId == TestNamespaceId).CountAsync(ct));
    }

    [Fact(DisplayName = "A start of several namespaces answers one row for each, the new ones inserted")]
    public async Task A_start_of_several_namespaces_answers_one_row_for_each()
    {
        var ct = TestContext.Current.CancellationToken;
        var added = Unique("deploy-added");
        await RecordAsync(TestNamespaceId, "1.0.0", ct);

        var rows = await StartAsync("1.0.0", ct, added, TestNamespace);

        var byName = rows.ToDictionary(r => r.NamespaceName);
        Assert.Equal(2, byName.Count);
        Assert.Equal(
            (TestNamespaceId, "1.0.0", true),
            (byName[TestNamespace].NamespaceId, byName[TestNamespace].CurrentDeploymentVersion, byName[TestNamespace].IsDeployed)
        );
        Assert.Null(byName[added].CurrentDeploymentVersion);
        Assert.False(byName[added].IsDeployed);
        Assert.NotEqual(TestNamespaceId, byName[added].NamespaceId);
    }

    [Fact(DisplayName = "A start answers the current version, and separately whether the deploying version has a row")]
    public async Task A_start_answers_the_current_version_and_whether_this_version_deployed_before()
    {
        var ct = TestContext.Current.CancellationToken;
        await RecordAsync(TestNamespaceId, "1.0.0", ct);
        await RecordAsync(TestNamespaceId, "1.1.0", ct);

        var current = await StartOneAsync(TestNamespace, "1.1.0", ct);
        var earlier = await StartOneAsync(TestNamespace, "1.0.0", ct);
        var unseen = await StartOneAsync(TestNamespace, "1.2.0", ct);

        Assert.Equal(("1.1.0", true), (current.CurrentDeploymentVersion, current.IsDeployed));
        Assert.Equal(("1.1.0", true), (earlier.CurrentDeploymentVersion, earlier.IsDeployed));
        Assert.Equal(("1.1.0", false), (unseen.CurrentDeploymentVersion, unseen.IsDeployed));
    }

    [Fact(DisplayName = "A version that differs from a deployed one only in case has not deployed")]
    public async Task A_version_that_differs_only_in_case_has_not_deployed()
    {
        var ct = TestContext.Current.CancellationToken;
        await RecordAsync(TestNamespaceId, "1.4.0-RC1", ct);

        var start = await StartOneAsync(TestNamespace, "1.4.0-rc1", ct);

        Assert.Equal(("1.4.0-RC1", false), (start.CurrentDeploymentVersion, start.IsDeployed));
    }

    [Theory(DisplayName = "A second start in the namespace waits until the first one's transaction ends, and then sees what it wrote")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_second_start_waits_until_the_first_transaction_ends(bool namespaceExists)
    {
        var ct = TestContext.Current.CancellationToken;

        // A namespace with no row yet has no id to lock by: the two starts meet on the name.
        var name = namespaceExists ? TestNamespace : Unique("deploy-first");

        await using var connection = await Db.OpenConnectionAsync(ct);
        var (first, second) = await InCallerTransactionAsync(
            connection,
            async transaction =>
            {
                var opened = Assert.Single((await Deployments.StartDeploymentAsync(transaction, [name], "1.0.0", ct)).Namespaces);

                // On its own thread: SQLite's BEGIN IMMEDIATE waits for the write lock without yielding.
                var pending = Task.Run(() => StartOneAsync(name, "1.1.0", ct), ct);

                // Long enough for an unblocked start to answer: it is one round trip.
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                Assert.False(pending.IsCompleted, "the second start answered while the first deploy was still open");

                await Deployments.RecordDeploymentAsync(
                    new(transaction, opened.NamespaceId, "1.0.0", "test", null, null, false, [], []),
                    ct
                );
                await transaction.CommitAsync(ct);
                return (opened, pending);
            },
            ct
        );

        // What it waited for is visible to it: the same namespace row, and the first deploy's version as the current one.
        var answered = await second.WaitAsync(SpecWaits.Gate, ct);
        Assert.Equal((first.NamespaceId, "1.0.0", false), (answered.NamespaceId, answered.CurrentDeploymentVersion, answered.IsDeployed));
    }
}

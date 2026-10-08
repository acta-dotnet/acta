using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Schema;

[ConformanceSpec(
    "schema.namespace-row",
    "Worker init writes a readable namespace row with a positive id",
    Area = "Catalog",
    Contract = "A seeded namespace is persisted with a positive db-assigned id and is readable back by that id."
)]
public sealed class NamespaceRowSpec : ActaStorageTestBase<ConformanceFixture>
{
    [Fact(DisplayName = "Seeded namespace row persists and is readable by id with the expected name")]
    public async Task Seeded_namespace_is_readable_by_id()
    {
        var ct = TestContext.Current.CancellationToken;

        var ns = await Db.From<NamespaceEntity>().Where(n => n.Id == TestNamespaceId).SingleOrDefaultAsync(ct);

        Assert.NotNull(ns);
        Assert.Equal(TestNamespace, ns!.Name);
    }

    [Fact(DisplayName = "Db-assigned namespace id is positive and matches the persisted row")]
    public async Task Seeder_returns_positive_id_matching_db_row()
    {
        Assert.True(TestNamespaceId > 0);

        var ct = TestContext.Current.CancellationToken;

        var ns = await Db.From<NamespaceEntity>().Where(n => n.Id == TestNamespaceId).SingleOrDefaultAsync(ct);

        Assert.NotNull(ns);
        Assert.Equal(TestNamespaceId, ns!.Id);
    }
}

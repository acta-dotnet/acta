using Acta.Relational.Schema;
using Xunit;

namespace Acta.Tests.Schema;

/// <summary>
/// The fourth startup verdict: whether the versionless objects installed here are ones this build can
/// call. Operator views and routines carry no migration version, so history alone cannot answer it.
/// </summary>
public sealed class ObjectPackagePreflightTests
{
    private const string Dialect = "pg";

    [Fact(DisplayName = "An equal major at the required revision starts")]
    public void Equal_major_at_the_minimum_starts() => Verify("objects-1.3-" + Hash, new ObjectPackageRequirement(1, 3));

    [Fact(DisplayName = "An equal major at a higher revision starts, which is what keeps a rolling deploy working")]
    public void Equal_major_above_the_minimum_starts()
    {
        // The older worker against the newer package: the deploy that installs revision 7 while workers
        // requiring 3 are still running is a shape the production guide promises.
        Verify("objects-1.7-" + Hash, new ObjectPackageRequirement(1, 3));
    }

    [Fact(DisplayName = "A revision below the minimum is refused and names the provisioning script")]
    public void Below_the_minimum_is_refused()
    {
        var ex = Refused("objects-1.2-" + Hash, new ObjectPackageRequirement(1, 3));
        Assert.Contains("requires revision 3 or above", ex.Message, StringComparison.Ordinal);
        Assert.Contains("schema-pg.sql", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A higher installed major is refused rather than accepted as newer")]
    public void Higher_major_is_refused()
    {
        // The half a single ordered counter gets wrong: a higher major is the break the number exists to
        // signal, so accepting anything at or above the requirement would accept exactly that break.
        var ex = Refused("objects-2.1-" + Hash, new ObjectPackageRequirement(1, 1));
        Assert.Contains("contract major 2 installed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not interchangeable in either direction", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A lower installed major is refused too")]
    public void Lower_major_is_refused() => Assert.Contains("contract major 1 installed", Refused("objects-1.9-" + Hash, new ObjectPackageRequirement(2, 1)).Message, StringComparison.Ordinal);

    [Fact(DisplayName = "A high revision never compensates for the wrong major")]
    public void A_high_revision_does_not_rescue_a_wrong_major() =>
        Assert.Contains("major", Refused("objects-2.99-" + Hash, new ObjectPackageRequirement(1, 1)).Message, StringComparison.Ordinal);

    [Fact(DisplayName = "A database carrying no package row is refused, and told to run the script rather than reprovision")]
    public void A_missing_package_is_refused()
    {
        var ex = Refused(null, ObjectPackageStamp.Required);
        Assert.Contains("records no Acta object package", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a reprovision", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A package row this build cannot read is refused, quoting what it read")]
    public void An_unreadable_package_is_refused() =>
        Assert.Contains("'objects-banana'", Refused("objects-banana", ObjectPackageStamp.Required).Message, StringComparison.Ordinal);

    [Fact(DisplayName = "This build's own package satisfies this build's own requirement")]
    public void The_shipped_package_satisfies_the_shipped_requirement() =>
        Verify(ObjectPackageStamp.Format(ObjectPackageHashes.ForDialect(Dialect)), ObjectPackageStamp.Required);

    // The hash is never read by the verdict, so every case above can carry the same one; that it does not
    // matter is itself the property under test.
    private const string Hash = "00000000000000000000000000000000";

    private static void Verify(string? recorded, ObjectPackageRequirement required) =>
        MigrationHistoryPreflight.VerifyObjectPackage(recorded, required, Dialect);

    private static InvalidOperationException Refused(string? recorded, ObjectPackageRequirement required) =>
        Assert.Throws<InvalidOperationException>(() => Verify(recorded, required));
}

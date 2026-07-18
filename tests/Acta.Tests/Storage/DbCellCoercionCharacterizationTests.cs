using Acta.Relational.Commands;
using Xunit;

namespace Acta.Tests.Storage;

/// <summary>
/// Characterization test pinning <see cref="DbCellCoercion.DateTimeUtc"/>'s SQLite epoch-millis coercion
/// across the storage refactor that merged <c>DbRead</c>, <c>DbQueryMaterializer</c>, and
/// <c>DbScalarCoercion</c> into one utility. This exact output must survive that merge.
/// </summary>
public sealed class DbCellCoercionCharacterizationTests
{
    [Fact]
    public void DateTimeUtc_from_sqlite_epoch_millis_is_utc_kind()
    {
        var read = DbCellCoercion.DateTimeUtc("empty");
        var dt = read(1_700_000_000_000L);

        Assert.Equal(DateTimeKind.Utc, dt.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L).UtcDateTime, dt);
    }
}

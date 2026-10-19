using Xunit;

namespace Acta.Tests.Storage;

public sealed class DbCellCoercionCharacterizationTests
{
    // SQLite stores an instant as epoch milliseconds; it reads back as the same instant, Utc-kind.
    [Fact]
    public void ToUtc_from_sqlite_epoch_millis_is_utc_kind()
    {
        var dt = DbCellCoercion.ToUtc(1_700_000_000_000L);

        Assert.Equal(DateTimeKind.Utc, dt.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L).UtcDateTime, dt);
    }
}

using Acta.Tests.Conformance.DbSession;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.DbSession;

public sealed class SqliteDbSessionWriteSpec : DbSessionWriteSpec<SqliteConformanceFixture>;

public sealed class SqliteFluentReadSpec : FluentReadSpec<SqliteConformanceFixture>;

public sealed class SqliteSettingsUniqueKeySpec : SettingsUniqueKeySpec<SqliteConformanceFixture>;

public sealed class SqliteSessionProviderDiscriminatorSpec : SessionProviderDiscriminatorSpec<SqliteConformanceFixture>
{
    protected override DbProvider ExpectedProvider => DbProvider.Sqlite;
}

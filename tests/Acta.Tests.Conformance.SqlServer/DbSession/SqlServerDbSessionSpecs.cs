using Acta.Tests.Conformance.DbSession;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.DbSession;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerDbSessionWriteSpec : DbSessionWriteSpec<SqlServerConformanceFixture>;

public sealed class SqlServerFluentReadSpec : FluentReadSpec<SqlServerConformanceFixture>;

public sealed class SqlServerSettingsUniqueKeySpec : SettingsUniqueKeySpec<SqlServerConformanceFixture>;

public sealed class SqlServerSessionProviderDiscriminatorSpec : SessionProviderDiscriminatorSpec<SqlServerConformanceFixture>
{
    protected override DbProvider ExpectedProvider => DbProvider.SqlServer;
}

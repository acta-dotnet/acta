using Acta.Configuration;
using Acta.Tests.Conformance.DbSession;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.DbSession;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgDbSessionWriteSpec : DbSessionWriteSpec<PgConformanceFixture>;

public sealed class PgFluentReadSpec : FluentReadSpec<PgConformanceFixture>;

public sealed class PgSettingsUniqueKeySpec : SettingsUniqueKeySpec<PgConformanceFixture>;

public sealed class PgSessionProviderDiscriminatorSpec : SessionProviderDiscriminatorSpec<PgConformanceFixture>
{
    protected override DbProvider ExpectedProvider => DbProvider.Postgres;
}

using Acta.Tests.Conformance.Features.Schema;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Schema;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgM001InstallSpec : M001InstallSpec<PgConformanceFixture>;

public sealed class PgOperatorViewSpec : OperatorViewSpec<PgConformanceFixture>;

public sealed class PgSchemaHardeningSpec : SchemaHardeningSpec<PgConformanceFixture>;

public sealed class PgMigrationHistoryPreflightSpec : MigrationHistoryPreflightSpec<PgConformanceFixture>;

using Acta.Tests.Conformance.Features.Schema;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Schema;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerM001InstallSpec : M001InstallSpec<SqlServerConformanceFixture>;

public sealed class SqlServerOperatorViewSpec : OperatorViewSpec<SqlServerConformanceFixture>;

public sealed class SqlServerSchemaHardeningSpec : SchemaHardeningSpec<SqlServerConformanceFixture>;

public sealed class SqlServerMigrationHistoryPreflightSpec : MigrationHistoryPreflightSpec<SqlServerConformanceFixture>;

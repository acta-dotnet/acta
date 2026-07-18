using Acta.Tests.Conformance.Features.Schema;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Schema;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqliteM001InstallSpec : M001InstallSpec<SqliteConformanceFixture>;

public sealed class SqliteOperatorViewSpec : OperatorViewSpec<SqliteConformanceFixture>;

public sealed class SqliteSchemaHardeningSpec : SchemaHardeningSpec<SqliteConformanceFixture>;

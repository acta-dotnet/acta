using Acta.Tests.Conformance.Features.Workers;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Workers;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqliteExtendWorkerLeasesSpec : ExtendWorkerLeasesSpec<SqliteConformanceFixture>;

public sealed class SqliteGetWorkerSpec : GetWorkerSpec<SqliteConformanceFixture>;

public sealed class SqliteListWorkersFilterMatrixSpec : ListWorkersFilterMatrixSpec<SqliteConformanceFixture>;

public sealed class SqliteListWorkersSpec : ListWorkersSpec<SqliteConformanceFixture>;

public sealed class SqliteMarkDeadWorkersSpec : MarkDeadWorkersSpec<SqliteConformanceFixture>;

public sealed class SqliteStartWorkerSpec : StartWorkerSpec<SqliteConformanceFixture>;

public sealed class SqliteStopWorkerSpec : StopWorkerSpec<SqliteConformanceFixture>;

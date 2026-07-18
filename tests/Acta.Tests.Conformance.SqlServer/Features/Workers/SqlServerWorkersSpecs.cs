using Acta.Tests.Conformance.Features.Workers;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Workers;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerExtendWorkerLeasesSpec : ExtendWorkerLeasesSpec<SqlServerConformanceFixture>;

public sealed class SqlServerGetWorkerSpec : GetWorkerSpec<SqlServerConformanceFixture>;

public sealed class SqlServerListWorkersFilterMatrixSpec : ListWorkersFilterMatrixSpec<SqlServerConformanceFixture>;

public sealed class SqlServerListWorkersSpec : ListWorkersSpec<SqlServerConformanceFixture>;

public sealed class SqlServerMarkDeadWorkersSpec : MarkDeadWorkersSpec<SqlServerConformanceFixture>;

public sealed class SqlServerStartWorkerSpec : StartWorkerSpec<SqlServerConformanceFixture>;

public sealed class SqlServerStopWorkerSpec : StopWorkerSpec<SqlServerConformanceFixture>;

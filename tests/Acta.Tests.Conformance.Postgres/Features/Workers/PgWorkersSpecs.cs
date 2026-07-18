using Acta.Tests.Conformance.Features.Workers;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Workers;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgExtendWorkerLeasesSpec : ExtendWorkerLeasesSpec<PgConformanceFixture>;

public sealed class PgGetWorkerSpec : GetWorkerSpec<PgConformanceFixture>;

public sealed class PgListWorkersFilterMatrixSpec : ListWorkersFilterMatrixSpec<PgConformanceFixture>;

public sealed class PgListWorkersSpec : ListWorkersSpec<PgConformanceFixture>;

public sealed class PgMarkDeadWorkersSpec : MarkDeadWorkersSpec<PgConformanceFixture>;

public sealed class PgStartWorkerSpec : StartWorkerSpec<PgConformanceFixture>;

public sealed class PgStopWorkerSpec : StopWorkerSpec<PgConformanceFixture>;

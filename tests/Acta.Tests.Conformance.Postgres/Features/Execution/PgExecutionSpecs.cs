using Acta.Tests.Conformance.Features.Execution;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Execution;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgCompleteExecutionsBatchSpec : CompleteExecutionsBatchSpec<PgConformanceFixture>;

public sealed class PgExecutionOutcomeMatrixSpec : ExecutionOutcomeMatrixSpec<PgConformanceFixture>;

public sealed class PgReclaimStuckJobsSpec : ReclaimStuckJobsSpec<PgConformanceFixture>;

public sealed class PgStartExecutionStaleVersionSpec : StartExecutionStaleVersionSpec<PgConformanceFixture>;

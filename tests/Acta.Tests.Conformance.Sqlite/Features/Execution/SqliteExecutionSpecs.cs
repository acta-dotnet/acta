using Acta.Tests.Conformance.Features.Execution;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Execution;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqliteCompleteExecutionsBatchSpec : CompleteExecutionsBatchSpec<SqliteConformanceFixture>;

public sealed class SqliteExecutionOutcomeMatrixSpec : ExecutionOutcomeMatrixSpec<SqliteConformanceFixture>;

public sealed class SqliteReclaimStuckJobsSpec : ReclaimStuckJobsSpec<SqliteConformanceFixture>;

public sealed class SqliteStartExecutionStaleVersionSpec : StartExecutionStaleVersionSpec<SqliteConformanceFixture>;

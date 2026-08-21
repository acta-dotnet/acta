using Acta.Tests.Conformance.Features.Execution;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Execution;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerCompleteExecutionsBatchSpec : CompleteExecutionsBatchSpec<SqlServerConformanceFixture>;

public sealed class SqlServerExecutionOutcomeMatrixSpec : ExecutionOutcomeMatrixSpec<SqlServerConformanceFixture>;

public sealed class SqlServerReclaimStuckJobsSpec : ReclaimStuckJobsSpec<SqlServerConformanceFixture>;

public sealed class SqlServerStartExecutionStaleVersionSpec : StartExecutionStaleVersionSpec<SqlServerConformanceFixture>;

public sealed class SqlServerWaitTimeoutReclaimSpec : WaitTimeoutReclaimSpec<SqlServerConformanceFixture>;

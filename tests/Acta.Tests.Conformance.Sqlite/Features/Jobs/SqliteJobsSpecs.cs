using Acta.Tests.Conformance.Features.Jobs;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Jobs;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqliteControlVerbAuditGatingSpec : ControlVerbAuditGatingSpec<SqliteConformanceFixture>;

public sealed class SqliteControlVerbStateMatrixSpec : ControlVerbStateMatrixSpec<SqliteConformanceFixture>;

public sealed class SqliteEnqueueRejectionSpec : EnqueueRejectionSpec<SqliteConformanceFixture>;

public sealed class SqliteEnqueueRejectionTaxonomySpec : EnqueueRejectionTaxonomySpec<SqliteConformanceFixture>;

public sealed class SqliteEnqueueSpec : EnqueueSpec<SqliteConformanceFixture>;

public sealed class SqliteGetJobExplanationSpec : GetJobExplanationSpec<SqliteConformanceFixture>;

public sealed class SqliteGetJobLineageMapSpec : GetJobLineageMapSpec<SqliteConformanceFixture>;

public sealed class SqliteGetJobSpec : GetJobSpec<SqliteConformanceFixture>;

public sealed class SqliteGetJobStatusSpec : GetJobStatusSpec<SqliteConformanceFixture>;

public sealed class SqliteIdentifierCaseFoldingSpec : IdentifierCaseFoldingSpec<SqliteConformanceFixture>;

public sealed class SqliteJobPurgeSpec : JobPurgeSpec<SqliteConformanceFixture>;

public sealed class SqliteJobReprioritizeSpec : JobReprioritizeSpec<SqliteConformanceFixture>;

public sealed class SqliteJobRescheduleSpec : JobRescheduleSpec<SqliteConformanceFixture>;

public sealed class SqliteListJobsFilterMatrixSpec : ListJobsFilterMatrixSpec<SqliteConformanceFixture>;

public sealed class SqliteListJobsSpec : ListJobsSpec<SqliteConformanceFixture>;

public sealed class SqliteNamespaceEnqueueSpec : NamespaceEnqueueSpec<SqliteConformanceFixture>;

public sealed class SqliteResetJobStateSpec : ResetJobStateSpec<SqliteConformanceFixture>;

public sealed class SqliteResolveJobIdByDeduplicationKeySpec : ResolveJobIdByDeduplicationKeySpec<SqliteConformanceFixture>;

public sealed class SqliteResolveJobIdByRefSpec : ResolveJobIdByRefSpec<SqliteConformanceFixture>;

public sealed class SqliteTenantEnqueueSpec : TenantEnqueueSpec<SqliteConformanceFixture>;

public sealed class SqliteJobControlBatchSpec : JobControlBatchSpec<SqliteConformanceFixture>;

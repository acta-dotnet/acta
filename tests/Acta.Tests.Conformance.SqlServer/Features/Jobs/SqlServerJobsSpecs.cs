using Acta.Tests.Conformance.Features.Jobs;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Jobs;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerControlVerbAuditGatingSpec : ControlVerbAuditGatingSpec<SqlServerConformanceFixture>;

public sealed class SqlServerControlVerbStateMatrixSpec : ControlVerbStateMatrixSpec<SqlServerConformanceFixture>;

public sealed class SqlServerEnqueueRejectionSpec : EnqueueRejectionSpec<SqlServerConformanceFixture>;

public sealed class SqlServerEnqueueRejectionTaxonomySpec : EnqueueRejectionTaxonomySpec<SqlServerConformanceFixture>;

public sealed class SqlServerEnqueueSpec : EnqueueSpec<SqlServerConformanceFixture>;

public sealed class SqlServerGetJobExplanationSpec : GetJobExplanationSpec<SqlServerConformanceFixture>;

public sealed class SqlServerGetJobLineageMapSpec : GetJobLineageMapSpec<SqlServerConformanceFixture>;

public sealed class SqlServerGetJobSpec : GetJobSpec<SqlServerConformanceFixture>;

public sealed class SqlServerGetJobStatusSpec : GetJobStatusSpec<SqlServerConformanceFixture>;

public sealed class SqlServerIdentifierCaseFoldingSpec : IdentifierCaseFoldingSpec<SqlServerConformanceFixture>;

public sealed class SqlServerJobPayloadReadsSpec : JobPayloadReadsSpec<SqlServerConformanceFixture>;

public sealed class SqlServerJobPurgeSpec : JobPurgeSpec<SqlServerConformanceFixture>;

public sealed class SqlServerJobReprioritizeSpec : JobReprioritizeSpec<SqlServerConformanceFixture>;

public sealed class SqlServerJobRescheduleSpec : JobRescheduleSpec<SqlServerConformanceFixture>;

public sealed class SqlServerListJobsFilterMatrixSpec : ListJobsFilterMatrixSpec<SqlServerConformanceFixture>;

public sealed class SqlServerListJobsSpec : ListJobsSpec<SqlServerConformanceFixture>;

public sealed class SqlServerNamespaceEnqueueSpec : NamespaceEnqueueSpec<SqlServerConformanceFixture>;

public sealed class SqlServerResetJobStateSpec : ResetJobStateSpec<SqlServerConformanceFixture>;

public sealed class SqlServerResolveJobIdByDeduplicationKeySpec : ResolveJobIdByDeduplicationKeySpec<SqlServerConformanceFixture>;

public sealed class SqlServerResolveJobIdByRefSpec : ResolveJobIdByRefSpec<SqlServerConformanceFixture>;

public sealed class SqlServerTenantEnqueueSpec : TenantEnqueueSpec<SqlServerConformanceFixture>;

public sealed class SqlServerTransactionalEnqueueContractSpec : TransactionalEnqueueContractSpec<SqlServerConformanceFixture>;

public sealed class SqlServerTransactionalEnqueueSmokeSpec : TransactionalEnqueueSmokeSpec<SqlServerConformanceFixture>;

public sealed class SqlServerUpdateJobInputSpec : UpdateJobInputSpec<SqlServerConformanceFixture>;

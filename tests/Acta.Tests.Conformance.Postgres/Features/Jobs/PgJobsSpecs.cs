using Acta.Tests.Conformance.Features.Jobs;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Jobs;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgControlVerbAuditGatingSpec : ControlVerbAuditGatingSpec<PgConformanceFixture>;

public sealed class PgControlVerbStateMatrixSpec : ControlVerbStateMatrixSpec<PgConformanceFixture>;

public sealed class PgEnqueueRejectionSpec : EnqueueRejectionSpec<PgConformanceFixture>;

public sealed class PgEnqueueRejectionTaxonomySpec : EnqueueRejectionTaxonomySpec<PgConformanceFixture>;

public sealed class PgEnqueueSpec : EnqueueSpec<PgConformanceFixture>;

public sealed class PgGetJobExplanationSpec : GetJobExplanationSpec<PgConformanceFixture>;

public sealed class PgGetJobLineageMapSpec : GetJobLineageMapSpec<PgConformanceFixture>;

public sealed class PgGetJobSpec : GetJobSpec<PgConformanceFixture>;

public sealed class PgGetJobStatusSpec : GetJobStatusSpec<PgConformanceFixture>;

public sealed class PgIdentifierCaseFoldingSpec : IdentifierCaseFoldingSpec<PgConformanceFixture>;

public sealed class PgJobPurgeSpec : JobPurgeSpec<PgConformanceFixture>;

public sealed class PgJobReprioritizeSpec : JobReprioritizeSpec<PgConformanceFixture>;

public sealed class PgJobRescheduleSpec : JobRescheduleSpec<PgConformanceFixture>;

public sealed class PgListJobsFilterMatrixSpec : ListJobsFilterMatrixSpec<PgConformanceFixture>;

public sealed class PgListJobsSpec : ListJobsSpec<PgConformanceFixture>;

public sealed class PgNamespaceEnqueueSpec : NamespaceEnqueueSpec<PgConformanceFixture>;

public sealed class PgResetJobStateSpec : ResetJobStateSpec<PgConformanceFixture>;

public sealed class PgResolveJobIdByDeduplicationKeySpec : ResolveJobIdByDeduplicationKeySpec<PgConformanceFixture>;

public sealed class PgResolveJobIdByRefSpec : ResolveJobIdByRefSpec<PgConformanceFixture>;

public sealed class PgTenantEnqueueSpec : TenantEnqueueSpec<PgConformanceFixture>;

public sealed class PgJobControlBatchSpec : JobControlBatchSpec<PgConformanceFixture>;

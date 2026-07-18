using Acta.Tests.Conformance.Features.Definitions;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Definitions;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgDefinitionOverrideBindMatrixSpec : DefinitionOverrideBindMatrixSpec<PgConformanceFixture>;

public sealed class PgFrameworkJobRegistrationSpec : FrameworkJobRegistrationSpec<PgConformanceFixture>;

public sealed class PgGetJobDefinitionSpec : GetJobDefinitionSpec<PgConformanceFixture>;

public sealed class PgListJobDefinitionsFilterMatrixSpec : ListJobDefinitionsFilterMatrixSpec<PgConformanceFixture>;

public sealed class PgListJobDefinitionsSpec : ListJobDefinitionsSpec<PgConformanceFixture>;

public sealed class PgMonotonicDefinitionPromotionSpec : MonotonicDefinitionPromotionSpec<PgConformanceFixture>;

public sealed class PgSetJobDefinitionOverridesSpec : SetJobDefinitionOverridesSpec<PgConformanceFixture>;

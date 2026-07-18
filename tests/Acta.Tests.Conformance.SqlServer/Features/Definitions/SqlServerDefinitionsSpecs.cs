using Acta.Tests.Conformance.Features.Definitions;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Definitions;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerDefinitionOverrideBindMatrixSpec : DefinitionOverrideBindMatrixSpec<SqlServerConformanceFixture>;

public sealed class SqlServerFrameworkJobRegistrationSpec : FrameworkJobRegistrationSpec<SqlServerConformanceFixture>;

public sealed class SqlServerGetJobDefinitionSpec : GetJobDefinitionSpec<SqlServerConformanceFixture>;

public sealed class SqlServerListJobDefinitionsFilterMatrixSpec : ListJobDefinitionsFilterMatrixSpec<SqlServerConformanceFixture>;

public sealed class SqlServerListJobDefinitionsSpec : ListJobDefinitionsSpec<SqlServerConformanceFixture>;

public sealed class SqlServerMonotonicDefinitionPromotionSpec : MonotonicDefinitionPromotionSpec<SqlServerConformanceFixture>;

public sealed class SqlServerSetJobDefinitionOverridesSpec : SetJobDefinitionOverridesSpec<SqlServerConformanceFixture>;

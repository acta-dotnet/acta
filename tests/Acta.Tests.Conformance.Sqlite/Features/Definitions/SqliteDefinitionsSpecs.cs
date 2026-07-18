using Acta.Tests.Conformance.Features.Definitions;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Definitions;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqliteDefinitionOverrideBindMatrixSpec : DefinitionOverrideBindMatrixSpec<SqliteConformanceFixture>;

public sealed class SqliteFrameworkJobRegistrationSpec : FrameworkJobRegistrationSpec<SqliteConformanceFixture>;

public sealed class SqliteGetJobDefinitionSpec : GetJobDefinitionSpec<SqliteConformanceFixture>;

public sealed class SqliteListJobDefinitionsFilterMatrixSpec : ListJobDefinitionsFilterMatrixSpec<SqliteConformanceFixture>;

public sealed class SqliteListJobDefinitionsSpec : ListJobDefinitionsSpec<SqliteConformanceFixture>;

public sealed class SqliteMonotonicDefinitionPromotionSpec : MonotonicDefinitionPromotionSpec<SqliteConformanceFixture>;

public sealed class SqliteSetJobDefinitionOverridesSpec : SetJobDefinitionOverridesSpec<SqliteConformanceFixture>;

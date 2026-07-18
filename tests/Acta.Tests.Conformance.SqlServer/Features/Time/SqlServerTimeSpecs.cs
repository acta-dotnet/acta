using Acta.Tests.Conformance.Features.Time;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Time;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerGetUtcNowSpec : GetUtcNowSpec<SqlServerConformanceFixture>;

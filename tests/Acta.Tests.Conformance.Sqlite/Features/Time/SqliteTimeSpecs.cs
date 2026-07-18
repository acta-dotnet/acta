using Acta.Tests.Conformance.Features.Time;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Time;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqliteGetUtcNowSpec : GetUtcNowSpec<SqliteConformanceFixture>;

using Acta.Tests.Conformance.Features.Overview;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Overview;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqliteGetOverviewSpec : GetOverviewSpec<SqliteConformanceFixture>;

using Acta.Tests.Conformance.Features.Overview;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Overview;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerGetOverviewSpec : GetOverviewSpec<SqlServerConformanceFixture>;

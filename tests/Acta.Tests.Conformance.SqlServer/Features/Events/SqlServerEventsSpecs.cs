using Acta.Tests.Conformance.Features.Events;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Events;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerListJobEventsFilterMatrixSpec : ListJobEventsFilterMatrixSpec<SqlServerConformanceFixture>;

public sealed class SqlServerListJobEventsSpec : ListJobEventsSpec<SqlServerConformanceFixture>;

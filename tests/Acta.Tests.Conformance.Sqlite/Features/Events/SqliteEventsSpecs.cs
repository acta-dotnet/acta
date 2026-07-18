using Acta.Tests.Conformance.Features.Events;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Events;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqliteListJobEventsFilterMatrixSpec : ListJobEventsFilterMatrixSpec<SqliteConformanceFixture>;

public sealed class SqliteListJobEventsSpec : ListJobEventsSpec<SqliteConformanceFixture>;

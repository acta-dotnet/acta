using Acta.Tests.Conformance.Features.Events;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Events;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgListJobEventsFilterMatrixSpec : ListJobEventsFilterMatrixSpec<PgConformanceFixture>;

public sealed class PgListJobEventsSpec : ListJobEventsSpec<PgConformanceFixture>;

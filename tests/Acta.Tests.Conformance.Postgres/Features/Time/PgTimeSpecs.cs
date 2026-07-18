using Acta.Tests.Conformance.Features.Time;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Time;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgGetUtcNowSpec : GetUtcNowSpec<PgConformanceFixture>;

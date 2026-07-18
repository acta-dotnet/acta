using Acta.Tests.Conformance.Features.Overview;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Overview;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgGetOverviewSpec : GetOverviewSpec<PgConformanceFixture>;

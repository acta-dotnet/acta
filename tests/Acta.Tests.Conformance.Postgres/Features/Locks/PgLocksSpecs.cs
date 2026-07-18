using Acta.Tests.Conformance.Features.Locks;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Locks;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgAcquireLockSpec : AcquireLockSpec<PgConformanceFixture>;

public sealed class PgExtendLockSpec : ExtendLockSpec<PgConformanceFixture>;

public sealed class PgReleaseLockSpec : ReleaseLockSpec<PgConformanceFixture>;

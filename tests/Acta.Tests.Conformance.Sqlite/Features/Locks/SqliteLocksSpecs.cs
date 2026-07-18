using Acta.Tests.Conformance.Features.Locks;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Locks;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqliteAcquireLockSpec : AcquireLockSpec<SqliteConformanceFixture>;

public sealed class SqliteExtendLockSpec : ExtendLockSpec<SqliteConformanceFixture>;

public sealed class SqliteReleaseLockSpec : ReleaseLockSpec<SqliteConformanceFixture>;

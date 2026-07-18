using Acta.Tests.Conformance.Features.Locks;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Locks;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerAcquireLockSpec : AcquireLockSpec<SqlServerConformanceFixture>;

public sealed class SqlServerExtendLockSpec : ExtendLockSpec<SqlServerConformanceFixture>;

public sealed class SqlServerReleaseLockSpec : ReleaseLockSpec<SqlServerConformanceFixture>;

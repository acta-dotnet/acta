using Acta.Tests.Conformance.Features.Namespaces;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Namespaces;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerListNamespacesSpec : ListNamespacesSpec<SqlServerConformanceFixture>;

public sealed class SqlServerListNamespaceItemsSpec : ListNamespaceItemsSpec<SqlServerConformanceFixture>;

public sealed class SqlServerSuspendResumeNamespaceSpec : SuspendResumeNamespaceSpec<SqlServerConformanceFixture>;

public sealed class SqlServerUpdateNamespaceSpec : UpdateNamespaceSpec<SqlServerConformanceFixture>;

public sealed class SqlServerRegisterNamespaceSpec : RegisterNamespaceSpec<SqlServerConformanceFixture>;

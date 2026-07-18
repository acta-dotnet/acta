using Acta.Tests.Conformance.Features.Namespaces;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Namespaces;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqliteListNamespacesSpec : ListNamespacesSpec<SqliteConformanceFixture>;

public sealed class SqliteListNamespaceItemsSpec : ListNamespaceItemsSpec<SqliteConformanceFixture>;

public sealed class SqliteSuspendResumeNamespaceSpec : SuspendResumeNamespaceSpec<SqliteConformanceFixture>;

public sealed class SqliteUpdateNamespaceMetadataSpec : UpdateNamespaceMetadataSpec<SqliteConformanceFixture>;

public sealed class SqliteRegisterNamespaceSpec : RegisterNamespaceSpec<SqliteConformanceFixture>;

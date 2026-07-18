using Acta.Tests.Conformance.Features.Namespaces;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Namespaces;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgListNamespacesSpec : ListNamespacesSpec<PgConformanceFixture>;

public sealed class PgListNamespaceItemsSpec : ListNamespaceItemsSpec<PgConformanceFixture>;

public sealed class PgSuspendResumeNamespaceSpec : SuspendResumeNamespaceSpec<PgConformanceFixture>;

public sealed class PgUpdateNamespaceMetadataSpec : UpdateNamespaceMetadataSpec<PgConformanceFixture>;

public sealed class PgRegisterNamespaceSpec : RegisterNamespaceSpec<PgConformanceFixture>;

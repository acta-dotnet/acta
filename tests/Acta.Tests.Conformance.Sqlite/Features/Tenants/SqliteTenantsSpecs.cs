using Acta.Tests.Conformance.Features.Tenants;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Tenants;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqliteGetTenantSpec : GetTenantSpec<SqliteConformanceFixture>;

public sealed class SqliteListTenantsSpec : ListTenantsSpec<SqliteConformanceFixture>;

public sealed class SqliteRegisterTenantSpec : RegisterTenantSpec<SqliteConformanceFixture>;

public sealed class SqliteSuspendResumeTenantSpec : SuspendResumeTenantSpec<SqliteConformanceFixture>;

public sealed class SqliteUpdateTenantMetadataSpec : UpdateTenantMetadataSpec<SqliteConformanceFixture>;

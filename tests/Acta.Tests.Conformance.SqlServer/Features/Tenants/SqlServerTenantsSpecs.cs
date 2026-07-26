using Acta.Tests.Conformance.Features.Tenants;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Tenants;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerGetTenantSpec : GetTenantSpec<SqlServerConformanceFixture>;

public sealed class SqlServerListTenantsSpec : ListTenantsSpec<SqlServerConformanceFixture>;

public sealed class SqlServerRegisterTenantSpec : RegisterTenantSpec<SqlServerConformanceFixture>;

public sealed class SqlServerSuspendResumeTenantSpec : SuspendResumeTenantSpec<SqlServerConformanceFixture>;

public sealed class SqlServerUpdateTenantMetadataSpec : UpdateTenantMetadataSpec<SqlServerConformanceFixture>;

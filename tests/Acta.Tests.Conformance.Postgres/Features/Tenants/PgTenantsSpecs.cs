using Acta.Tests.Conformance.Features.Tenants;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Tenants;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgGetTenantSpec : GetTenantSpec<PgConformanceFixture>;

public sealed class PgListTenantsSpec : ListTenantsSpec<PgConformanceFixture>;

public sealed class PgRegisterTenantSpec : RegisterTenantSpec<PgConformanceFixture>;

public sealed class PgSuspendResumeTenantSpec : SuspendResumeTenantSpec<PgConformanceFixture>;

public sealed class PgUpdateTenantMetadataSpec : UpdateTenantMetadataSpec<PgConformanceFixture>;

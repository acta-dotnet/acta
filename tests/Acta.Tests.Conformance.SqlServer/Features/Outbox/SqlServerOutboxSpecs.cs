using Acta.Tests.Conformance.Features.Outbox;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Outbox;

public sealed class SqlServerOutboxClaimSpec : OutboxClaimSpec<SqlServerConformanceFixture>;

public sealed class SqlServerOutboxLeaseRecoverySpec : OutboxLeaseRecoverySpec<SqlServerConformanceFixture>;

public sealed class SqlServerOutboxDeleteSpec : OutboxDeleteSpec<SqlServerConformanceFixture>;

public sealed class SqlServerOutboxRescheduleSpec : OutboxRescheduleSpec<SqlServerConformanceFixture>;

public sealed class SqlServerOutboxQuarantineSpec : OutboxQuarantineSpec<SqlServerConformanceFixture>;

public sealed class SqlServerOutboxReleaseSpec : OutboxReleaseSpec<SqlServerConformanceFixture>;

public sealed class SqlServerOutboxSourceIndependenceSpec : OutboxSourceIndependenceSpec<SqlServerConformanceFixture>;

public sealed class SqlServerOutboxStagingSpec : OutboxStagingSpec<SqlServerConformanceFixture>;

public sealed class SqlServerOutboxDdlSpec : OutboxDdlSpec<SqlServerConformanceFixture>;

public sealed class SqlServerOutboxRelayHandoffSpec : OutboxRelayHandoffSpec<SqlServerConformanceFixture>;

public sealed class SqlServerOutboxRelayQuarantineSpec : OutboxRelayQuarantineSpec<SqlServerConformanceFixture>;

public sealed class SqlServerOutboxRelayRouteSpec : OutboxRelayRouteSpec<SqlServerConformanceFixture>;

public sealed class SqlServerOutboxRelayDispatchSpec : OutboxRelayDispatchSpec<SqlServerConformanceFixture>;

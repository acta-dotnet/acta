using Acta.Tests.Conformance.Features.Outbox;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Outbox;

public sealed class SqliteOutboxClaimSpec : OutboxClaimSpec<SqliteConformanceFixture>;

public sealed class SqliteOutboxLeaseRecoverySpec : OutboxLeaseRecoverySpec<SqliteConformanceFixture>;

public sealed class SqliteOutboxDeleteSpec : OutboxDeleteSpec<SqliteConformanceFixture>;

public sealed class SqliteOutboxRescheduleSpec : OutboxRescheduleSpec<SqliteConformanceFixture>;

public sealed class SqliteOutboxQuarantineSpec : OutboxQuarantineSpec<SqliteConformanceFixture>;

public sealed class SqliteOutboxReleaseSpec : OutboxReleaseSpec<SqliteConformanceFixture>;

public sealed class SqliteOutboxSourceIndependenceSpec : OutboxSourceIndependenceSpec<SqliteConformanceFixture>;

public sealed class SqliteOutboxStagingSpec : OutboxStagingSpec<SqliteConformanceFixture>;

public sealed class SqliteOutboxDdlSpec : OutboxDdlSpec<SqliteConformanceFixture>;

public sealed class SqliteOutboxRelayHandoffSpec : OutboxRelayHandoffSpec<SqliteConformanceFixture>;

public sealed class SqliteOutboxRelayQuarantineSpec : OutboxRelayQuarantineSpec<SqliteConformanceFixture>;

public sealed class SqliteOutboxRelayRouteSpec : OutboxRelayRouteSpec<SqliteConformanceFixture>;

public sealed class SqliteOutboxRelayDispatchSpec : OutboxRelayDispatchSpec<SqliteConformanceFixture>;

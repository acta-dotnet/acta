using Acta.Tests.Conformance.Features.Outbox;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Outbox;

public sealed class PgOutboxClaimSpec : OutboxClaimSpec<PgConformanceFixture>;

public sealed class PgOutboxLeaseRecoverySpec : OutboxLeaseRecoverySpec<PgConformanceFixture>;

public sealed class PgOutboxDeleteSpec : OutboxDeleteSpec<PgConformanceFixture>;

public sealed class PgOutboxRescheduleSpec : OutboxRescheduleSpec<PgConformanceFixture>;

public sealed class PgOutboxQuarantineSpec : OutboxQuarantineSpec<PgConformanceFixture>;

public sealed class PgOutboxReleaseSpec : OutboxReleaseSpec<PgConformanceFixture>;

public sealed class PgOutboxBacklogCountSpec : OutboxBacklogCountSpec<PgConformanceFixture>;

public sealed class PgOutboxListQuarantinedSpec : OutboxListQuarantinedSpec<PgConformanceFixture>;

public sealed class PgOutboxRequeueSpec : OutboxRequeueSpec<PgConformanceFixture>;

public sealed class PgOutboxDiscardSpec : OutboxDiscardSpec<PgConformanceFixture>;

public sealed class PgOutboxSignalInboxSpec : OutboxSignalInboxSpec<PgConformanceFixture>;

public sealed class PgOutboxSignalEvidenceSpec : OutboxSignalEvidenceSpec<PgConformanceFixture>;

public sealed class PgOutboxRelaySignalApplySpec : OutboxRelaySignalApplySpec<PgConformanceFixture>;

public sealed class PgOutboxOperatorFacadeSpec : OutboxOperatorFacadeSpec<PgConformanceFixture>;

public sealed class PgOutboxSourceIndependenceSpec : OutboxSourceIndependenceSpec<PgConformanceFixture>;

public sealed class PgOutboxStagingSpec : OutboxStagingSpec<PgConformanceFixture>;

public sealed class PgOutboxLaneOrderSpec : OutboxLaneOrderSpec<PgConformanceFixture>;

public sealed class PgOutboxDdlSpec : OutboxDdlSpec<PgConformanceFixture>;

public sealed class PgOutboxRelayHandoffSpec : OutboxRelayHandoffSpec<PgConformanceFixture>;

public sealed class PgOutboxRelayQuarantineSpec : OutboxRelayQuarantineSpec<PgConformanceFixture>;

public sealed class PgOutboxRelayRouteSpec : OutboxRelayRouteSpec<PgConformanceFixture>;

public sealed class PgOutboxRelayDispatchSpec : OutboxRelayDispatchSpec<PgConformanceFixture>;

public sealed class PgOutboxLaneHoldSpec : OutboxLaneHoldSpec<PgConformanceFixture>;

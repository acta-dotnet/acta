using Acta.Tests.Conformance.Postgres.Testing;
using Acta.Tests.Conformance.Runtime;

namespace Acta.Tests.Conformance.Postgres.Runtime;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgAttemptOverlapChaosSpec : AttemptOverlapChaosSpec<PgConformanceFixture>;

public sealed class PgCancelPropagatesToHandlerSpec : CancelPropagatesToHandlerSpec<PgConformanceFixture>;

public sealed class PgClaimAndControlRaceChaosSpec : ClaimAndControlRaceChaosSpec<PgConformanceFixture>;

public sealed class PgClaimBatchSpec : ClaimBatchSpec<PgConformanceFixture>;

public sealed class PgCombinedDispatchParitySpec : CombinedDispatchParitySpec<PgConformanceFixture>;

public sealed class PgBulkCompletionParitySpec : BulkCompletionParitySpec<PgConformanceFixture>;

public sealed class PgClockSkewInitializationChaosSpec : ClockSkewInitializationChaosSpec<PgConformanceFixture>;

public sealed class PgCompleteAndClockChaosSpec : CompleteAndClockChaosSpec<PgConformanceFixture>;

public sealed class PgExecutionTimeoutSpec : ExecutionTimeoutSpec<PgConformanceFixture>;

public sealed class PgInputDeserializationFailureSpec : InputDeserializationFailureSpec<PgConformanceFixture>;

public sealed class PgTimeoutRetryBudgetSpec : TimeoutRetryBudgetSpec<PgConformanceFixture>;

public sealed class PgGetJobResultSpec : GetJobResultSpec<PgConformanceFixture>;

public sealed class PgHandlerLockHeartbeatSpec : HandlerLockHeartbeatSpec<PgConformanceFixture>;

public sealed class PgJobContextDiResolutionSpec : JobContextDiResolutionSpec<PgConformanceFixture>;

public sealed class PgJobAttemptIdentitySpec : JobAttemptIdentitySpec<PgConformanceFixture>;

public sealed class PgSchedulePauseFireRaceChaosSpec : SchedulePauseFireRaceChaosSpec<PgConformanceFixture>;

public sealed class PgDeadlineSpec : DeadlineSpec<PgConformanceFixture>;

public sealed class PgJobRefContextSpec : JobRefContextSpec<PgConformanceFixture>;

public sealed class PgRecoveryDuplicationChaosSpec : RecoveryDuplicationChaosSpec<PgConformanceFixture>;

public sealed class PgMultiWorkerRegistrationSpec : MultiWorkerRegistrationSpec<PgConformanceFixture>;

public sealed class PgOneShotRetrySpec : OneShotRetrySpec<PgConformanceFixture>;

public sealed class PgPayloadSizeLimitSpec : PayloadSizeLimitSpec<PgConformanceFixture>;

public sealed class PgSignalStepWakeChaosSpec : SignalStepWakeChaosSpec<PgConformanceFixture>;

public sealed class PgSignalSuspendHandoffRaceChaosSpec : SignalSuspendHandoffRaceChaosSpec<PgConformanceFixture>;

public sealed class PgWorkerCrashRecoveryChaosSpec : WorkerCrashRecoveryChaosSpec<PgConformanceFixture>;

public sealed class PgWorkerLoopDispatchSpec : WorkerLoopDispatchSpec<PgConformanceFixture>;

public sealed class PgBufferedWorkerShutdownDrainSpec : BufferedWorkerShutdownDrainSpec<PgConformanceFixture>;

public sealed class PgDirectWorkerShutdownDrainSpec : DirectWorkerShutdownDrainSpec<PgConformanceFixture>;

public sealed class PgBulkWorkerShutdownDrainSpec : BulkWorkerShutdownDrainSpec<PgConformanceFixture>;

public sealed class PgBufferedWorkerDrainSpec : BufferedWorkerDrainSpec<PgConformanceFixture>;

public sealed class PgDirectWorkerDrainSpec : DirectWorkerDrainSpec<PgConformanceFixture>;

public sealed class PgBulkWorkerDrainSpec : BulkWorkerDrainSpec<PgConformanceFixture>;

public sealed class PgWorkerRuntimeRegistrationSpec : WorkerRuntimeRegistrationSpec<PgConformanceFixture>;

public sealed class PgCompletionSinkBulkFallbackSpec : CompletionSinkBulkFallbackSpec<PgConformanceFixture>;

using Acta.Tests.Conformance.Runtime;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Runtime;

// Runtime specs, including the concurrency/chaos/multi-worker ones: multiple worker processes (or
// concurrent executors) against one embedded database file behave as concurrent workers (SQLite
// serializes them under a per-operation BEGIN IMMEDIATE), so SQLite binds them all.

public sealed class SqliteCancelPropagatesToHandlerSpec : CancelPropagatesToHandlerSpec<SqliteConformanceFixture>;

public sealed class SqliteAttemptOverlapChaosSpec : AttemptOverlapChaosSpec<SqliteConformanceFixture>;

public sealed class SqliteClaimAndControlRaceChaosSpec : ClaimAndControlRaceChaosSpec<SqliteConformanceFixture>;

public sealed class SqliteClockSkewInitializationChaosSpec : ClockSkewInitializationChaosSpec<SqliteConformanceFixture>;

public sealed class SqliteCompleteAndClockChaosSpec : CompleteAndClockChaosSpec<SqliteConformanceFixture>;

public sealed class SqliteCombinedDispatchParitySpec : CombinedDispatchParitySpec<SqliteConformanceFixture>;

public sealed class SqliteBulkCompletionParitySpec : BulkCompletionParitySpec<SqliteConformanceFixture>;

public sealed class SqliteHandlerLockHeartbeatSpec : HandlerLockHeartbeatSpec<SqliteConformanceFixture>;

public sealed class SqliteRecoveryDuplicationChaosSpec : RecoveryDuplicationChaosSpec<SqliteConformanceFixture>;

public sealed class SqliteMultiWorkerRegistrationSpec : MultiWorkerRegistrationSpec<SqliteConformanceFixture>;

public sealed class SqliteSignalStepWakeChaosSpec : SignalStepWakeChaosSpec<SqliteConformanceFixture>;

public sealed class SqliteSignalSuspendHandoffRaceChaosSpec : SignalSuspendHandoffRaceChaosSpec<SqliteConformanceFixture>;

public sealed class SqliteWorkerCrashRecoveryChaosSpec : WorkerCrashRecoveryChaosSpec<SqliteConformanceFixture>;

public sealed class SqliteClaimBatchSpec : ClaimBatchSpec<SqliteConformanceFixture>;

public sealed class SqliteExecutionTimeoutSpec : ExecutionTimeoutSpec<SqliteConformanceFixture>;

public sealed class SqliteInputDeserializationFailureSpec : InputDeserializationFailureSpec<SqliteConformanceFixture>;

public sealed class SqliteTimeoutRetryBudgetSpec : TimeoutRetryBudgetSpec<SqliteConformanceFixture>;

public sealed class SqliteGetJobResultSpec : GetJobResultSpec<SqliteConformanceFixture>;

public sealed class SqliteJobContextDiResolutionSpec : JobContextDiResolutionSpec<SqliteConformanceFixture>;

public sealed class SqliteJobAttemptIdentitySpec : JobAttemptIdentitySpec<SqliteConformanceFixture>;

public sealed class SqliteSchedulePauseFireRaceChaosSpec : SchedulePauseFireRaceChaosSpec<SqliteConformanceFixture>;

public sealed class SqliteDeadlineSpec : DeadlineSpec<SqliteConformanceFixture>;

public sealed class SqliteJobRefContextSpec : JobRefContextSpec<SqliteConformanceFixture>;

public sealed class SqliteOneShotRetrySpec : OneShotRetrySpec<SqliteConformanceFixture>;

public sealed class SqlitePayloadSizeLimitSpec : PayloadSizeLimitSpec<SqliteConformanceFixture>;

public sealed class SqliteWorkerLoopDispatchSpec : WorkerLoopDispatchSpec<SqliteConformanceFixture>;

public sealed class SqliteBufferedWorkerShutdownDrainSpec : BufferedWorkerShutdownDrainSpec<SqliteConformanceFixture>;

public sealed class SqliteDirectWorkerShutdownDrainSpec : DirectWorkerShutdownDrainSpec<SqliteConformanceFixture>;

public sealed class SqliteBulkWorkerShutdownDrainSpec : BulkWorkerShutdownDrainSpec<SqliteConformanceFixture>;

public sealed class SqliteBufferedWorkerDrainSpec : BufferedWorkerDrainSpec<SqliteConformanceFixture>;

public sealed class SqliteDirectWorkerDrainSpec : DirectWorkerDrainSpec<SqliteConformanceFixture>;

public sealed class SqliteBulkWorkerDrainSpec : BulkWorkerDrainSpec<SqliteConformanceFixture>;

public sealed class SqliteWorkerRuntimeRegistrationSpec : WorkerRuntimeRegistrationSpec<SqliteConformanceFixture>;

public sealed class SqliteCompletionSinkBulkFallbackSpec : CompletionSinkBulkFallbackSpec<SqliteConformanceFixture>;

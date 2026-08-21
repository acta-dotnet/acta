using Acta.Tests.Conformance.Runtime;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Runtime;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerAttemptOverlapChaosSpec : AttemptOverlapChaosSpec<SqlServerConformanceFixture>;

public sealed class SqlServerCancelPropagatesToHandlerSpec : CancelPropagatesToHandlerSpec<SqlServerConformanceFixture>;

public sealed class SqlServerClaimAndControlRaceChaosSpec : ClaimAndControlRaceChaosSpec<SqlServerConformanceFixture>;

public sealed class SqlServerClaimBatchSpec : ClaimBatchSpec<SqlServerConformanceFixture>;

public sealed class SqlServerCombinedDispatchParitySpec : CombinedDispatchParitySpec<SqlServerConformanceFixture>;

public sealed class SqlServerBulkCompletionParitySpec : BulkCompletionParitySpec<SqlServerConformanceFixture>;

public sealed class SqlServerClockSkewInitializationChaosSpec : ClockSkewInitializationChaosSpec<SqlServerConformanceFixture>;

public sealed class SqlServerCompleteAndClockChaosSpec : CompleteAndClockChaosSpec<SqlServerConformanceFixture>;

public sealed class SqlServerExecutionTimeoutSpec : ExecutionTimeoutSpec<SqlServerConformanceFixture>;

public sealed class SqlServerInputDeserializationFailureSpec : InputDeserializationFailureSpec<SqlServerConformanceFixture>;

public sealed class SqlServerTimeoutRetryBudgetSpec : TimeoutRetryBudgetSpec<SqlServerConformanceFixture>;

public sealed class SqlServerGetJobResultSpec : GetJobResultSpec<SqlServerConformanceFixture>;

public sealed class SqlServerHandlerLockHeartbeatSpec : HandlerLockHeartbeatSpec<SqlServerConformanceFixture>;

public sealed class SqlServerJobContextDiResolutionSpec : JobContextDiResolutionSpec<SqlServerConformanceFixture>;

public sealed class SqlServerJobAttemptIdentitySpec : JobAttemptIdentitySpec<SqlServerConformanceFixture>;

public sealed class SqlServerSchedulePauseFireRaceChaosSpec : SchedulePauseFireRaceChaosSpec<SqlServerConformanceFixture>;

public sealed class SqlServerDeadlineSpec : DeadlineSpec<SqlServerConformanceFixture>;

public sealed class SqlServerJobRefContextSpec : JobRefContextSpec<SqlServerConformanceFixture>;

public sealed class SqlServerRecoveryDuplicationChaosSpec : RecoveryDuplicationChaosSpec<SqlServerConformanceFixture>;

public sealed class SqlServerMultiWorkerRegistrationSpec : MultiWorkerRegistrationSpec<SqlServerConformanceFixture>;

public sealed class SqlServerOneShotRetrySpec : OneShotRetrySpec<SqlServerConformanceFixture>;

public sealed class SqlServerPayloadSizeLimitSpec : PayloadSizeLimitSpec<SqlServerConformanceFixture>;

public sealed class SqlServerSignalStepWakeChaosSpec : SignalStepWakeChaosSpec<SqlServerConformanceFixture>;

public sealed class SqlServerSignalSuspendHandoffRaceChaosSpec : SignalSuspendHandoffRaceChaosSpec<SqlServerConformanceFixture>;

public sealed class SqlServerWorkerCrashRecoveryChaosSpec : WorkerCrashRecoveryChaosSpec<SqlServerConformanceFixture>;

public sealed class SqlServerWorkerLoopDispatchSpec : WorkerLoopDispatchSpec<SqlServerConformanceFixture>;

public sealed class SqlServerBufferedWorkerShutdownDrainSpec : BufferedWorkerShutdownDrainSpec<SqlServerConformanceFixture>;

public sealed class SqlServerDirectWorkerShutdownDrainSpec : DirectWorkerShutdownDrainSpec<SqlServerConformanceFixture>;

public sealed class SqlServerBulkWorkerShutdownDrainSpec : BulkWorkerShutdownDrainSpec<SqlServerConformanceFixture>;

public sealed class SqlServerBufferedWorkerDrainSpec : BufferedWorkerDrainSpec<SqlServerConformanceFixture>;

public sealed class SqlServerDirectWorkerDrainSpec : DirectWorkerDrainSpec<SqlServerConformanceFixture>;

public sealed class SqlServerBulkWorkerDrainSpec : BulkWorkerDrainSpec<SqlServerConformanceFixture>;

public sealed class SqlServerWorkerRuntimeRegistrationSpec : WorkerRuntimeRegistrationSpec<SqlServerConformanceFixture>;

public sealed class SqlServerCompletionSinkBulkFallbackSpec : CompletionSinkBulkFallbackSpec<SqlServerConformanceFixture>;

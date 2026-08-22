using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Checkpoints;
using Acta.Runtime.Modules.Execution.ChildLatches;
using Acta.Runtime.Modules.Execution.Timers;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Services.Locks;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// Drives one real <see cref="JobExecution.RunAsync"/> attempt end to end over a scripted
/// <see cref="IExecutionStore"/>: the start CAS, a handler that runs one durable step, and the
/// completion CAS. The script says what <c>complete_step</c> and <c>complete_execution</c> answer,
/// and the harness keeps every <see cref="CompleteExecutionRequest"/> the runner submitted plus the
/// subset the store's CAS actually applied, so a test can assert on the completion shape rather than
/// on a database. Reusable seam for executor unit tests; add scripted knobs here, not per test.
/// </summary>
internal sealed class JobExecutionHarness(
    CompleteStepOutcomeCode stepOutcome = CompleteStepOutcomeCode.Succeeded,
    CompleteExecutionAction completionAction = CompleteExecutionAction.Completed,
    bool cancelAttemptOnStepCompletion = false,
    short maxAttempts = 3,
    short failureCount = 0
)
{
    /// <summary>The step the default handler runs; asserted on by name in the ownership pins.</summary>
    public const string StepName = "charge-card";

    private const string JobName = "harness-job";
    private const int WorkerId = 7;

    private readonly CancellationTokenSource _attemptCts = new();

    // The execution-timeout source. In production JobExecutor links it into the attempt token, so
    // firing the timeout cancels the attempt; TimeOutAttempt below reproduces that pairing.
    private readonly CancellationTokenSource _timeoutCts = new();
    private readonly ScriptedExecutionStore _store = new(stepOutcome, completionAction);

    /// <summary>Every completion command the runner handed the store, in submission order.</summary>
    public IReadOnlyList<CompleteExecutionRequest> Submitted => _store.Submitted;

    /// <summary>The subset whose completion CAS matched a row; a NotOwner answer writes nothing.</summary>
    public IReadOnlyList<CompleteExecutionRequest> Applied => _store.Applied;

    /// <summary>The single completion command, for the pins that expect exactly one.</summary>
    public CompleteExecutionRequest Completion => Assert.Single(Submitted);

    /// <summary>
    /// Fires this attempt's execution timeout the way the production watchdog does: the timeout source
    /// is cancelled, which cancels the attempt token linked to it. Call from inside a handler; the
    /// handler must then observe its token, exactly as a cooperative handler does in production.
    /// </summary>
    public void TimeOutAttempt()
    {
        _timeoutCts.Cancel();
        _attemptCts.Cancel();
    }

    /// <summary>
    /// Runs one attempt. The default handler invokes <c>ctx.RunStepAsync</c> once with a body that
    /// succeeds, so the scripted <c>complete_step</c> answer is what decides the outcome.
    /// </summary>
    public async Task<RunOnceOutcome> RunAsync(Func<JobContext, CancellationToken, Task>? handler = null)
    {
        handler ??= static (ctx, token) => ctx.RunStepAsync(StepName, static _ => Task.CompletedTask, ct: token);
        _store.OnStepCompletion = cancelAttemptOnStepCompletion ? _attemptCts.Cancel : null;

        var options = Options.Create(new JobsOptions());
        var job = Job(failureCount);
        var context = new RuntimeJobContext(
            job,
            jobName: JobName,
            namespaceName: "harness",
            namespaceId: 1,
            leaseTtlSeconds: options.Value.LeaseTtlSeconds,
            jobStore: null!,
            signalStore: null!,
            alerts: null!,
            executionStore: _store,
            new HarnessSerializers(),
            new UnusedLockStore(),
            cancellationToken: _attemptCts.Token,
            triggeringScheduleNames: [],
            deadlineAtUtc: null,
            // The two the production JobExecutor supplies and this harness used to default away:
            // without the cap a handler write is unbounded here but bounded in production, and
            // without the attempt every timeout reads as a plain external cancel.
            maxInlinePayloadBytes: options.Value.MaxInlinePayloadBytes,
            runningAttempt: new RunningAttempt(_attemptCts, _timeoutCts),
            workerId: WorkerId
        );

        var execution = new JobExecution(
            jobStore: null!,
            _store,
            new HarnessSerializers(),
            options,
            new JobBehaviorPipeline([]),
            new WorkerWakeupPublisher(new InProcessWakeup())
        );

        return await execution.RunAsync(
            EmptyServices.Instance,
            Descriptor(handler, maxAttempts),
            job,
            context,
            WorkerId,
            isRecurring: false,
            fireOutcome: null,
            alreadyStarted: false,
            CancellationToken.None
        );
    }

    private static ClaimedJob Job(short failureCount) =>
        new(
            JobId: 4242,
            JobRef: Guid.CreateVersion7(),
            NamespaceId: 1,
            DefinitionId: 1,
            TenantId: null,
            ExecutionNumber: 3,
            DeduplicationKey: null,
            CorrelationKey: null,
            ExclusiveKey: null,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            NextRunAtUtc: null,
            LeaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(3),
            CreatedAtUtc: DateTime.UtcNow,
            FailureCount: failureCount,
            Version: 1
        );

    private static JobDescriptor Descriptor(Func<JobContext, CancellationToken, Task> handler, short maxAttempts) =>
        new(
            JobName: JobName,
            HandlerType: typeof(JobExecutionHarness),
            MethodName: "N/A",
            InputType: typeof(NoInput),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.None,
            OutputPayloadFormat: null,
            InvocationKind: JobInvocationKind.Task,
            RequiresJobContextParameter: true,
            RequiresCancellationToken: true,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: maxAttempts,
            AuditLevel: JobAuditLevelCode.Audit,
            AlertProfile: AlertProfileCode.OnFailure,
            Invoker: async (_, _, ctx, token) =>
            {
                await handler(ctx, token);
                return new JobHandlerInvocationResult(false, null);
            },
            DeserializeInput: static (_, _) => new NoInput(),
            SerializeOutput: null
        );

    // The scripted store. Only the five calls one attempt makes are implemented; everything else
    // throws so a future change that starts leaning on another port is visible rather than silent.
    private sealed class ScriptedExecutionStore(CompleteStepOutcomeCode stepOutcome, CompleteExecutionAction completionAction)
        : IExecutionStore
    {
        private readonly List<CompleteExecutionRequest> _submitted = [];
        private readonly List<CompleteExecutionRequest> _applied = [];

        public IReadOnlyList<CompleteExecutionRequest> Submitted => _submitted;
        public IReadOnlyList<CompleteExecutionRequest> Applied => _applied;

        // Fires as complete_step answers, so a test can model the heartbeat cancelling the attempt
        // token the instant the slot is proven stolen.
        public Action? OnStepCompletion { get; set; }

        public Task<StartExecutionAction> StartExecutionAsync(
            long jobId,
            int workerId,
            int expectedExecutionNumber,
            int expectedVersion,
            int leaseTtlSeconds,
            CancellationToken ct
        ) => Task.FromResult(StartExecutionAction.Started);

        public Task<StartStepDecision> StartStepAsync(long jobId, string name, bool atMostOnce, CancellationToken ct) =>
            Task.FromResult(
                new StartStepDecision(
                    StartStepOutcomeCode.Invoke,
                    AttemptNumber: 1,
                    Version: 1,
                    NextRetryAtUtc: null,
                    ResultFormatId: 0,
                    Result: null,
                    ReasonCode: null,
                    ReasonMessage: null
                )
            );

        public Task<CompleteStepDecision> CompleteStepAsync(CompleteStepCommand command, CancellationToken ct)
        {
            OnStepCompletion?.Invoke();
            return Task.FromResult(new CompleteStepDecision(stepOutcome, NextRetryAtUtc: DateTime.UtcNow.AddSeconds(30)));
        }

        public Task<CompleteExecutionResult> CompleteExecutionAsync(CompleteExecutionRequest request, CancellationToken ct)
        {
            _submitted.Add(request);
            if (completionAction != CompleteExecutionAction.Completed)
            {
                // The CAS matched no row: nothing is written, and the reported status is the row's
                // current one rather than anything this attempt asked for.
                return Task.FromResult(
                    new CompleteExecutionResult(
                        completionAction,
                        (byte)JobStatusCode.Executing,
                        FinalNextRunAtUtc: null,
                        DateTime.UtcNow,
                        ParentReleased: false
                    )
                );
            }

            _applied.Add(request);
            return Task.FromResult(
                new CompleteExecutionResult(
                    CompleteExecutionAction.Completed,
                    FinalStatus(request),
                    FinalNextRunAtUtc: null,
                    DateTime.UtcNow,
                    ParentReleased: false
                )
            );
        }

        // Mirrors what complete_execution lands the job in for the shapes this harness produces: a
        // re-arm goes Ready, a handler-control decision takes the requested status, and everything
        // else follows the attempt outcome.
        private static byte FinalStatus(CompleteExecutionRequest request) =>
            request switch
            {
                { RescheduleStatusCode: not null } => (byte)JobStatusCode.Ready,
                { HandlerStatusCode: { } handler } => handler,
                { Outcome: ExecutionOutcome.Succeeded } => (byte)JobStatusCode.Succeeded,
                { Outcome: ExecutionOutcome.Cancelled } => (byte)JobStatusCode.Cancelled,
                _ => (byte)JobStatusCode.Failed,
            };

        public Task<ClaimResult> ClaimBatchAsync(ClaimRequest request, int leaseTtlSeconds, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ClaimResult> ClaimOneAsync(ClaimRequest request, int leaseTtlSeconds, long? jobId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<bool>> CompleteExecutionsBatchAsync(
            IReadOnlyList<CompleteExecutionRequest> requests,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<ReclaimStuckJobsResult> ReclaimStuckJobsAsync(short namespaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CheckpointSlotRow> CheckpointSlotAsync(CheckpointSlotCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RecordJobNoteAsync(long jobId, string message, JobPayload? detail, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> GetChildJobIdsAsync(long parentJobId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<StaleChildLatch>> GetStaleChildLatchesAsync(short namespaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SleepDecision> ArmOrConsumeSleepTimerAsync(ArmOrConsumeSleepTimerCommand command, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }

    // The attempt itself carries no payload, but a handler can write a variable or progress value, and
    // those go through the real JSON serializer so the inline-size cap is reached the way production
    // reaches it. Every other format still throws, keeping an accidental dependency visible.
    private sealed class HarnessSerializers : IJobPayloadSerializerRegistry
    {
        public IJobPayloadSerializer Resolve(byte formatId) =>
            formatId == JobPayloadFormat.Json.Id ? JsonJobPayloadSerializer.Default : throw new NotSupportedException();

        public bool IsRegistered(byte formatId) => formatId == JobPayloadFormat.Json.Id;
    }

    private sealed class UnusedLockStore : ILockStore
    {
        public Task<LockToken?> TryAcquireAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ExtendAsync(LockToken token, TimeSpan ttl, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> ReleaseAsync(LockToken token, CancellationToken ct) => throw new NotSupportedException();
    }
}

using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Checkpoints;
using Acta.Runtime.Modules.Execution.ChildLatches;
using Acta.Runtime.Modules.Execution.Timers;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Services.Locks;
using Acta.Runtime.Services.Time;
using Microsoft.Extensions.Logging;
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
    short failureCount = 0,
    int? maxInlinePayloadBytes = null,
    StartExecutionAction startAction = StartExecutionAction.Started,
    bool startFailsOnce = false,
    StartExecutionAction? startAfterFailure = null,
    string? concurrencyKey = null,
    short? concurrencyLimit = null,
    bool slotGranted = true
)
{
    /// <summary>The step the default handler runs; asserted on by name in the ownership pins.</summary>
    public const string StepName = "charge-card";

    private const string JobName = "harness-job";
    private const int WorkerId = 7;

    private readonly CancellationTokenSource _attemptCts = new();

    // The execution-timeout source, handed to the RunningAttempt so a cancellation can be told from an
    // external one. Unlinked from _attemptCts here; TimeOutAttempt cancels both.
    private readonly CancellationTokenSource _timeoutCts = new();
    private readonly ScriptedExecutionStore _store = new(stepOutcome, completionAction, startAction, startFailsOnce, startAfterFailure);
    private readonly ScriptedLockStore _locks = new(slotGranted);
    private readonly RecordingLogger _log = new();

    /// <summary>Every concurrency-slot acquire the runner issued, in order.</summary>
    public IReadOnlyList<SlotRequest> SlotRequests => _locks.SlotRequests;

    /// <summary>How many held slots the runner released.</summary>
    public int SlotReleases => _locks.SlotReleases;

    /// <summary>Every completion command the runner handed the store, in submission order.</summary>
    public IReadOnlyList<CompleteExecutionRequest> Submitted => _store.Submitted;

    /// <summary>How many times the runner issued the start write.</summary>
    public int StartAttempts => _store.StartAttempts;

    /// <summary>Every log line the runner wrote, for the arms whose whole contract is what they say.</summary>
    public IReadOnlyList<LogEntry> Log => _log.Entries;

    /// <summary>Whether the attempt ever reached <c>start_step</c>, i.e. whether the handler ran at all.</summary>
    public bool HandlerRan => _store.StartedSteps.Count > 0;

    /// <summary>The subset whose completion CAS matched a row; a NotOwner answer writes nothing.</summary>
    public IReadOnlyList<CompleteExecutionRequest> Applied => _store.Applied;

    /// <summary>The single completion command, for the pins that expect exactly one.</summary>
    public CompleteExecutionRequest Completion => Assert.Single(Submitted);

    /// <summary>
    /// Puts the attempt into the state a fired execution timeout leaves it in: the timeout source is
    /// cancelled (so <c>RunningAttempt.TimedOut</c> reads true) and the attempt source is cancelled
    /// after it. The two are cancelled separately because this harness owns them separately, where
    /// production links the attempt token to the timeout source and gets the second cancel for free.
    /// Call from inside a handler; the handler must then observe its token, as a cooperative handler
    /// does in production.
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
        if (maxInlinePayloadBytes is { } configuredCap)
        {
            // One options instance feeds both the attempt context and JobExecution, so a configured
            // cap moves the handler-write limit and the result-drop limit together, as it does in a host.
            options.Value.MaxInlinePayloadBytes = configuredCap;
        }

        var job = Job(failureCount, concurrencyKey);
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
            _locks,
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
            new WorkerWakeupPublisher(new InProcessWakeup()),
            _log
        );

        return await execution.RunAsync(
            EmptyServices.Instance,
            Descriptor(handler, maxAttempts, concurrencyLimit),
            job,
            context,
            WorkerId,
            isRecurring: false,
            fireOutcome: null,
            alreadyStarted: false,
            CancellationToken.None
        );
    }

    /// <summary>
    /// Drives one claimed job through the real <see cref="JobExecutor"/> with an EMPTY descriptor
    /// index: the state a deployment that dropped the definition leaves a worker in. The scripted
    /// store is the same one, so a test reads the released claim off <see cref="Submitted"/> and the
    /// bounce off <see cref="Log"/>.
    /// </summary>
    public async Task<RunOnceOutcome> RunWithNoDescriptorAsync()
    {
        var options = Options.Create(new JobsOptions());
        var serializers = new HarnessSerializers();
        var executor = new JobExecutor(
            _locks,
            new HarnessClock(),
            serializers,
            new StoreOnlyServices(_store),
            options,
            new WorkerContext(null),
            new JobExecution(
                jobStore: null!,
                _store,
                serializers,
                options,
                new JobBehaviorPipeline([]),
                new WorkerWakeupPublisher(new InProcessWakeup()),
                _log
            ),
            _log
        );

        return await executor.ExecuteClaimedJobAsync(
            Job(failureCount, concurrencyKey),
            namespaceName: "harness",
            namespaceId: 1,
            WorkerId,
            alreadyStarted: false,
            CancellationToken.None
        );
    }

    private static ClaimedJob Job(short failureCount, string? concurrencyKey) =>
        new(
            JobId: 4242,
            JobRef: Guid.CreateVersion7(),
            NamespaceId: 1,
            DefinitionId: 1,
            TenantId: null,
            ExecutionNumber: 3,
            DeduplicationKey: null,
            CorrelationKey: null,
            ConcurrencyKey: concurrencyKey,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            NextRunAtUtc: null,
            LeaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(3),
            CreatedAtUtc: DateTime.UtcNow,
            FailureCount: failureCount,
            Version: 1
        );

    private static JobDescriptor Descriptor(
        Func<JobContext, CancellationToken, Task> handler,
        short maxAttempts,
        short? concurrencyLimit
    ) =>
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
        )
        {
            ConcurrencyLimit = concurrencyLimit,
        };

    // The scripted store. Only the five calls one attempt makes are implemented; everything else
    // throws so a future change that starts leaning on another port is visible rather than silent.
    private sealed class ScriptedExecutionStore(
        CompleteStepOutcomeCode stepOutcome,
        CompleteExecutionAction completionAction,
        StartExecutionAction startAction,
        bool startFailsOnce,
        StartExecutionAction? startAfterFailure
    ) : IExecutionStore
    {
        private readonly List<CompleteExecutionRequest> _submitted = [];
        private readonly List<CompleteExecutionRequest> _applied = [];
        private readonly List<string> _startedSteps = [];
        private bool _startFailsOnce = startFailsOnce;

        /// <summary>How many times the start write was attempted, so a retry is visible.</summary>
        public int StartAttempts { get; private set; }

        public IReadOnlyList<CompleteExecutionRequest> Submitted => _submitted;
        public IReadOnlyList<CompleteExecutionRequest> Applied => _applied;
        public IReadOnlyList<string> StartedSteps => _startedSteps;

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
        )
        {
            StartAttempts++;
            if (_startFailsOnce)
            {
                _startFailsOnce = false;
                throw new ProviderDown();
            }
            // startAfterFailure models a first try that committed and lost only its response: the retry
            // resubmits a stale version and the CAS answers LostClaim although this worker holds the row.
            return Task.FromResult(StartAttempts > 1 && startAfterFailure is { } after ? after : startAction);
        }

        // The one exception class the retry helper repeats: a provider fault, not a handler fault.
        private sealed class ProviderDown() : System.Data.Common.DbException("connection dropped");

        public Task<StartStepDecision> StartStepAsync(long jobId, string name, bool atMostOnce, CancellationToken ct)
        {
            _startedSteps.Add(name);
            return Task.FromResult(
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
        }

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

        public Task<ReclaimStuckJobsResult> ReclaimStuckJobsAsync(int namespaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<RecoverySlotRepair> RepairRecoverySlotAsync(int namespaceId, long jobId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CheckpointSlotRow> CheckpointSlotAsync(CheckpointSlotCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RecordJobNoteAsync(long jobId, int executionNumber, string message, JobPayload? detail, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> GetChildJobIdsAsync(long parentJobId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<StaleChildLatch>> GetStaleChildLatchesAsync(int namespaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SleepDecision> ArmOrConsumeSleepTimerAsync(ArmOrConsumeSleepTimerCommand command, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>One line the runner wrote: enough to pin an arm whose whole contract is the log.</summary>
    internal sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }

    // The root provider JobExecutor resolves its store from. Only the store is answered, so a path
    // that starts resolving anything else fails loudly instead of silently taking a null.
    private sealed class StoreOnlyServices(IExecutionStore store) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IExecutionStore) ? store : null;
    }

    private sealed class HarnessClock : IActaClock
    {
        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(DateTime.UnixEpoch);
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

    /// <summary>
    /// Scripted concurrency-slot store. It answers the slot acquire the way the script says, records
    /// what the runner asked for, and counts releases; the handler-facing acquire stays unsupported,
    /// so an attempt that starts using it is visible rather than silent.
    /// </summary>
    private sealed class ScriptedLockStore(bool slotGranted) : ILockStore
    {
        private readonly List<SlotRequest> _slotRequests = [];

        public IReadOnlyList<SlotRequest> SlotRequests => _slotRequests;

        public int SlotReleases { get; private set; }

        public Task<LockToken?> TryAcquireAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<LockToken?> TryAcquireSlotAsync(string keyPrefix, int limit, TimeSpan ttl, long ownerJobId, CancellationToken ct)
        {
            _slotRequests.Add(new SlotRequest(keyPrefix, limit));
            return Task.FromResult(slotGranted ? new LockToken($"{keyPrefix}.0", Guid.NewGuid()) : (LockToken?)null);
        }

        public Task<bool> ExtendAsync(LockToken token, TimeSpan ttl, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> ReleaseAsync(LockToken token, CancellationToken ct)
        {
            SlotReleases++;
            return Task.FromResult(true);
        }
    }

    /// <summary>One concurrency-slot acquire the runner issued: the composed key prefix and the limit.</summary>
    internal readonly record struct SlotRequest(string KeyPrefix, int Limit);
}

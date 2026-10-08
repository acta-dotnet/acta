using Acta.Runtime;
using Acta.Tests.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// Drives one real <see cref="JobExecution.RunAsync"/> attempt end to end over the real execution and lock
/// groups on scripted sessions: the start CAS, a handler that runs one durable step, and the completion
/// CAS. The script says what <c>complete_step</c> and <c>complete_execution</c> answer, and the harness
/// keeps every <see cref="BoundCompletion"/> the runner bound plus the subset the scripted CAS applied, so
/// a test can assert on the completion shape rather than on a database. Reusable for executor unit tests;
/// add scripted knobs here, not per test.
/// </summary>
internal sealed class JobExecutionHarness(
    CompleteStepOutcomeCode stepOutcome = CompleteStepOutcomeCode.Succeeded,
    CompleteExecutionAction completionAction = CompleteExecutionAction.Completed,
    bool cancelAttemptOnStepCompletion = false,
    int maxAttempts = 3,
    int failureCount = 0,
    int? maxInlinePayloadBytes = null,
    StartExecutionAction startAction = StartExecutionAction.Started,
    bool startFailsOnce = false,
    StartExecutionAction? startAfterFailure = null,
    JobDetail? rowAfterLostClaim = null,
    string? concurrencyKey = null,
    int? concurrencyLimit = null,
    bool slotGranted = true,
    string? rateLimit = null,
    string? rateKey = null,
    DateTime? rateResumeAtUtc = null,
    int? rateWaitMilliseconds = null,
    bool cancelAttemptDuringRateWait = false,
    bool cancelWorkerDuringRateWait = false,
    DateTime? deadlineAtUtc = null,
    bool slotThrows = false,
    bool rateThrows = false,
    string jobName = "harness-job"
)
{
    /// <summary>The step the default handler runs; asserted on by name in the ownership pins.</summary>
    public const string StepName = "charge-card";

    private const int WorkerId = 7;

    private readonly CancellationTokenSource _attemptCts = new();

    // The worker token the runner is handed, cancelled by the script when a fact wants a shutdown.
    private readonly CancellationTokenSource _workerCts = new();

    // The execution-timeout source, handed to the RunningAttempt so a cancellation can be told from an
    // external one. Unlinked from _attemptCts here; TimeOutAttempt cancels both.
    private readonly CancellationTokenSource _timeoutCts = new();
    private readonly ScriptedExecutionStore _store = new(stepOutcome, completionAction, startAction, startFailsOnce, startAfterFailure);
    private readonly GetJobHandler _getJob = new(null!, new RowSession(rowAfterLostClaim), null!);
    private readonly ScriptedLockStore _locks = new(slotGranted, rateResumeAtUtc, rateWaitMilliseconds, slotThrows, rateThrows);

    /// <summary>The row the reconciliation reads after a LostClaim, as a fact stages it; the claim is execution 3 at version 1.</summary>
    public static JobDetail Row(JobStatusCode status, int version, int leasedByWorkerId = WorkerId, int executionNumber = 3) =>
        new(
            JobId: 4242,
            JobRef: new JobRef(Guid.CreateVersion7()),
            JobNamespace: "harness",
            DefinitionId: 1,
            JobName: "harness-job",
            LineageRootId: null,
            LineageRootJobRef: null,
            ParentJobId: null,
            ParentJobRef: null,
            TenantId: null,
            TenantKey: null,
            DeduplicationKey: null,
            CorrelationKey: null,
            ConcurrencyKey: null,
            InputFormatId: 0,
            Status: status,
            Priority: JobPriorityCode.Normal,
            Lane: null,
            BlockedBehindJobId: null,
            BlockedBehindJobRef: null,
            NextRunAtUtc: null,
            ExecutionNumber: executionNumber,
            FailureCount: 0,
            LeasedByWorkerId: leasedByWorkerId,
            LeaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(3),
            RetentionUntilUtc: null,
            CreatedAtUtc: DateTime.UtcNow,
            ModifiedAtUtc: DateTime.UtcNow,
            LeasedByWorkerRef: null,
            Version: version
        );

    /// <summary>The expected version each start write carried, in order.</summary>
    public IReadOnlyList<int> StartVersions => _store.StartVersions;
    private readonly RecordingLogger _log = new();

    /// <summary>Every concurrency-slot acquire the runner issued, in order.</summary>
    public IReadOnlyList<SlotRequest> SlotRequests => _locks.SlotRequests;

    /// <summary>How many held slots the runner released.</summary>
    public int SlotReleases => _locks.SlotReleases;

    /// <summary>Every rate reservation the runner asked the meter for, in order.</summary>
    public IReadOnlyList<RateRequest> RateRequests => _locks.RateRequests;

    /// <summary>Every completion the runner bound, in submission order.</summary>
    public IReadOnlyList<BoundCompletion> Submitted => _store.Submitted;

    /// <summary>How many times the runner issued the start write.</summary>
    public int StartAttempts => _store.StartAttempts;

    /// <summary>Every log line the runner wrote, for the arms whose whole contract is what they say.</summary>
    public IReadOnlyList<LogEntry> Log => _log.Entries;

    /// <summary>Whether the attempt ever reached <c>start_step</c>, i.e. whether the handler ran at all.</summary>
    public bool HandlerRan => _store.StartedSteps.Count > 0;

    /// <summary>The subset whose completion CAS matched a row; a NotOwner answer writes nothing.</summary>
    public IReadOnlyList<BoundCompletion> Applied => _store.Applied;

    /// <summary>The single completion, for the pins that expect exactly one.</summary>
    public BoundCompletion Completion => Assert.Single(Submitted);

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
        _locks.OnRateRequest =
            cancelAttemptDuringRateWait ? _attemptCts.Cancel
            : cancelWorkerDuringRateWait ? _workerCts.Cancel
            : null;

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
            jobName: jobName,
            namespaceName: "harness",
            namespaceId: 1,
            leaseTtlSeconds: options.Value.LeaseTtlSeconds,
            cancelJob: null!,
            resetJobState: null!,
            signals: null!,
            alerts: null!,
            executionStore: _store.Execution,
            new HarnessSerializers(),
            _locks.Locks,
            cancellationToken: _attemptCts.Token,
            triggeringScheduleNames: [],
            deadlineAtUtc: deadlineAtUtc,
            // The two the production JobExecutor supplies and this harness used to default away:
            // without the cap a handler write is unbounded here but bounded in production, and
            // without the attempt every timeout reads as a plain external cancel.
            maxInlinePayloadBytes: options.Value.MaxInlinePayloadBytes,
            runningAttempt: new RunningAttempt(_attemptCts, _timeoutCts),
            workerId: WorkerId
        );

        var execution = new JobExecution(
            _getJob,
            CancelJob(),
            _store.Execution,
            new HarnessSerializers(),
            options,
            new JobBehaviorPipeline([]),
            new WorkerWakeupPublisher(new InProcessWakeup()),
            _log
        );

        return await execution.RunAsync(
            EmptyServices.Instance,
            Descriptor(jobName, handler, maxAttempts, concurrencyLimit, rateLimit, rateKey),
            job,
            context,
            WorkerId,
            isRecurring: false,
            fireOutcome: null,
            alreadyStarted: false,
            _workerCts.Token
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
        var executor = Executor(new WorkerContext(null));
        return await executor.ExecuteClaimedJobAsync(
            Job(failureCount, concurrencyKey),
            namespaceName: "harness",
            namespaceId: 1,
            WorkerId,
            alreadyStarted: false,
            CancellationToken.None
        );
    }

    /// <summary>
    /// The heartbeat's orphan release on the row, with the claim's late answer landing while the
    /// release's start write is in flight: the executor is handed the claimed job inside that window,
    /// as a Buffered channel or a Direct loop would hand it after a delayed store response. Returns
    /// the outcome the executor reported for that late claim; the release's own writes are read off
    /// <see cref="Submitted"/> and <see cref="StartAttempts"/>.
    /// </summary>
    public async Task<RunOnceOutcome> ReleaseOrphanWithLateClaimAnswerAsync()
    {
        var context = new WorkerContext(null);
        context.WorkerIdByNamespace["harness"] = WorkerId;
        context.DescriptorByDefinitionId[1] = Descriptor(
            jobName,
            static (ctx, token) => ctx.RunStepAsync(StepName, static _ => Task.CompletedTask, ct: token),
            maxAttempts,
            concurrencyLimit,
            rateLimit,
            rateKey
        );
        var executor = Executor(context);

        var lateClaim = RunOnceOutcome.NothingClaimed;
        _store.BeforeFirstStart = async () =>
            lateClaim = await executor.ExecuteClaimedJobAsync(
                Job(failureCount, concurrencyKey),
                namespaceName: "harness",
                namespaceId: 1,
                WorkerId,
                alreadyStarted: false,
                CancellationToken.None
            );

        await executor.ReleaseOrphanedClaimsAsync([Job(failureCount, concurrencyKey).JobId], "lost answer", CancellationToken.None);
        return lateClaim;
    }

    private JobExecutor Executor(WorkerContext context)
    {
        var options = Options.Create(new JobsOptions());
        var serializers = new HarnessSerializers();
        return new JobExecutor(
            _locks.Locks,
            new HarnessClock(),
            serializers,
            new StoreOnlyServices(_store.Execution, _getJob, CancelJob()),
            options,
            context,
            new JobExecution(
                _getJob,
                CancelJob(),
                _store.Execution,
                serializers,
                options,
                new JobBehaviorPipeline([]),
                new WorkerWakeupPublisher(new InProcessWakeup()),
                _log
            ),
            _log
        );
    }

    private static ClaimedJob Job(int failureCount, string? concurrencyKey) =>
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
        string jobName,
        Func<JobContext, CancellationToken, Task> handler,
        int maxAttempts,
        int? concurrencyLimit,
        string? rateLimit,
        string? rateKey
    ) =>
        new(
            JobName: jobName,
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
            AuditLevel: DefinitionAuditLevelCode.Audit,
            AlertProfile: AlertProfileCode.OnFailure,
            Invoker: async (_, _, ctx, token) =>
            {
                await handler(ctx, token);
                return new JobHandlerOutput(false, null);
            },
            DeserializeInput: static (_, _) => new NoInput(),
            SerializeOutput: null
        )
        {
            ConcurrencyLimit = concurrencyLimit,
            RateLimit = rateLimit,
            RateKey = rateKey,
        };

    // The real execution group over a scripted session. Only the five calls one attempt makes are
    // answered; everything else throws so a future change that starts leaning on another call is
    // visible rather than silent.
    private sealed class ScriptedExecutionStore(
        CompleteStepOutcomeCode stepOutcome,
        CompleteExecutionAction completionAction,
        StartExecutionAction startAction,
        bool startFailsOnce,
        StartExecutionAction? startAfterFailure
    )
    {
        private readonly List<BoundCompletion> _submitted = [];
        private readonly List<BoundCompletion> _applied = [];
        private readonly List<string> _startedSteps = [];
        private readonly List<int> _startVersions = [];
        private bool _startFailsOnce = startFailsOnce;
        private Acta.Runtime.Execution? _execution;

        public Acta.Runtime.Execution Execution =>
            _execution ??= new ScriptedSession((call, _) => AnswerAsync(call)).Services().GetRequiredService<Acta.Runtime.Execution>();

        /// <summary>How many times the start write was attempted, so a retry is visible.</summary>
        public int StartAttempts { get; private set; }

        public IReadOnlyList<int> StartVersions => _startVersions;

        public IReadOnlyList<BoundCompletion> Submitted => _submitted;
        public IReadOnlyList<BoundCompletion> Applied => _applied;
        public IReadOnlyList<string> StartedSteps => _startedSteps;

        // Fires as complete_step answers, so a test can model the heartbeat cancelling the attempt
        // token the instant the slot is proven stolen.
        public Action? OnStepCompletion { get; set; }

        // Fires once, before the first start write answers, so a test can land another actor's move
        // inside the window a start is in flight.
        public Func<Task>? BeforeFirstStart { get; set; }

        private async Task<object?> AnswerAsync(BoundCall call) =>
            call.Stem switch
            {
                "StartExecution" => await StartAsync(call.Get(ActaSchema.Runtime.Version)),
                "StartStep" => StartStep(call.Get(ActaSchema.Step.Name)),
                "CompleteStep" => CompleteStep(),
                "CompleteExecution" => Complete(BoundCompletion.Read(call)),
                // The deadline path cancels descendants, and a harness job has none.
                "GetChildJobIds" => Array.Empty<long>(),
                _ => throw new NotSupportedException(call.Stem),
            };

        private async Task<byte> StartAsync(int expectedVersion)
        {
            if (BeforeFirstStart is { } hook)
            {
                BeforeFirstStart = null;
                await hook();
            }

            StartAttempts++;
            _startVersions.Add(expectedVersion);
            if (_startFailsOnce)
            {
                _startFailsOnce = false;
                throw new ProviderDown();
            }
            // startAfterFailure models a first try that committed and lost only its response: the retry
            // resubmits a stale version and the CAS answers LostClaim although this worker holds the row.
            return (byte)(StartAttempts > 1 && startAfterFailure is { } after ? after : startAction);
        }

        private StartStepDecision StartStep(string name)
        {
            _startedSteps.Add(name);
            return new StartStepDecision(
                StartStepOutcomeCode.Invoke,
                AttemptNumber: 1,
                Version: 1,
                NextRetryAtUtc: null,
                ResultFormatId: 0,
                Result: null,
                ReasonCode: null,
                ReasonMessage: null
            );
        }

        private CompleteStepDecision CompleteStep()
        {
            OnStepCompletion?.Invoke();
            return new CompleteStepDecision(stepOutcome, NextRetryAtUtc: DateTime.UtcNow.AddSeconds(30));
        }

        private CompleteExecutionResult Complete(BoundCompletion completion)
        {
            _submitted.Add(completion);
            if (completionAction != CompleteExecutionAction.Completed)
            {
                // The CAS matched no row: nothing is written, and the reported status is the row's
                // current one rather than anything this attempt asked for.
                return new CompleteExecutionResult(
                    completionAction,
                    (byte)JobStatusCode.Executing,
                    FinalNextRunAtUtc: null,
                    DateTime.UtcNow,
                    ParentReleased: false
                );
            }

            _applied.Add(completion);
            return new CompleteExecutionResult(
                CompleteExecutionAction.Completed,
                FinalStatus(completion),
                FinalNextRunAtUtc: null,
                DateTime.UtcNow,
                ParentReleased: false
            );
        }

        // Mirrors what complete_execution lands the job in for the shapes this harness produces: a
        // re-arm goes Ready, a handler-control decision takes the requested status, and everything
        // else follows the attempt outcome.
        private static byte FinalStatus(BoundCompletion completion) =>
            completion switch
            {
                { RescheduleStatusCode: not null } => (byte)JobStatusCode.Ready,
                { HandlerStatusCode: { } handler } => handler,
                { Outcome: ExecutionOutcome.Succeeded } => (byte)JobStatusCode.Succeeded,
                { Outcome: ExecutionOutcome.Cancelled } => (byte)JobStatusCode.Cancelled,
                _ => (byte)JobStatusCode.Failed,
            };
    }

    /// <summary>One line the runner wrote: enough to pin an arm whose whole contract is the log.</summary>
    internal sealed record LogEntry(LogLevel Level, string Message);

    // The by-id job read the reconciliation makes after a LostClaim, answered with the staged row; any
    // other read reaches the stub and throws.
    private sealed class RowSession(JobDetail? row) : DbSessionStub
    {
        public override Task<T> QueryAsync<T>(
            string stem,
            Action<System.Data.Common.DbCommand> bind,
            Func<System.Data.Common.DbDataReader, CancellationToken, Task<T>> read,
            CancellationToken ct
        ) => stem == "GetJob" ? Task.FromResult((T)(object?)row!) : base.QueryAsync(stem, bind, read, ct);
    }

    // A handler cancel walks the subtree from the scripted store's children, which it never stages.
    private CancelJobHandler CancelJob() =>
        new(null!, new DbSessionStub(), null!, _store.Execution, null!, new WorkerWakeupPublisher(new InProcessWakeup()));

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

    // The services the executor resolves for an attempt, and a scope that is the provider itself: the
    // stores are the scripted ones, the signal seam and the alert sink are never reached by a handler
    // that runs one step, and anything else resolves to null.
    private sealed class StoreOnlyServices(Acta.Runtime.Execution store, GetJobHandler getJob, CancelJobHandler cancelJob)
        : IServiceProvider,
            IServiceScopeFactory,
            IServiceScope,
            IAsyncDisposable
    {
        private readonly WorkerWakeupPublisher _wakeup = new(new InProcessWakeup());
        private readonly JobContextAccessor _accessor = new();
        private readonly ResetJobStateHandler _resetJobState = new(new DbSessionStub(), null!);

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(Acta.Runtime.Execution) ? store
            : serviceType == typeof(GetJobHandler) ? getJob
            : serviceType == typeof(CancelJobHandler) ? cancelJob
            : serviceType == typeof(ResetJobStateHandler) ? _resetJobState
            : serviceType == typeof(IServiceScopeFactory) ? this
            : serviceType == typeof(WorkerWakeupPublisher) ? _wakeup
            : serviceType == typeof(IJobContextAccessor) ? _accessor
            : serviceType == typeof(Signals) ? ScriptedSession.Unreachable.GetRequiredService<Signals>()
            : serviceType == typeof(AlertLifecycle) ? ScriptedSession.Unreachable.GetRequiredService<AlertLifecycle>()
            : null;

        public IServiceScope CreateScope() => this;

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    // The one exception class the retry helper repeats and admission bounces on: a provider fault, not
    // a handler fault.
    private sealed class ProviderDown() : System.Data.Common.DbException("connection dropped");

    /// <summary>
    /// Scripted admission: the real lock group over a session that answers the slot acquire and the rate
    /// reservation the way the script says, records what the runner asked for, and counts releases; any
    /// other lock call, the handler-facing acquire among them, throws, so an attempt that starts using it
    /// is visible rather than silent.
    /// </summary>
    private sealed class ScriptedLockStore(
        bool slotGranted,
        DateTime? rateResumeAtUtc,
        int? rateWaitMilliseconds,
        bool slotThrows,
        bool rateThrows
    )
    {
        private readonly List<SlotRequest> _slotRequests = [];
        private readonly List<RateRequest> _rateRequests = [];
        private Locks? _locks;

        public Locks Locks => _locks ??= new ScriptedSession(Answer).Services().GetRequiredService<Locks>();

        public IReadOnlyList<SlotRequest> SlotRequests => _slotRequests;

        public IReadOnlyList<RateRequest> RateRequests => _rateRequests;

        /// <summary>Runs after the first reservation is recorded: the script's way to cancel the attempt mid-wait.</summary>
        public Action? OnRateRequest { get; set; }

        public int SlotReleases { get; private set; }

        private object? Answer(BoundCall call) =>
            call.Stem switch
            {
                "AcquireSlot" => AcquireSlot(call.Get(ActaSchema.Sql.LockKeyPrefix), call.Get(ActaSchema.Sql.SlotCount)),
                "ReserveRate" => ReserveRate(
                    new RateRequest(
                        call.Get(ActaSchema.Lock.LockKey),
                        call.Get(ActaSchema.Sql.RateIntervalMilliseconds),
                        call.Get(ActaSchema.Sql.RateBurst),
                        call.Get(ActaSchema.Sql.RateGraceSeconds)
                    )
                ),
                "ReleaseLock" => Release(),
                _ => throw new NotSupportedException(call.Stem),
            };

        // The slot key the SQL took, which the handler pairs with the hold token it minted.
        private string? AcquireSlot(string keyPrefix, int limit)
        {
            _slotRequests.Add(new SlotRequest(keyPrefix, limit));
            if (slotThrows)
            {
                throw new ProviderDown();
            }
            return slotGranted ? $"{keyPrefix}.0" : null;
        }

        private bool Release()
        {
            SlotReleases++;
            return true;
        }

        private RateReservation ReserveRate(RateRequest request)
        {
            _rateRequests.Add(request);
            if (_rateRequests.Count == 1)
            {
                OnRateRequest?.Invoke();
            }
            if (rateThrows)
            {
                throw new ProviderDown();
            }
            // A scripted far turn re-arms (its wait is past the in-process window); a scripted near turn
            // is handed back with its wait once, and the call that follows the sleep finds it admitted.
            if (rateWaitMilliseconds is { } wait && _rateRequests.Count == 1)
            {
                return new RateReservation(false, DateTime.UtcNow.AddMilliseconds(wait), wait);
            }
            return rateResumeAtUtc is { } resumeAt
                ? new RateReservation(false, resumeAt, JobExecution.RateTurnWaitMilliseconds + 1)
                : new RateReservation(true, DateTime.UtcNow, 0);
        }
    }

    /// <summary>One concurrency-slot acquire the runner issued: the composed key prefix and the limit.</summary>
    internal readonly record struct SlotRequest(string KeyPrefix, int Limit);

    /// <summary>One rate reservation the runner issued: the composed bucket key and the meter's shape.</summary>
    internal readonly record struct RateRequest(string BucketKey, int IntervalMilliseconds, int Burst, int GraceSeconds);
}

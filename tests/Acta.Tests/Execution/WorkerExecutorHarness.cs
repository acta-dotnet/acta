using Acta.Tests.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// Drives one real attempt end to end through <see cref="WorkerExecutor.ExecuteAsync"/> and
/// <see cref="WorkerFinalizer.FinalizeAsync"/> over the real execution and lock groups on scripted sessions: the
/// start CAS, a handler that runs one durable step, and the completion CAS. The script says what
/// <c>complete_step</c> and <c>complete_executions</c> answer, and the harness
/// keeps every <see cref="BoundCompletion"/> the runner bound plus the subset the scripted CAS applied, so
/// a test can assert on the completion shape rather than on a database. Reusable for executor unit tests;
/// add scripted knobs here, not per test.
/// </summary>
internal sealed class WorkerExecutorHarness(
    CompleteStepOutcomeCode stepOutcome = CompleteStepOutcomeCode.Succeeded,
    CompleteExecutionsAction completionAction = CompleteExecutionsAction.Completed,
    bool cancelAttemptOnStepCompletion = false,
    int maxAttempts = 3,
    int failureCount = 0,
    int? maxInlinePayloadBytes = null,
    string? concurrencyKey = null,
    int? concurrencyLimit = null,
    bool slotGranted = true,
    string? rateLimit = null,
    string? rateName = null,
    DateTime? rateResumeAtUtc = null,
    int? rateWaitMilliseconds = null,
    bool cancelAttemptDuringRateWait = false,
    bool cancelWorkerDuringRateWait = false,
    DateTime? deadlineAtUtc = null,
    bool slotThrows = false,
    bool rateThrows = false,
    bool scopeDisposalThrows = false,
    string jobName = "harness-job"
)
{
    /// <summary>The step the default handler runs; asserted on by name in the ownership pins.</summary>
    public const string StepName = "charge-card";

    private const int WorkerId = 7;

    // The worker token the runner is handed, cancelled by the script when a fact wants a shutdown.
    private readonly CancellationTokenSource _workerCts = new();

    // The entry RunAsync runs, linked from the worker token as the claimer links it.
    private InFlightJob? _entry;
    private readonly ScriptedExecutionStore _store = new(stepOutcome, completionAction);
    private readonly ScriptedLockStore _locks = new(slotGranted, rateResumeAtUtc, rateWaitMilliseconds, slotThrows, rateThrows);

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
    /// <summary>Every log line the runner wrote, for the arms whose whole contract is what they say.</summary>
    public IReadOnlyList<LogEntry> Log => _log.Entries;

    /// <summary>Whether the attempt ever reached <c>start_step</c>, i.e. whether the handler ran at all.</summary>
    public bool HandlerRan => _store.StartedSteps.Count > 0;

    /// <summary>The subset whose completion CAS matched a row; a NotOwner answer writes nothing.</summary>
    public IReadOnlyList<BoundCompletion> Applied => _store.Applied;

    /// <summary>The single completion, for the pins that expect exactly one.</summary>
    public BoundCompletion Completion => Assert.Single(Submitted);

    /// <summary>
    /// Fires the attempt's execution timeout now, so <c>InFlightJob.TimedOut</c> reads true and the linked
    /// attempt token cancels after it. Call from inside a handler; the handler must then observe its token, as a
    /// cooperative handler does in production.
    /// </summary>
    public void TimeOutAttempt() => _entry!.ArmTimeout(TimeSpan.Zero);

    /// <summary>
    /// Runs one attempt. The default handler invokes <c>ctx.RunStepAsync</c> once with a body that
    /// succeeds, so the scripted <c>complete_step</c> answer is what decides the outcome.
    /// </summary>
    public async Task<RunOnceStatus> RunAsync(Func<JobContext, CancellationToken, Task>? handler = null)
    {
        handler ??= static (ctx, token) => ctx.RunStepAsync(StepName, static _ => Task.CompletedTask, ct: token);
        _entry = new InFlightJob(Job(), leaseRunway: null, _workerCts.Token);
        _store.OnStepCompletion = cancelAttemptOnStepCompletion ? _entry.Cancel : null;
        _locks.OnRateRequest =
            cancelAttemptDuringRateWait ? _entry.Cancel
            : cancelWorkerDuringRateWait ? _workerCts.Cancel
            : null;

        return await Executor(Descriptor(jobName, handler, maxAttempts, concurrencyLimit, rateLimit, rateName, deadlineAtUtc))
            .RunInlineAsync(_entry, _workerCts.Token);
    }

    /// <summary>
    /// Drives one claimed job through the real <see cref="WorkerExecutor"/> with an EMPTY descriptor
    /// index: the state a deployment that dropped the definition leaves a worker in. The scripted
    /// store is the same one, so a test reads the released claim off <see cref="Submitted"/> and the
    /// bounce off <see cref="Log"/>.
    /// </summary>
    public Task<RunOnceStatus> RunWithNoDescriptorAsync() =>
        Executor(descriptor: null)
            .RunInlineAsync(new InFlightJob(Job(), leaseRunway: null, CancellationToken.None), CancellationToken.None);

    // The worker this runtime would be: registered for one namespace, with the descriptor the claim names, if any.
    private WorkerExecutor Executor(JobDescriptor? descriptor)
    {
        var context = new WorkerContext(new WorkerRegistration("harness", null, null, [], []));
        context.NamespaceIds["harness"] = 1;
        context.WorkerIdByNamespace["harness"] = WorkerId;
        if (descriptor is not null)
        {
            context.DescriptorByDefinitionId[1] = descriptor;
        }

        // One options instance feeds the attempt context and the invocation, so a configured cap moves the
        // handler-write limit and the result-drop limit together, as it does in a host.
        var options = new JobsOptions();
        if (maxInlinePayloadBytes is { } configuredCap)
        {
            options.MaxInlinePayloadBytes = configuredCap;
        }

        var registry = new InFlightJobRegistry(options);
        var finalizer = new WorkerFinalizer(
            _store.Execution,
            _store.Jobs,
            new WorkerWakeupPublisher(new InProcessWakeup()),
            options,
            context,
            registry,
            _log,
            metrics: null
        );
        return new WorkerExecutor(
            _locks.Locks,
            new HarnessClock(),
            new HarnessSerializers(),
            new StoreOnlyServices(_store.Execution, _store.Jobs, scopeDisposalThrows),
            options,
            new JobBehaviorPipeline([]),
            context,
            registry,
            finalizer,
            _log
        );
    }

    // A deadline lands as the claim's creation plus a whole-second policy, so a fact's instant is reached through both.
    private ClaimedJob Job() =>
        new(
            JobId: 4242,
            JobRef: Guid.CreateVersion7(),
            DefinitionId: 1,
            TenantId: null,
            ExecutionNumber: 3,
            DeduplicationKey: null,
            CorrelationKey: null,
            ConcurrencyKey: concurrencyKey,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            NextRunAtUtc: null,
            CreatedAtUtc: deadlineAtUtc?.AddSeconds(-1) ?? DateTime.UtcNow,
            FailureCount: failureCount
        );

    private static JobDescriptor Descriptor(
        string jobName,
        Func<JobContext, CancellationToken, Task> handler,
        int maxAttempts,
        int? concurrencyLimit,
        string? rateLimit,
        string? rateName,
        DateTime? deadlineAtUtc
    ) =>
        new(
            JobName: jobName,
            HandlerType: typeof(WorkerExecutorHarness),
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
            RateName = rateName,
            DeadlineSeconds = deadlineAtUtc is null ? null : 1,
        };

    // The real execution group over a scripted session. Only the calls one attempt makes are
    // answered; everything else throws so a future change that starts leaning on another call is
    // visible rather than silent.
    private sealed class ScriptedExecutionStore(CompleteStepOutcomeCode stepOutcome, CompleteExecutionsAction completionAction)
    {
        private readonly List<BoundCompletion> _submitted = [];
        private readonly List<BoundCompletion> _applied = [];
        private readonly List<string> _startedSteps = [];
        private ServiceProvider? _services;

        public ExecutionOperations Execution => Services().GetRequiredService<ExecutionOperations>();

        /// <summary>The jobs operations over the same session: a handler cancel walks the children the store never stages.</summary>
        public JobsOperations Jobs => Services().GetRequiredService<JobsOperations>();

        public IReadOnlyList<BoundCompletion> Submitted => _submitted;
        public IReadOnlyList<BoundCompletion> Applied => _applied;
        public IReadOnlyList<string> StartedSteps => _startedSteps;

        // Fires as complete_step answers, so a test can model the heartbeat cancelling the attempt
        // token the instant the slot is proven stolen.
        public Action? OnStepCompletion { get; set; }

        private ServiceProvider Services() => _services ??= new ScriptedSession(Answer).Services();

        private object? Answer(BoundCall call) =>
            call.Stem switch
            {
                "StartStep" => StartStep(call.Get(ActaSchema.Step.Name)),
                "CompleteStep" => CompleteStep(),
                "CompleteExecutions" => BoundCompletion.Read(call).Select(Complete).ToList(),
                // The deadline path cancels descendants, and a harness job has none.
                "GetChildJobIds" => Array.Empty<long>(),
                _ => throw new NotSupportedException(call.Stem),
            };

        private StartStepResponse StartStep(string name)
        {
            _startedSteps.Add(name);
            return new StartStepResponse(
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

        private CompleteStepResponse CompleteStep()
        {
            OnStepCompletion?.Invoke();
            return new CompleteStepResponse(stepOutcome, NextRetryAtUtc: DateTime.UtcNow.AddSeconds(30));
        }

        private CompleteExecutionsRow Complete(BoundCompletion completion)
        {
            _submitted.Add(completion);
            if (completionAction != CompleteExecutionsAction.Completed)
            {
                // The CAS matched no row: nothing is written, and the reported status is the row's
                // current one rather than anything this attempt asked for.
                return new CompleteExecutionsRow(
                    completion.JobId,
                    completion.ExecutionNumber,
                    (CompleteExecutionsAction)completionAction,
                    (byte)JobStatusCode.Executing,
                    FinalNextRunAtUtc: null,
                    DateTime.UtcNow,
                    ParentReleased: false,
                    LanePromoted: false
                );
            }

            _applied.Add(completion);
            return new CompleteExecutionsRow(
                completion.JobId,
                completion.ExecutionNumber,
                CompleteExecutionsAction.Completed,
                FinalStatus(completion),
                FinalNextRunAtUtc: null,
                DateTime.UtcNow,
                ParentReleased: false,
                LanePromoted: false
            );
        }

        // Mirrors what complete_executions lands the job in for the shapes this harness produces: a
        // re-arm goes Ready, a handler-control decision takes the requested status, and everything
        // else follows the attempt outcome.
        private static byte FinalStatus(BoundCompletion completion) =>
            completion switch
            {
                { RescheduleStatusCode: not null } => (byte)JobStatusCode.Ready,
                { HandlerStatusCode: { } handler } => handler,
                { Outcome: CompletionKind.Succeeded } => (byte)JobStatusCode.Succeeded,
                { Outcome: CompletionKind.Cancelled } => (byte)JobStatusCode.Cancelled,
                _ => (byte)JobStatusCode.Failed,
            };
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

    // The services the executor resolves for an attempt, and a scope that is the provider itself: the
    // stores are the scripted ones, the signal seam and the alert sink are never reached by a handler
    // that runs one step, and anything else resolves to null.
    private sealed class StoreOnlyServices(ExecutionOperations store, JobsOperations jobs, bool disposalThrows)
        : IServiceProvider,
            IServiceScopeFactory,
            IServiceScope,
            IAsyncDisposable
    {
        private readonly WorkerWakeupPublisher _wakeup = new(new InProcessWakeup());
        private readonly JobContextAccessor _accessor = new();

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ExecutionOperations) ? store
            : serviceType == typeof(JobsOperations) ? jobs
            : serviceType == typeof(IServiceScopeFactory) ? this
            : serviceType == typeof(WorkerWakeupPublisher) ? _wakeup
            : serviceType == typeof(IJobContextAccessor) ? _accessor
            : serviceType == typeof(SignalsOperations) ? ScriptedSession.Unreachable.GetRequiredService<SignalsOperations>()
            : serviceType == typeof(AlertsOperations) ? ScriptedSession.Unreachable.GetRequiredService<AlertsOperations>()
            : null;

        public IServiceScope CreateScope() => this;

        public void Dispose() { }

        // The attempt scope is this provider, so a throwing disposal stands in for a scoped service that throws.
        public ValueTask DisposeAsync() =>
            disposalThrows ? throw new InvalidOperationException("A scoped service threw while disposing.") : ValueTask.CompletedTask;
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
        private LocksOperations? _locks;

        public LocksOperations Locks => _locks ??= new ScriptedSession(Answer).Services().GetRequiredService<LocksOperations>();

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

        private ReserveRateResponse ReserveRate(RateRequest request)
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
                return new ReserveRateResponse(false, DateTime.UtcNow.AddMilliseconds(wait), wait);
            }
            return rateResumeAtUtc is { } resumeAt
                ? new ReserveRateResponse(false, resumeAt, ExecutionAdmission.RateTurnWaitMilliseconds + 1)
                : new ReserveRateResponse(true, DateTime.UtcNow, 0);
        }
    }

    /// <summary>One concurrency-slot acquire the runner issued: the composed key prefix and the limit.</summary>
    internal readonly record struct SlotRequest(string KeyPrefix, int Limit);

    /// <summary>One rate reservation the runner issued: the composed bucket key and the meter's shape.</summary>
    internal readonly record struct RateRequest(string BucketKey, int IntervalMilliseconds, int Burst, int GraceSeconds);
}

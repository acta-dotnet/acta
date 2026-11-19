using Acta.Tests.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// A handler's and a step's result are stored up to the result ceiling whatever the inline cap, with a warning past the
/// cap, and past the ceiling the job or the step fails for good, naming the ceiling. A cap set above the ceiling is the
/// ceiling.
/// </summary>
public sealed class ResultCeilingTests
{
    private const int Ceiling = 16 * 1024 * 1024;

    private const int SmallCap = 8;

    private const int RaisedCap = Ceiling + 16;

    // A JSON string's payload is its characters plus two quotes.
    private static string JsonOf(int bytes) => new('x', bytes - 2);

    [Theory]
    [InlineData(SmallCap, Ceiling)]
    [InlineData(SmallCap, Ceiling + 1)]
    [InlineData(RaisedCap, RaisedCap)]
    [InlineData(RaisedCap, RaisedCap + 1)]
    public async Task A_handler_result_is_stored_up_to_the_ceiling_and_fails_the_job_for_good_past_it(int cap, int bytes)
    {
        var ceiling = Math.Max(cap, Ceiling);
        var log = new RecordingLogger();
        var invocation = new HandlerInvocation(
            new JobPayloadSerializerRegistry([JsonJobPayloadSerializer.Default]),
            new JobBehaviorPipeline([]),
            new NonRetryableExceptions([]),
            cap,
            log,
            metrics: null
        );
        var job = Job();

        var completion = await invocation.InvokeAsync(
            ScriptedSession.Unreachable,
            Descriptor(JsonOf(bytes)),
            job,
            Context(job, session: null, log, cap),
            CancellationToken.None
        );

        Assert.NotNull(completion);
        if (bytes <= ceiling)
        {
            Assert.Equal(CompletionKind.Succeeded, completion.Outcome);
            Assert.Equal(bytes, completion.Result.Length);
            AssertWarnsOnlyPastTheCap(log, cap, bytes, $"the handler's result: {bytes} bytes, cap {cap} bytes");
        }
        else
        {
            Assert.Equal(CompletionKind.Failed, completion.Outcome);
            Assert.Equal(JobStatusCode.Failed, completion.HandlerStatusCode);
            Assert.Equal(EventReason.NonRetryableException, completion.Reason);
            Assert.Contains($"{bytes} bytes, over the {ceiling}-byte ceiling", completion.ReasonMessage, StringComparison.Ordinal);
            Assert.Empty(log.Warnings);
        }
    }

    [Theory]
    [InlineData(SmallCap, Ceiling)]
    [InlineData(SmallCap, Ceiling + 1)]
    [InlineData(RaisedCap, RaisedCap)]
    [InlineData(RaisedCap, RaisedCap + 1)]
    public async Task A_step_result_is_stored_up_to_the_ceiling_and_fails_the_step_for_good_past_it(int cap, int bytes)
    {
        var ceiling = Math.Max(cap, Ceiling);
        var session = new ScriptedSession(call =>
            call.Stem switch
            {
                "StartJobStep" => new StartJobStepResponse(
                    StartJobStepOutcomeCode.Invoke,
                    Version: 1,
                    ResultFormatId: 0,
                    Result: null,
                    FailureMessage: null
                ),
                "CompleteJobStep" => new CompleteJobStepResponse(CompleteJobStepOutcomeCode.Succeeded),
                _ => throw new NotSupportedException(call.Stem),
            }
        );
        var log = new RecordingLogger();
        var ctx = Context(Job(), session, log, cap);
        var value = JsonOf(bytes);

        if (bytes <= ceiling)
        {
            Assert.Equal(value, await ctx.RunStepAsync("sized", _ => Task.FromResult(value), CancellationToken.None));
            Assert.Contains(session.Calls, call => call.Stem == "CompleteJobStep");
            AssertWarnsOnlyPastTheCap(log, cap, bytes, $"step 'sized': {bytes} bytes, cap {cap} bytes");
        }
        else
        {
            var failed = await Assert.ThrowsAsync<StepFailedException>(() =>
                ctx.RunStepAsync("sized", _ => Task.FromResult(value), CancellationToken.None)
            );
            Assert.Contains($"{bytes} bytes, over the {ceiling}-byte ceiling", failed.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(session.Calls, call => call.Stem == "CompleteJobStep");
            Assert.Empty(log.Warnings);
        }
    }

    private static void AssertWarnsOnlyPastTheCap(RecordingLogger log, int cap, int bytes, string detail)
    {
        if (bytes <= cap)
        {
            Assert.Empty(log.Warnings);
            return;
        }

        Assert.Contains(detail, Assert.Single(log.Warnings), StringComparison.Ordinal);
    }

    private static ClaimedJob Job() =>
        new(
            JobId: 42,
            JobRef: Guid.CreateVersion7(),
            DefinitionId: 1,
            TenantId: null,
            ExecutionNumber: 1,
            CorrelationKey: null,
            ConcurrencyKey: null,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            NextRunAtUtc: null,
            CreatedAtUtc: DateTime.UtcNow,
            FailureCount: 0
        );

    private static RuntimeJobContext Context(ClaimedJob job, ScriptedSession? session, ILogger log, int cap) =>
        new(
            job,
            jobName: "sized-result",
            namespaceName: "test",
            namespaceId: 1,
            leaseTtlSeconds: 180,
            jobOperations: null!,
            signals: null!,
            alerts: null!,
            executionStore: (session?.Services() ?? ScriptedSession.Unreachable).GetRequiredService<ExecutionOperations>(),
            new JobPayloadSerializerRegistry([JsonJobPayloadSerializer.Default]),
            ScriptedSession.Unreachable.GetRequiredService<LocksOperations>(),
            firing: null,
            deadlineAtUtc: null,
            cancellationToken: CancellationToken.None,
            maxInlinePayloadBytes: cap,
            log: log
        );

    // A handler that returns the given string as its JSON result.
    private static JobDescriptor Descriptor(string result) =>
        new(
            JobName: "sized-result",
            HandlerType: typeof(ResultCeilingTests),
            MethodName: "N/A",
            InputType: typeof(NoInput),
            ResultType: typeof(string),
            InputPayloadFormat: JobPayloadFormat.None,
            ResultPayloadFormat: JobPayloadFormat.Json,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: 3,
            EventRecording: EventRecordingCode.Audit,
            AlertProfile: AlertProfileCode.OnFailure,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: (_, _, _, _) => ValueTask.FromResult(new JobHandlerOutput(true, result)),
            DeserializeInput: (_, _) => new NoInput(),
            SerializeResult: (serializer, value) => serializer.Serialize((string)value!)
        );

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Line)> _lines = [];

        public IEnumerable<string> Warnings => _lines.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Line);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => _lines.Add((logLevel, formatter(state, exception)));
    }
}

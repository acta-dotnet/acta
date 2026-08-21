using System.Globalization;
using System.Text;

namespace Acta.Tests.Context;

/// <summary>
/// In-memory JobContext double for the Map/Parallel/Join sugar: records the order of child starts
/// and child waits, hands every start a fresh sequential id, and resolves each wait from a seeded
/// per-child-name outcome (defaulting to Succeeded). No database, no substrate; only the child-job sinks
/// the sugar touches are implemented, the rest throw.
/// </summary>
internal class RecordingJobContext(IReadOnlyDictionary<string, ChildJobOutcome>? seeded = null) : JobContext
{
    private readonly Dictionary<long, string> _idToName = [];
    private readonly Dictionary<string, object> _variables = new(StringComparer.Ordinal);
    private long _nextId;

    /// <summary>Ordered log of substrate calls: <c>start:{childName}</c> then <c>wait:{childName}</c>.</summary>
    public List<string> Events { get; } = [];

    /// <summary>Child names in start order, with the typed input each was started from.</summary>
    public List<(string Name, object Input)> Started { get; } = [];

    /// <summary>Typed child enqueue options in start order.</summary>
    public List<JobEnqueueOptions> StartOptions { get; } = [];

    /// <summary>Raw child enqueue requests in start order.</summary>
    public List<JobEnqueueRequest> RawStarted { get; } = [];

    public Exception? LockReleaseException { get; init; }
    public int LockReleaseCalls { get; private set; }
    public List<Exception> LockReleaseFailures { get; } = [];

    public override long JobId => 1000;
    public override string JobNamespace => "test-ns";
    public override short NamespaceId => 1;
    public override string JobName => "parent";
    public override CancellationToken CancellationToken => CancellationToken.None;

    protected override Task<JobEnqueueOutcome> StartChildCoreAsync<TInput>(TInput input, JobEnqueueOptions options, CancellationToken ct)
    {
        var name = options.DeduplicationKey ?? throw new InvalidOperationException("child options carried no deduplication key.");
        var id = ++_nextId;
        _idToName[id] = name;
        Started.Add((name, input));
        StartOptions.Add(options);
        Events.Add($"start:{name}");
        return Task.FromResult(new JobEnqueueOutcome(id, JobRef.New(), JobEnqueueAction.Inserted));
    }

    protected override Task<SignalWaitOutcome> WaitSignalCoreAsync(string name, CancellationToken ct)
    {
        if (!name.StartsWith("sys.child.", StringComparison.Ordinal))
        {
            // A plain user signal: resolve it as immediately Set with no payload. Only the child-latch
            // shape carries an outcome envelope, and only that shape is seeded.
            Events.Add($"wait:{name}");
            return Task.FromResult(new SignalWaitOutcome(0, null));
        }

        var id = long.Parse(name["sys.child.".Length..], CultureInfo.InvariantCulture);
        var childName = _idToName[id];
        Events.Add($"wait:{childName}");

        var outcome =
            seeded is not null && seeded.TryGetValue(childName, out var o)
                ? o with
                {
                    ChildJobId = id,
                }
                : new ChildJobOutcome(id, JobStatusCode.Succeeded);
        return Task.FromResult(new SignalWaitOutcome(0, EnvelopeBytes(outcome)));
    }

    private static byte[] EnvelopeBytes(ChildJobOutcome o)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append(CultureInfo.InvariantCulture, $"\"childJobId\":{o.ChildJobId},");
        sb.Append(CultureInfo.InvariantCulture, $"\"status\":{(short)o.Status}");
        sb.Append('}');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static T Unsupported<T>() => throw new NotSupportedException("RecordingJobContext only implements the child-job sinks.");

    protected override Task SetProgressCoreAsync<T>(T value, CancellationToken ct) => Unsupported<Task>();

    protected override Task SetVariableCoreAsync<T>(string name, T value, CancellationToken ct)
    {
        _variables[name] = value;
        return Task.CompletedTask;
    }

    protected override Task SetVariableCoreAsync(string name, JobPayload payload, CancellationToken ct) => Unsupported<Task>();

    protected override Task<(bool Found, T? Value)> TryGetVariableCoreAsync<T>(string name, CancellationToken ct)
        where T : default
    {
        var found = _variables.TryGetValue(name, out var value);
        return Task.FromResult((found, found ? (T)value! : default));
    }

    protected override Task<T> GetOrSetVariableCoreAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> valueFactory,
        CancellationToken ct
    ) => Unsupported<Task<T>>();

    protected override Task<bool> ExistsVariableCoreAsync(string name, CancellationToken ct) =>
        Task.FromResult(_variables.ContainsKey(name));

    protected override Task<bool> DeleteVariableCoreAsync(string name, CancellationToken ct) => Task.FromResult(_variables.Remove(name));

    protected override Task ResetStateCoreAsync(CancellationToken ct) => Unsupported<Task>();

    protected override Task SleepCoreAsync(string name, TimeSpan? delay, DateTime? resumeAtUtc, string? reason, CancellationToken ct) =>
        Unsupported<Task>();

    protected override T? DeserializeSignalPayload<T>(byte valueFormatId, byte[] value)
        where T : default => Unsupported<T?>();

    protected override Task<JobEnqueueOutcome> StartChildCoreAsync(JobEnqueueRequest request, CancellationToken ct)
    {
        var name = request.DeduplicationKey ?? throw new InvalidOperationException("child request carried no deduplication key.");
        var id = ++_nextId;
        _idToName[id] = name;
        RawStarted.Add(request);
        Events.Add($"start:{name}");
        return Task.FromResult(new JobEnqueueOutcome(id, JobRef.New(), JobEnqueueAction.Inserted));
    }

    protected override Task<TResult?> GetChildResultCoreAsync<TResult>(long childJobId, CancellationToken ct)
        where TResult : default => Unsupported<Task<TResult?>>();

    protected override Task RunStepCoreAsync(string name, Func<CancellationToken, Task> body, StepOptions options, CancellationToken ct) =>
        Unsupported<Task>();

    protected override Task<TResult> RunStepCoreAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        StepOptions options,
        CancellationToken ct
    ) => Unsupported<Task<TResult>>();

    protected override Task<Guid?> AcquireLockCoreAsync(string key, LockScope scope, CancellationToken ct) =>
        Task.FromResult<Guid?>(Guid.NewGuid());

    protected override Task ReleaseLockCoreAsync(string key, LockScope scope, Guid holdToken, CancellationToken ct)
    {
        LockReleaseCalls++;
        return LockReleaseException is null ? Task.CompletedTask : Task.FromException(LockReleaseException);
    }

    protected override void OnLockReleaseFailure(string key, LockScope scope, Exception exception) => LockReleaseFailures.Add(exception);

    protected override Task WriteNoteCoreAsync<T>(string message, T? detail, CancellationToken ct)
        where T : default => Unsupported<Task>();

    protected override Task RaiseAlertCoreAsync(
        AlertSeverityCode severityCode,
        string title,
        string message,
        string? channelName,
        string? deduplicationKey,
        CancellationToken ct
    ) => Unsupported<Task>();
}

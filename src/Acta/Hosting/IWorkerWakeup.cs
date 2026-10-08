using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Acta;

/// <summary>
/// Why a wake was published. Carried for metrics and transport diagnostics only; waiters never
/// observe it and never branch on it, and the wake-to-re-read behavior is reason-independent.
/// </summary>
public enum WorkerWakeupReason : byte
{
    /// <summary>Unspecified. Present so a defaulted value is distinguishable in logs and metrics.</summary>
    Unknown = 0,

    /// <summary>A job may be claimable now (enqueued due-now, signal released, resumed, reclaimed).</summary>
    WorkAvailable = 1,

    /// <summary>A job gained a run time ahead of now, so sleeping claim loops re-read their horizon.</summary>
    HorizonChanged = 2,

    /// <summary>A job reached a terminal status, so completion waiters re-read its outcome.</summary>
    JobFinished = 3,
}

/// <summary>
/// How a <see cref="IWorkerWakeup.WaitAsync"/> call returned.
/// </summary>
public enum WorkerWakeupWaitStatus : byte
{
    /// <summary>Unspecified. Present so a defaulted value is distinguishable in logs and metrics.</summary>
    Unknown = 0,

    /// <summary>The timeout elapsed with no wake; the poll-floor path.</summary>
    TimedOut = 1,

    /// <summary>A published wake interrupted the wait.</summary>
    Signaled = 2,
}

/// <summary>The kind of a <see cref="WorkerWakeupChannel"/>; the low-cardinality metrics dimension.</summary>
public enum WorkerWakeupChannelKind : byte
{
    /// <summary>Every worker namespace; wakes all claim loops. The default channel.</summary>
    AllWorkerNamespaces = 0,

    /// <summary>One worker namespace's claim loop.</summary>
    WorkerNamespace = 1,

    /// <summary>One job's terminal completion; unbounded keyspace, waiter-managed lifetime.</summary>
    JobCompletion = 2,
}

/// <summary>
/// One subscribable wake key. <see cref="Name"/> is the canonical string: the in-process dictionary
/// key and a transport's channel suffix (<c>ns:{namespace}</c>, <c>*</c>, or <c>job:{id}</c>). Routing
/// information lives here and only here; a wake carries no payload.
/// </summary>
public readonly record struct WorkerWakeupChannel
{
    /// <summary>The reserved channel name addressing every worker namespace.</summary>
    public const string AllWorkerNamespacesName = "*";

    internal const string WorkerNamespacePrefix = "ns:";
    internal const string JobCompletionPrefix = "job:";

    private WorkerWakeupChannel(string? name, WorkerWakeupChannelKind kind)
    {
        Name = name;
        Kind = kind;
    }

    /// <summary>The channel kind; metrics tag by this, never by <see cref="Name"/>.</summary>
    public WorkerWakeupChannelKind Kind { get; }

    /// <summary>
    /// The canonical channel name. A default-constructed channel normalizes to
    /// <see cref="AllWorkerNamespacesName"/>.
    /// </summary>
    [AllowNull]
    public string Name => field ?? AllWorkerNamespacesName;

    /// <summary>
    /// Whether a wake to this channel may create the channel entry (latching a pre-wait wake).
    /// True for the bounded worker-namespace keyspace; false for
    /// <see cref="WorkerWakeupChannelKind.JobCompletion"/>, whose keyspace is unbounded, so there a
    /// wake reaches existing waiters only and a pre-wait wake is lost by contract.
    /// </summary>
    public bool AllocatesOnPublish => Kind != WorkerWakeupChannelKind.JobCompletion;

    /// <summary>
    /// The channel one worker namespace's claim loop waits on. <c>"*"</c> is reserved for
    /// <see cref="AllWorkerNamespaces"/> and rejected here.
    /// </summary>
    public static WorkerWakeupChannel WorkerNamespace(string namespaceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        return namespaceName == AllWorkerNamespacesName
            ? throw new ArgumentException(
                $"'{AllWorkerNamespacesName}' is reserved for WorkerWakeupChannel.AllWorkerNamespaces.",
                nameof(namespaceName)
            )
            : new WorkerWakeupChannel(WorkerNamespacePrefix + namespaceName, WorkerWakeupChannelKind.WorkerNamespace);
    }

    /// <summary>The channel addressing every worker namespace; wakes all current claim-loop waiters.</summary>
    public static WorkerWakeupChannel AllWorkerNamespaces => default;

    /// <summary>The channel a caller waits on for one job's terminal completion.</summary>
    public static WorkerWakeupChannel JobCompletion(long jobId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(jobId);
        return new WorkerWakeupChannel(
            JobCompletionPrefix + jobId.ToString(CultureInfo.InvariantCulture),
            WorkerWakeupChannelKind.JobCompletion
        );
    }

    /// <summary>
    /// Parse a canonical channel name (a transport's wire form) back into a channel. An unparseable
    /// name returns false, and transports drop such wakes (best-effort contract).
    /// </summary>
    public static bool TryParse(string? name, out WorkerWakeupChannel channel)
    {
        channel = default;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name == AllWorkerNamespacesName)
        {
            return true;
        }

        if (name.StartsWith(WorkerNamespacePrefix, StringComparison.Ordinal) && name.Length > WorkerNamespacePrefix.Length)
        {
            channel = new WorkerWakeupChannel(name, WorkerWakeupChannelKind.WorkerNamespace);
            return true;
        }

        if (
            name.StartsWith(JobCompletionPrefix, StringComparison.Ordinal)
            && long.TryParse(name.AsSpan(JobCompletionPrefix.Length), out var jobId)
            && jobId > 0
        )
        {
            channel = new WorkerWakeupChannel(name, WorkerWakeupChannelKind.JobCompletion);
            return true;
        }

        return false;
    }
}

/// <summary>
/// The wake seam: a wake from a state-changing operation interrupts a waiter's sleep so the change is discovered
/// without waiting out its poll floor. A wake carries no payload ("go look") and never decides what runs: the row is
/// the message and the waiter re-reads the database. Claim loops wait on <c>WorkerNamespace</c>, completion waiters
/// on <c>JobCompletion</c>, <c>AllWorkerNamespaces</c> satisfies any worker-namespace wait, and every waiter keeps a
/// poll floor for lost wakes. Delivery is best-effort (duplicates, misses, spurious returns, no ordering) and
/// coalescing (a wake with no waiter latches once); an implementation honors the wait timeout and swallows transport failures.
/// </summary>
public interface IWorkerWakeup
{
    /// <summary>
    /// Signal the waiters of <paramref name="channel"/> that its underlying state may have changed.
    /// Returns quickly; <paramref name="reason"/> is diagnostic only.
    /// </summary>
    ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default);

    /// <summary>
    /// Wait until a wake for <paramref name="channel"/> arrives, <paramref name="timeout"/>
    /// elapses, or <paramref name="ct"/> cancels.
    /// </summary>
    ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct);
}

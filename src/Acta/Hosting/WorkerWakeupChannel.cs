using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Acta;

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

    internal const string AdmissionPrefix = "admit:";

    /// <summary>
    /// The in-process channel one worker namespace's schedule admitter waits on, beside its claim loop. Every wake of
    /// the namespace sets it as well, so each waiter gets every wake; it never travels on a transport.
    /// </summary>
    internal static WorkerWakeupChannel Admission(string namespaceName) =>
        new(AdmissionPrefix + WorkerNamespace(namespaceName).Name, WorkerWakeupChannelKind.WorkerNamespace);

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

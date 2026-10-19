namespace Acta;

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

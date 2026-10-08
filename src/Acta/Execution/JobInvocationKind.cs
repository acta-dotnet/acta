namespace Acta;

/// <summary>
/// Dispatch shape of a discovered handler: the generator picks one of these values per descriptor from the handler's
/// declared return type, so the runtime invoker normalizes all 16 supported method shapes onto one
/// <see cref="JobHandlerInvokeDelegate"/> contract. Not persisted: the durable catalog holds the derived
/// job-definition contract, and this belongs to the dispatch layer.
/// </summary>
public enum JobInvocationKind : byte
{
    /// <summary>Synchronous void return. <c>void Handle(TIn)</c> / <c>void Handle(TIn, CancellationToken)</c>.</summary>
    Sync = 1,

    /// <summary>Synchronous typed return. <c>TOut Handle(TIn)</c> / <c>TOut Handle(TIn, CancellationToken)</c>.</summary>
    SyncOfT = 2,

    /// <summary>Asynchronous void return. <c>Task Handle(...)</c>.</summary>
    Task = 3,

    /// <summary>Asynchronous typed return. <c>Task&lt;TOut&gt; Handle(...)</c>.</summary>
    TaskOfT = 4,

    /// <summary>Asynchronous void return. <c>ValueTask Handle(...)</c>.</summary>
    ValueTask = 5,

    /// <summary>Asynchronous typed return. <c>ValueTask&lt;TOut&gt; Handle(...)</c>.</summary>
    ValueTaskOfT = 6,
}

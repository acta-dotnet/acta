namespace Acta;

/// <summary>
/// Scope of a <see cref="JobContext"/> <c>RunWithLockAsync</c> lock.
/// </summary>
public enum LockScope
{
    /// <summary>
    /// Namespace-scoped (default): the lock is confined to the calling job's namespace, so two
    /// namespaces can hold the same key concurrently without contending.
    /// </summary>
    Namespace,

    /// <summary>
    /// Cluster-wide: a single holder across every namespace for a given key. All global callers of
    /// the same key converge on one mutex.
    /// </summary>
    Global,
}

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Acta;

public abstract partial class JobContext
{
    /// <summary>
    /// Durable, replay-safe wait for every child the starts answered to reach a terminal status (all-of), suspending this
    /// Job as needed; on a replay, children that already finished resolve from their latches without suspending. The
    /// outcomes come back in caller order. Never throws because a child failed and does not cancel siblings; branch on the
    /// outcome or call <see cref="ChildrenWaitOutcome.ThrowIfAnyFailed"/>.
    /// </summary>
    public async Task<ChildrenWaitOutcome> WaitChildrenAsync(IReadOnlyList<EnqueueJobResponse> children, CancellationToken ct = default)
    {
        ThrowIfInAtMostOnceStep(nameof(WaitChildrenAsync));
        ValidateChildren(children);
        var waits = await WaitChildGroupAsync(children, timeout: null, ct);
        return ChildrenWaitOutcome.From([.. waits.Select(wait => wait.Outcome)]);
    }

    /// <summary>
    /// Bounded twin of <see cref="WaitChildrenAsync(IReadOnlyList{EnqueueJobResponse}, CancellationToken)"/>: the first pass stores
    /// <c>db_now + timeout</c> once as the join's deadline (whole seconds, at least one, for the whole join rather than per
    /// child), and every child on every replay waits toward that one instant, so the budget never restarts, except after
    /// <see cref="ResetStateAsync"/>, which clears the stored deadline with every other checkpoint. Children that landed
    /// keep their outcome; on expiry Acta cancels only the unfinished ones and their subtrees (<c>wait-timed-out</c>), and
    /// this Job resumes with the outcome either way.
    /// </summary>
    public async Task<ChildrenWaitOutcome> WaitChildrenAsync(
        IReadOnlyList<EnqueueJobResponse> children,
        TimeSpan timeout,
        CancellationToken ct = default
    )
    {
        ThrowIfInAtMostOnceStep(nameof(WaitChildrenAsync));
        ValidateChildren(children);
        ToWaitTimeoutSeconds(timeout);
        if (children.Count == 0)
        {
            // No group, so no deadline to persist: an empty wait is over before it starts.
            return ChildrenWaitOutcome.From([]);
        }

        var waits = await WaitChildGroupAsync(children, timeout, ct);
        return ChildrenWaitOutcome.From([.. waits.Select(wait => wait.Outcome)]);
    }

    /// <summary>
    /// Checks every child before the first store call, so a bad one anywhere in the list stops the whole join and a
    /// rejected join never leaves a deadline checkpoint or a half-armed latch behind.
    /// </summary>
    private static void ValidateChildren(IReadOnlyList<EnqueueJobResponse> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        for (var i = 0; i < children.Count; i++)
        {
            ValidateChild(children[i], nameof(children));
        }
    }

    /// <summary>
    /// Fans out one child job per item and waits for all of them, returning outcomes keyed back to the items. The
    /// group name plus the caller's stable item key derives a deterministic, parent-scoped child name (readable when
    /// the key is name-safe, otherwise a stable hash), so a replay finds the same children and builds neither input nor
    /// options for one already started. Duplicate keys are rejected before any child starts; every child starts before
    /// any is awaited. Does not throw on a failed child and does not limit worker concurrency; branch on the outcome or
    /// call <see cref="MapOutcome{TKey}.ThrowIfAnyFailed"/>.
    /// </summary>
    public Task<MapOutcome<TKey>> MapAsync<TItem, TKey, TInput>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, TInput> child,
        CancellationToken ct = default
    )
        where TKey : notnull
        where TInput : notnull => MapOutcomesAsync(groupName, items, itemKey, child, timeout: null, configure: null, ct);

    /// <summary>
    /// <see cref="MapAsync{TItem, TKey, TInput}(string, IEnumerable{TItem}, Func{TItem, TKey}, Func{TItem, TInput}, CancellationToken)"/>
    /// with options every child of the map gets; the parent and each child's idempotency key stay the framework's.
    /// </summary>
    public Task<MapOutcome<TKey>> MapAsync<TItem, TKey, TInput>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, TInput> child,
        Action<JobEnqueueOptionsBuilder> configure,
        CancellationToken ct = default
    )
        where TKey : notnull
        where TInput : notnull => MapOutcomesAsync(groupName, items, itemKey, child, timeout: null, configure, ct);

    /// <summary>
    /// Bounded twin of
    /// <see cref="MapAsync{TItem, TKey, TInput}(string, IEnumerable{TItem}, Func{TItem, TKey}, Func{TItem, TInput}, CancellationToken)"/>:
    /// every fanned-out child waits toward one stored absolute deadline (whole seconds, at least one, for the group rather
    /// than per item) that a replay reuses and never extends. An item whose child did not land by then reports
    /// <see cref="JobOutcome.TimedOut"/> and that child is cancelled along with its subtree; the item keying is
    /// unchanged, and this Job resumes either way.
    /// </summary>
    public Task<MapOutcome<TKey>> MapAsync<TItem, TKey, TInput>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, TInput> child,
        TimeSpan timeout,
        CancellationToken ct = default
    )
        where TKey : notnull
        where TInput : notnull => MapOutcomesAsync(groupName, items, itemKey, child, timeout, configure: null, ct);

    /// <summary>The bounded map with options every child of the map gets.</summary>
    public Task<MapOutcome<TKey>> MapAsync<TItem, TKey, TInput>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, TInput> child,
        TimeSpan timeout,
        Action<JobEnqueueOptionsBuilder> configure,
        CancellationToken ct = default
    )
        where TKey : notnull
        where TInput : notnull => MapOutcomesAsync(groupName, items, itemKey, child, timeout, configure, ct);

    /// <summary>
    /// The map of generated calls of a job with a result: each item's outcome carries the child's typed result, read with
    /// the map from the execution its wait saw; a child that succeeded without storing one answers with no value.
    /// </summary>
    public Task<MapOutcome<TKey, TResult>> MapAsync<TItem, TKey, TInput, TResult>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, JobCall<TInput, TResult>> child,
        CancellationToken ct = default
    )
        where TKey : notnull
        where TInput : notnull
        where TResult : notnull => MapResultsAsync(groupName, items, itemKey, child, timeout: null, configure: null, ct);

    /// <summary>The typed map with options every child of the map gets.</summary>
    public Task<MapOutcome<TKey, TResult>> MapAsync<TItem, TKey, TInput, TResult>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, JobCall<TInput, TResult>> child,
        Action<JobEnqueueOptionsBuilder> configure,
        CancellationToken ct = default
    )
        where TKey : notnull
        where TInput : notnull
        where TResult : notnull => MapResultsAsync(groupName, items, itemKey, child, timeout: null, configure, ct);

    /// <summary>The bounded typed map; a child that timed out carries no result.</summary>
    public Task<MapOutcome<TKey, TResult>> MapAsync<TItem, TKey, TInput, TResult>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, JobCall<TInput, TResult>> child,
        TimeSpan timeout,
        CancellationToken ct = default
    )
        where TKey : notnull
        where TInput : notnull
        where TResult : notnull => MapResultsAsync(groupName, items, itemKey, child, timeout, configure: null, ct);

    /// <summary>The bounded typed map with options every child of the map gets.</summary>
    public Task<MapOutcome<TKey, TResult>> MapAsync<TItem, TKey, TInput, TResult>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, JobCall<TInput, TResult>> child,
        TimeSpan timeout,
        Action<JobEnqueueOptionsBuilder> configure,
        CancellationToken ct = default
    )
        where TKey : notnull
        where TInput : notnull
        where TResult : notnull => MapResultsAsync(groupName, items, itemKey, child, timeout, configure, ct);

    private async Task<MapOutcome<TKey>> MapOutcomesAsync<TItem, TKey, TInput>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, TInput> child,
        TimeSpan? timeout,
        Action<JobEnqueueOptionsBuilder>? configure,
        CancellationToken ct
    )
        where TKey : notnull
        where TInput : notnull
    {
        var (keys, _, waits) = await FanOutAsync(groupName, items, itemKey, child, timeout, configure, ct);
        var outcomes = new MapItemOutcome<TKey>[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            outcomes[i] = new MapItemOutcome<TKey>(keys[i], waits[i].Outcome);
        }

        return new MapOutcome<TKey>(outcomes);
    }

    private async Task<MapOutcome<TKey, TResult>> MapResultsAsync<TItem, TKey, TInput, TResult>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, JobCall<TInput, TResult>> child,
        TimeSpan? timeout,
        Action<JobEnqueueOptionsBuilder>? configure,
        CancellationToken ct
    )
        where TKey : notnull
        where TInput : notnull
        where TResult : notnull
    {
        var (keys, started, waits) = await FanOutAsync(groupName, items, itemKey, child, timeout, configure, ct);
        var outcomes = new MapItemOutcome<TKey, TResult>[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            outcomes[i] = new MapItemOutcome<TKey, TResult>(keys[i], await LandAsync<TResult>(started[i], waits[i], ct));
        }

        return new MapOutcome<TKey, TResult>(outcomes);
    }

    /// <summary>The map's children, named from their keys, started in item order, and waited on as one group.</summary>
    [DebuggerDisableUserUnhandledExceptions]
    private async Task<(TKey[] Keys, EnqueueJobResponse[] Started, (JobOutcome Outcome, long? ExecutionNumber)[] Waits)> FanOutAsync<
        TItem,
        TKey,
        TInput
    >(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, TInput> child,
        TimeSpan? timeout,
        Action<JobEnqueueOptionsBuilder>? configure,
        CancellationToken ct
    )
        where TKey : notnull
        where TInput : notnull
    {
        ThrowIfInAtMostOnceStep(nameof(MapAsync));
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(itemKey);
        ArgumentNullException.ThrowIfNull(child);
        groupName = NameSyntax.CanonicalizeDurable(groupName, nameof(groupName));
        ValidateGroupTimeout(timeout);

        var materialized = items as IReadOnlyList<TItem> ?? items.ToList();
        var keys = new TKey[materialized.Count];
        var names = new string[materialized.Count];
        var seenKeys = new HashSet<TKey>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < materialized.Count; i++)
        {
            var key = itemKey(materialized[i])
                ?? throw new ArgumentException($"Map group '{groupName}' produced a null item key.", nameof(itemKey));
            if (!seenKeys.Add(key))
            {
                throw new ArgumentException($"Map group '{groupName}' has a duplicate item key '{key}'.", nameof(itemKey));
            }

            var childName = KeySyntax.Canonicalize(ChildGroupName(groupName, key), nameof(groupName));
            if (!seenNames.Add(childName))
            {
                throw new ArgumentException(
                    $"Map group '{groupName}' derived a duplicate child name '{childName}' for key '{key}'.",
                    nameof(itemKey)
                );
            }

            keys[i] = key;
            names[i] = childName;
        }

        IReadOnlyDictionary<string, EnqueueJobResponse>? existing = null;
        if (names.Length > 0)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
            existing = await ListChildJobsByNameCoreAsync(names, linked.Token);
        }

        // A child already started is answered from its row, so the start's checks of route, definition, tenant and payload
        // size do not run again for it.
        var started = new EnqueueJobResponse[materialized.Count];
        for (var i = 0; i < materialized.Count; i++)
        {
            started[i] =
                existing is not null && existing.TryGetValue(names[i], out var found)
                    ? found
                    : await EnqueueGroupChildAsync(names[i], child(materialized[i]), configure, ct);
        }

        return (keys, started, await WaitChildGroupAsync(started, timeout, ct));
    }

    /// <summary>
    /// Subclass sink: this Job's children under <paramref name="names"/>, by name, each answered as its start would
    /// answer it; a name no child holds is absent. The default answers null, and every child is started, so a
    /// <see cref="JobContext"/> subclass without durable storage keeps working.
    /// </summary>
    private protected virtual Task<IReadOnlyDictionary<string, EnqueueJobResponse>?> ListChildJobsByNameCoreAsync(
        IReadOnlyList<string> names,
        CancellationToken ct
    ) => Task.FromResult<IReadOnlyDictionary<string, EnqueueJobResponse>?>(null);

    private static string ChildGroupName<TKey>(string groupName, TKey key)
        where TKey : notnull
    {
        // Invariant, so a replay on a host with another culture derives the same name.
        var canonical = (key is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : key.ToString()) ?? string.Empty;
        return IsNameSafeTail(canonical) && groupName.Length + 1 + canonical.Length <= NameSyntax.ExtendedMaxLength
            ? $"{groupName}/{canonical}"
            : $"{groupName}/{ShortHash(canonical)}";
    }

    private static bool IsNameSafeTail(string value)
    {
        if (value.Length == 0 || value[0] == '-' || value[^1] == '-')
        {
            return false;
        }
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') || c == '-';
            if (!ok)
            {
                return false;
            }
            if (c == '-' && i > 0 && value[i - 1] == '-')
            {
                return false;
            }
        }
        return true;
    }

    private static string ShortHash(string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}

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
    public async Task<ChildrenWaitOutcome> JoinAsync(IReadOnlyList<EnqueueJobResponse> children, CancellationToken ct = default)
    {
        ThrowIfInAtMostOnceStep(nameof(JoinAsync));
        ValidateChildren(children);
        return ChildrenWaitOutcome.From(await WaitChildGroupAsync(children, timeout: null, ct));
    }

    /// <summary>
    /// Bounded twin of <see cref="JoinAsync(IReadOnlyList{EnqueueJobResponse}, CancellationToken)"/>: the first pass stores
    /// <c>db_now + timeout</c> once as the join's deadline (whole seconds, positive, for the whole join rather than per
    /// child), and every child on every replay waits toward that one instant, so the budget never restarts, except after
    /// <see cref="ResetStateAsync"/>, which clears the stored deadline with every other checkpoint. Children that landed
    /// keep their outcome; on expiry Acta cancels only the unfinished ones and their subtrees (<c>wait-timed-out</c>), and
    /// this Job resumes with the outcome either way.
    /// </summary>
    public async Task<ChildrenWaitOutcome> JoinAsync(
        IReadOnlyList<EnqueueJobResponse> children,
        TimeSpan timeout,
        CancellationToken ct = default
    )
    {
        ThrowIfInAtMostOnceStep(nameof(JoinAsync));
        ValidateChildren(children);
        ToWaitTimeoutSeconds(timeout);
        if (children.Count == 0)
        {
            // No group, so no deadline to persist: an empty wait is over before it starts.
            return ChildrenWaitOutcome.From([]);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return ChildrenWaitOutcome.From(await WaitChildGroupAsync(children, timeout, linked.Token));
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
    /// Starts each named branch as a child job and waits for all of them, returning branch-keyed
    /// outcomes. The group and branch names are validated and branch names must be unique; every
    /// branch is started before any is awaited. Does not throw on a failed branch and does not
    /// fail-fast; branch on the outcome or call <see cref="ParallelOutcome.ThrowIfAnyFailed"/>.
    /// </summary>
    public Task<ParallelOutcome> ParallelAsync(string groupName, Action<ParallelBuilder> configure, CancellationToken ct = default) =>
        ParallelCoreAsync(groupName, configure, timeout: null, ct);

    /// <summary>
    /// Bounded twin of <see cref="ParallelAsync(string, Action{ParallelBuilder}, CancellationToken)"/>: every branch
    /// waits toward one stored absolute deadline (whole seconds, positive, for the group rather than per branch) that
    /// a replay reuses and never extends. A branch that did not land by then reports
    /// <see cref="JobOutcome.TimedOut"/> and is cancelled along with its subtree; the branch keying is unchanged,
    /// and this Job resumes either way. Each branch child is named <c>groupName/branchName</c>.
    /// </summary>
    public Task<ParallelOutcome> ParallelAsync(
        string groupName,
        Action<ParallelBuilder> configure,
        TimeSpan timeout,
        CancellationToken ct = default
    ) => ParallelCoreAsync(groupName, configure, timeout, ct);

    private async Task<ParallelOutcome> ParallelCoreAsync(
        string groupName,
        Action<ParallelBuilder> configure,
        TimeSpan? timeout,
        CancellationToken ct
    )
    {
        ThrowIfInAtMostOnceStep(nameof(ParallelAsync));
        ArgumentNullException.ThrowIfNull(configure);
        groupName = NameSyntax.Canonicalize(groupName, nameof(groupName), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        ValidateGroupTimeout(timeout);

        var builder = new ParallelBuilder();
        configure(builder);
        var branches = builder.Branches;

        var childNames = new string[branches.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < branches.Count; i++)
        {
            var branchName = NameSyntax.Canonicalize(
                branches[i].BranchName,
                "branchName",
                NameSyntax.ExtendedMaxLength,
                reserveSystem: true
            );
            // Checked whole before any branch starts, so a key too long fails the group, not its last branch.
            childNames[i] = KeySyntax.Canonicalize($"{groupName}/{branchName}", "branchName");
            if (!seen.Add(branchName))
            {
                throw new ArgumentException(
                    $"Parallel group '{groupName}' has a duplicate branch name '{branches[i].BranchName}'.",
                    nameof(configure)
                );
            }
        }

        var started = new EnqueueJobResponse[branches.Count];
        for (var i = 0; i < branches.Count; i++)
        {
            started[i] = await branches[i].Start(this, childNames[i], ct);
        }

        var outcomes = await WaitChildGroupAsync(started, timeout, ct);

        var byBranch = new Dictionary<string, JobOutcome>(StringComparer.Ordinal);
        for (var i = 0; i < branches.Count; i++)
        {
            byBranch[branches[i].BranchName] = outcomes[i];
        }

        return new ParallelOutcome(byBranch);
    }

    /// <summary>
    /// Fans out one child job per item and waits for all of them, returning outcomes keyed back to the items. The
    /// group name plus the caller's stable item key derives a deterministic, parent-scoped child name (readable when
    /// the key is name-safe, otherwise a stable hash), so a replay dedupes onto the same children. Duplicate keys are
    /// rejected before any child starts; every child starts before any is awaited. Does not throw on a failed child
    /// and does not limit worker concurrency; branch on the outcome or call <see cref="MapOutcome{TKey}.ThrowIfAnyFailed"/>.
    /// </summary>
    public Task<MapOutcome<TKey>> MapAsync<TItem, TKey, TInput>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, TInput> child,
        CancellationToken ct = default
    )
        where TKey : notnull
        where TInput : notnull => MapCoreAsync<TItem, TKey, TInput>(groupName, items, itemKey, child, timeout: null, ct);

    /// <summary>
    /// Bounded twin of
    /// <see cref="MapAsync{TItem, TKey, TInput}(string, IEnumerable{TItem}, Func{TItem, TKey}, Func{TItem, TInput}, CancellationToken)"/>:
    /// every fanned-out child waits toward one stored absolute deadline (whole seconds, positive, for the group rather
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
        where TInput : notnull => MapCoreAsync<TItem, TKey, TInput>(groupName, items, itemKey, child, timeout, ct);

    private async Task<MapOutcome<TKey>> MapCoreAsync<TItem, TKey, TInput>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, TInput> child,
        TimeSpan? timeout,
        CancellationToken ct
    )
        where TKey : notnull
        where TInput : notnull
    {
        ThrowIfInAtMostOnceStep(nameof(MapAsync));
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(itemKey);
        ArgumentNullException.ThrowIfNull(child);
        groupName = NameSyntax.Canonicalize(groupName, nameof(groupName), NameSyntax.ExtendedMaxLength, reserveSystem: true);
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

        var started = new EnqueueJobResponse[materialized.Count];
        for (var i = 0; i < materialized.Count; i++)
        {
            started[i] = await StartGroupChildAsync(names[i], child(materialized[i]), ct);
        }

        var outcomes = await WaitChildGroupAsync(started, timeout, ct);

        var resultItems = new MapItemOutcome<TKey>[materialized.Count];
        for (var i = 0; i < materialized.Count; i++)
        {
            resultItems[i] = new MapItemOutcome<TKey>(keys[i], outcomes[i]);
        }

        return new MapOutcome<TKey>(resultItems);
    }

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

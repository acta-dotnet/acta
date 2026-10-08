using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Acta;

public abstract partial class JobContext
{
    /// <summary>
    /// Waits for every child handle to reach a terminal status and returns the outcomes in caller
    /// order. A nicer wrapper over
    /// <see cref="WaitChildrenAsync(IReadOnlyList{long}, CancellationToken)"/>: it does not throw because a child
    /// failed and does not cancel siblings; branch on the returned outcome or call
    /// <see cref="JoinOutcome.ThrowIfAnyFailed"/>.
    /// </summary>
    public async Task<JoinOutcome> JoinAsync(IReadOnlyList<JobEnqueueResponse> children, CancellationToken ct = default) =>
        new(await WaitChildrenAsync(ChildIds(children), ct));

    /// <summary>
    /// Bounded twin of <see cref="JoinAsync(IReadOnlyList{JobEnqueueResponse}, CancellationToken)"/>: the whole join
    /// shares one stored deadline a replay never extends. It answers a <see cref="ChildrenWaitOutcome"/>, which carries
    /// the same ordered outcomes as a <see cref="JoinOutcome"/> and can also say the group ran out of time.
    /// </summary>
    /// <param name="children">The child handles to join on, in the order the outcomes come back.</param>
    /// <param name="timeout">Budget for the whole join from DB now, not per child. Must be positive.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public Task<ChildrenWaitOutcome> JoinAsync(
        IReadOnlyList<JobEnqueueResponse> children,
        TimeSpan timeout,
        CancellationToken ct = default
    ) => TryWaitChildrenAsync(ChildIds(children), timeout, ct);

    private static long[] ChildIds(IReadOnlyList<JobEnqueueResponse> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        var ids = new long[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            ids[i] = children[i].JobId;
        }
        return ids;
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
    /// <see cref="ChildJobOutcome.TimedOut"/> and is cancelled along with its subtree; the branch keying is unchanged,
    /// and this Job resumes either way. Each branch child is named <c>groupName-branchName</c>.
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
        ArgumentNullException.ThrowIfNull(configure);
        IdentifierSyntax.ValidateUserKebab(groupName, nameof(groupName), IdentifierSyntax.ExtendedMaxLength);
        ValidateGroupTimeout(timeout);

        var builder = new ParallelBuilder();
        configure(builder);
        var branches = builder.Branches;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in branches)
        {
            IdentifierSyntax.ValidateUserKebab(branch.BranchName, "branchName", IdentifierSyntax.ExtendedMaxLength);
            // Checked whole before any branch starts, so a name too long fails the group, not its last branch.
            IdentifierSyntax.ValidateUserKebab($"{groupName}-{branch.BranchName}", "branchName", IdentifierSyntax.ExtendedMaxLength);
            if (!seen.Add(branch.BranchName))
            {
                throw new ArgumentException(
                    $"Parallel group '{groupName}' has a duplicate branch name '{branch.BranchName}'.",
                    nameof(configure)
                );
            }
        }

        var ids = new long[branches.Count];
        for (var i = 0; i < branches.Count; i++)
        {
            var childName = $"{groupName}-{branches[i].BranchName}";
            ids[i] = (await branches[i].Start(this, childName, ct)).JobId;
        }

        var outcomes = await WaitChildGroupAsync(ids, timeout, ct);

        var byBranch = new Dictionary<string, ChildJobOutcome>(StringComparer.Ordinal);
        for (var i = 0; i < branches.Count; i++)
        {
            byBranch[branches[i].BranchName] = outcomes[i];
        }

        return new ParallelOutcome(groupName, byBranch);
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
    /// <see cref="ChildJobOutcome.TimedOut"/> and that child is cancelled along with its subtree; the item keying is
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
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(itemKey);
        ArgumentNullException.ThrowIfNull(child);
        IdentifierSyntax.ValidateUserKebab(groupName, nameof(groupName), IdentifierSyntax.ExtendedMaxLength);
        ValidateGroupTimeout(timeout);

        var materialized = items as IReadOnlyList<TItem> ?? items.ToList();
        var keys = new TKey[materialized.Count];
        var names = new string[materialized.Count];
        var seenKeys = new HashSet<TKey>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < materialized.Count; i++)
        {
            var key =
                itemKey(materialized[i])
                ?? throw new ArgumentException($"Map group '{groupName}' produced a null item key.", nameof(itemKey));
            if (!seenKeys.Add(key))
            {
                throw new ArgumentException($"Map group '{groupName}' has a duplicate item key '{key}'.", nameof(itemKey));
            }

            var childName = ChildGroupName(groupName, key);
            IdentifierSyntax.ValidateUserKebab(childName, nameof(groupName), IdentifierSyntax.ExtendedMaxLength);
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

        var ids = new long[materialized.Count];
        for (var i = 0; i < materialized.Count; i++)
        {
            ids[i] = (await StartChildAsync(names[i], child(materialized[i]), ct: ct)).JobId;
        }

        var outcomes = await WaitChildGroupAsync(ids, timeout, ct);

        var resultItems = new MapItemOutcome<TKey>[materialized.Count];
        for (var i = 0; i < materialized.Count; i++)
        {
            resultItems[i] = new MapItemOutcome<TKey>(keys[i], ids[i], outcomes[i]);
        }

        return new MapOutcome<TKey>(groupName, resultItems);
    }

    private static string ChildGroupName<TKey>(string groupName, TKey key)
        where TKey : notnull
    {
        // Invariant, so a replay on a host with another culture derives the same name.
        var canonical = (key is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : key.ToString()) ?? string.Empty;
        return IsNameSafeTail(canonical) && groupName.Length + 1 + canonical.Length <= IdentifierSyntax.ExtendedMaxLength
            ? $"{groupName}-{canonical}"
            : $"{groupName}-{ShortHash(canonical)}";
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

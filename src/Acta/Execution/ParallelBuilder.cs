namespace Acta;

/// <summary>
/// Collects the named heterogeneous branches of a <c>JobContext.ParallelAsync</c> group. Each
/// <see cref="Child{TInput}"/> captures a typed input and defers the start; the branch name is
/// combined with the group name into the child's stable, parent-scoped deduplication key.
/// </summary>
public sealed class ParallelBuilder
{
    private readonly List<Branch> _branches = [];

    /// <summary>
    /// Adds a branch that starts a child job from <paramref name="input"/>. The branch name is unique
    /// within the group and becomes part of the child's stable name.
    /// </summary>
    public ParallelBuilder Child<TInput>(string branchName, TInput input)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(branchName);
        ArgumentNullException.ThrowIfNull(input);
        _branches.Add(new Branch(branchName, (context, childName, ct) => context.StartChildAsync(childName, input, ct: ct)));
        return this;
    }

    internal IReadOnlyList<Branch> Branches => _branches;

    internal sealed record Branch(string BranchName, Func<JobContext, string, CancellationToken, Task<JobEnqueueOutcome>> Start);
}

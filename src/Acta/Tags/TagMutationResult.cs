namespace Acta;

/// <summary>
/// Outcome of one tag mutation (<see cref="ITags.UpsertAsync"/> / <see cref="ITags.ReplaceAsync"/> /
/// <see cref="ITags.RemoveAsync"/>): the <see cref="Action"/> taken against the target. A record so
/// the shape can grow additively (a count, a version) without breaking callers, matching every
/// sibling control result on the surface.
/// </summary>
public sealed record TagMutationResult(TagMutationAction Action)
{
    /// <summary>True when the mutation applied (the target existed).</summary>
    public bool IsApplied => Action == TagMutationAction.Applied;
}

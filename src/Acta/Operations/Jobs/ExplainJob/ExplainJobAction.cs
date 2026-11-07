namespace Acta;

/// <summary>
/// A suggested operator move. <see cref="Kind"/> is a stable slug
/// (<c>raise-signal</c> | <c>cancel</c> | <c>resume</c> | <c>restart</c> | <c>inspect-timeline</c> |
/// <c>wait-recovery</c> | <c>none</c>); <see cref="Description"/> is the prose shown to the operator.
/// </summary>
/// <param name="Kind">Which move it is, as a stable code: raise-signal, cancel, resume, restart, inspect-timeline, wait-recovery, or
/// none.</param>
/// <param name="Description">The move, described for the operator.</param>
public sealed record ExplainJobAction(string Kind, string Description);

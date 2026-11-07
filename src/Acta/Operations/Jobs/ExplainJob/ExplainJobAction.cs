namespace Acta;

/// <summary>A suggested operator move.</summary>
/// <param name="Kind">Which move it is: raise-signal, cancel, resume, restart, inspect-timeline, wait-recovery, or none. Other values may
/// appear: keep one this client does not know.</param>
/// <param name="Description">The move, described for the operator.</param>
public sealed record ExplainJobAction(string Kind, string Description);

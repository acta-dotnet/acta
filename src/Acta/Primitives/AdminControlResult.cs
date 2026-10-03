namespace Acta;

/// <summary>Result of an admin control transition: the action plus the row version after the attempt.</summary>
public sealed record AdminControlResult(ControlAction Action, int? Version) : IControlResponse;

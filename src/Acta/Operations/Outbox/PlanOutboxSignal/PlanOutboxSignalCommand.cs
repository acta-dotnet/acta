namespace Acta;

/// <summary>The command row the tick read, matched by its stored value, and the plan to store in its place.</summary>
internal sealed record PlanOutboxSignalCommand(long JobId, string Name, byte[] Value, byte[] PlannedValue);

namespace Acta.Runtime;

/// <summary>The silence after which an Active worker counts as dead.</summary>
internal sealed record MarkDeadWorkersCommand(int DeadAfterSeconds);

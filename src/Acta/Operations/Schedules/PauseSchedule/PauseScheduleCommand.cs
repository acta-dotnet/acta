namespace Acta;

/// <summary>The schedule to pause: indefinitely when <c>UntilUtc</c> is null, else until that instant.</summary>
internal sealed record PauseScheduleCommand(ScheduleLookup Schedule, DateTime? UntilUtc, string? ReasonMessage, string? ActorKey);

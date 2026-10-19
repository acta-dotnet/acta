namespace Acta;

/// <summary>A batch of completions by one worker, of every shape, written in one transaction.</summary>
internal sealed record CompleteExecutionsCommand(int WorkerId, IReadOnlyList<CompleteExecutionsValues> Completions);

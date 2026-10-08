namespace Acta;

/// <summary>The step slot to read or open, and whether it runs at most once.</summary>
internal sealed record StartStepCommand(long JobId, string Name, bool AtMostOnce);

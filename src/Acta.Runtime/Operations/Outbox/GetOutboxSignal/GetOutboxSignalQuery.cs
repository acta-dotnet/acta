namespace Acta.Runtime;

/// <summary>The slot job and the command name whose pending row to read.</summary>
internal sealed record GetOutboxSignalQuery(long JobId, string Name);

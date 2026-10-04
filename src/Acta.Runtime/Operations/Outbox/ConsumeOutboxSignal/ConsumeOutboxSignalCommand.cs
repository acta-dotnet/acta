namespace Acta.Runtime;

/// <summary>The applied command row to delete, guarded by the version the applying tick read.</summary>
internal sealed record ConsumeOutboxSignalCommand(long JobId, string Name, int Version);

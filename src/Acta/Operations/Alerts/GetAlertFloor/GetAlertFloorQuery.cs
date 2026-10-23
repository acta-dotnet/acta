namespace Acta;

/// <summary>The namespace whose alert floor to read, named directly or by one of its jobs.</summary>
internal sealed record GetAlertFloorQuery(int? NamespaceId = null, long? JobId = null);

namespace Acta;

/// <summary>The skipped events to read back for a replay, as the <see cref="GetAlertFloorResponse"/> JSON names them.</summary>
internal sealed record GetPoisonAlertEventsQuery(int NamespaceId, string PoisonJson);

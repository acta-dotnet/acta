namespace Acta;

/// <summary>
/// Scope and thresholds for <see cref="ILedger.GetOverviewAsync"/>. Null namespace means
/// whole system.
/// </summary>
/// <param name="JobNamespace">Optional namespace scope.</param>
/// <param name="StaleWorkerAfterSeconds">Last-seen age in seconds after which a live worker counts as stale.</param>
/// <param name="DueSoonWindowSeconds">Window ahead of the database clock for the due-soon schedule count.</param>
public sealed record GetOverviewQuery(string? JobNamespace = null, int StaleWorkerAfterSeconds = 180, int DueSoonWindowSeconds = 3600);

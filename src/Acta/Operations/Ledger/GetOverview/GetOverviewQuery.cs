namespace Acta;

/// <summary>
/// Scope and due-soon window for <see cref="ILedger.GetOverviewAsync"/>. Null namespace means whole system. Stale workers
/// are judged against this deployment's lease window, as <see cref="ListWorkersItem.Stale"/> is.
/// </summary>
/// <param name="JobNamespace">Optional namespace scope.</param>
/// <param name="DueSoonWindowSeconds">Window ahead of the database clock for the due-soon schedule count.</param>
public sealed record GetOverviewQuery(string? JobNamespace = null, int DueSoonWindowSeconds = 3600);

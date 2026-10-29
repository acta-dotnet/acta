namespace Acta;

/// <summary>
/// The admitter's read: every member schedule of each slot that has a schedule due for evaluation at
/// <paramref name="NowUtc"/>, at most <paramref name="Take"/> due schedules per pass.
/// </summary>
internal sealed record GetDueSchedulesQuery(int NamespaceId, DateTime NowUtc, int Take);

namespace Acta;

/// <summary>
/// A keyset page request over the Quarantined rows, ordered by <c>job_ref</c> - the one portable
/// unique order across the three source encodings. <paramref name="AfterJobRef"/> is the last id of
/// the previous page, or null for the first page.
/// </summary>
internal sealed record ListQuarantinedOutboxRowsQuery(int PageSize, Guid? AfterJobRef);

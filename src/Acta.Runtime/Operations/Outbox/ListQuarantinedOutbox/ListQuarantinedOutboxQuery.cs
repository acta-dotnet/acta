namespace Acta.Runtime;

/// <summary>
/// A keyset page request over the Quarantined rows, ordered by <c>outbox_id</c> - the one portable
/// unique order across the three source encodings. <paramref name="AfterOutboxId"/> is the last id of
/// the previous page, or null for the first page.
/// </summary>
internal sealed record ListQuarantinedOutboxQuery(int PageSize, Guid? AfterOutboxId);

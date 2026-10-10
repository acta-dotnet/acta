namespace Acta;

/// <summary>One answer per completion of the batch.</summary>
internal sealed record CompleteExecutionsResponse(IReadOnlyList<CompleteExecutionsRow> Rows);

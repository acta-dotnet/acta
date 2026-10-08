namespace Acta;

/// <summary>The claimed batch in staging order, each row under the claim's token.</summary>
internal readonly record struct ClaimOutboxResponse(IReadOnlyList<ClaimOutboxRow> Rows);

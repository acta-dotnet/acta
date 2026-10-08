namespace Acta;

/// <summary>
/// A bounded claim request: expired-lease recovery plus a due-Pending claim of at most
/// <paramref name="BatchSize"/> rows, stamping <paramref name="ClaimToken"/> valid for
/// <paramref name="LeaseTtlSeconds"/> from the source-database clock.
/// </summary>
internal sealed record ClaimOutboxCommand(Guid ClaimToken, int BatchSize, int LeaseTtlSeconds);

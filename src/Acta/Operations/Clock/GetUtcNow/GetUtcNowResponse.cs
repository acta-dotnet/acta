namespace Acta;

/// <summary>The database server's clock, as UTC.</summary>
internal sealed record GetUtcNowResponse(DateTime UtcNow);

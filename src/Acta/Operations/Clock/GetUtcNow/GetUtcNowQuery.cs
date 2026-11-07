using System.Data.Common;

namespace Acta;

/// <summary>The clock read: on the caller's transaction when it holds one, else on a connection of its own.</summary>
internal sealed record GetUtcNowQuery(DbTransaction? Transaction);

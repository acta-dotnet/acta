using System.Data.Common;

namespace Acta;

/// <summary>The migration transaction the history table is created in.</summary>
internal sealed record EnsureMigrationsCommand(DbTransaction Transaction);

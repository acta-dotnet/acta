using System.Data.Common;

namespace Acta;

/// <summary>The outbox install transaction to probe in, under its installer lock.</summary>
internal sealed record FindOutboxTableQuery(DbTransaction Transaction);

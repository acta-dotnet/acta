using System.Data.Common;

namespace Acta;

/// <summary>The migration transaction to take the lock in, and the schema whose installers it excludes.</summary>
internal sealed record AcquireSchemaLockCommand(DbTransaction Transaction, string SchemaName);

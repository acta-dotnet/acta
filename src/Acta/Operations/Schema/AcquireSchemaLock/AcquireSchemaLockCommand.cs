using System.Data.Common;

namespace Acta;

/// <summary>
/// The transaction to take the lock in, and the key of the installers it excludes: a schema's migrations
/// (<see cref="AcquireSchemaLockHandler.LockKey"/>) or a database's outbox installs
/// (<see cref="AcquireSchemaLockHandler.OutboxLockKey"/>).
/// </summary>
internal sealed record AcquireSchemaLockCommand(DbTransaction Transaction, string Key);

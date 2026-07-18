using System.Runtime.CompilerServices;

// Provider assemblies implement the internal semantic store contracts without exposing those ports
// as public product API.
[assembly: InternalsVisibleTo("Acta.SqlServer")]
[assembly: InternalsVisibleTo("Acta.Postgres")]
[assembly: InternalsVisibleTo("Acta.Sqlite")]

// The Redis wake transport composes the internal InProcessWakeup for its local delivery layer.
[assembly: InternalsVisibleTo("Acta.Redis")]

// Shared relational stores implement the internal store ports over IDbSession from Acta.Relational.
[assembly: InternalsVisibleTo("Acta.Relational")]

// Tests need access to internal helpers (e.g., SchemaMigrationDiscovery) for unit coverage.
[assembly: InternalsVisibleTo("Acta.Tests")]
[assembly: InternalsVisibleTo("Acta.Tests.Conformance")]

// Code-emission tool reads the [DbTable]/[DbColumn] attribute graph and the ActaSchema metadata
// to render DDL and docs.
[assembly: InternalsVisibleTo("Acta.Emit")]

// Acta.Testing composes internal runtime services for deterministic host control and diagnostics.
[assembly: InternalsVisibleTo("Acta.Testing")]

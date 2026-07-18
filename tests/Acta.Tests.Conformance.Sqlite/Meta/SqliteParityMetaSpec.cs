using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Meta;

/// <summary>
/// SQLite parity gate: asserts this provider binds every capability-eligible contract spec exactly once.
/// </summary>
public sealed class SqliteParityMetaSpec : ParityMetaSpec<SqliteConformanceFixture>;

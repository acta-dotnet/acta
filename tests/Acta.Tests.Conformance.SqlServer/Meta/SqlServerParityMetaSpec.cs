using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Meta;

/// <summary>
/// SQL Server parity gate: asserts this provider binds every eligible contract spec exactly once.
/// </summary>
public sealed class SqlServerParityMetaSpec : ParityMetaSpec<SqlServerConformanceFixture>;

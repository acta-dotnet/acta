using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Meta;

/// <summary>
/// Postgres parity gate: asserts this provider binds every eligible contract spec exactly once.
/// </summary>
public sealed class PgParityMetaSpec : ParityMetaSpec<PgConformanceFixture>;

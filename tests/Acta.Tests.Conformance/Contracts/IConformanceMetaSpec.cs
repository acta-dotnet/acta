using Acta.Tests.Conformance.Testing;

namespace Acta.Tests.Conformance.Contracts;

/// <summary>
/// Marker contract for provider-specific harness meta-specs (e.g. the parity gate). Allocates no schema,
/// so it runs without a DB connection. Candidate contract specs are excluded from the parity set by
/// not implementing this type.
/// </summary>
/// <typeparam name="TFixture">The per-provider fixture this meta-spec checks.</typeparam>
public interface IConformanceMetaSpec<TFixture>
    where TFixture : IConformanceFixture, new();

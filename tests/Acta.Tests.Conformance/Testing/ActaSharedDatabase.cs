namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Fixture-driven facade over the shared <c>acta_test</c> schema bootstrap. Delegates to the
/// provider's <see cref="IConformanceFixture.CreateSchemaAsync"/> (which is itself one-shot per
/// process via the provider-specific <c>Lazy&lt;Task&gt;</c>) so the schema is bootstrapped lazily,
/// reused across tests, and never torn down by the harness - only by an explicit
/// <c>DatabaseSetup.ResetActaTestSchema</c> run.
/// </summary>
public static class ActaSharedDatabase
{
    /// <summary>
    /// Ensure the shared <c>acta_test</c> schema exists with M001 applied and the framework
    /// namespace row present. Idempotent and process-wide cached.
    /// </summary>
    public static async ValueTask<IIntegrationSchema> EnsureReadyAsync(IConformanceFixture fixture) => await fixture.CreateSchemaAsync();
}

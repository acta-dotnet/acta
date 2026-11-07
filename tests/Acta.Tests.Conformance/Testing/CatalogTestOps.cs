using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support entry points for the catalog a worker reads: a worker start writes none of it, so a spec deploys the
/// container's declared workers the way a development start does before it starts one.
/// </summary>
internal static class CatalogTestOps
{
    /// <summary>
    /// Writes the definitions and schedules of every worker <paramref name="services"/> declares, recording no version:
    /// what startup runs under <c>MigrateOnStartup</c>. The namespaces stay open to any build's workers.
    /// </summary>
    public static Task DeployAsync(IServiceProvider services, CancellationToken ct) =>
        services.GetRequiredService<CatalogDeployer>().DeployUnrecordedAsync(ct);

    /// <summary>Deploys the container's workers, then starts <paramref name="runtime"/>: one build coming up in development.</summary>
    public static async Task DeployAndStartAsync(IServiceProvider services, WorkerRuntime runtime, CancellationToken ct)
    {
        await DeployAsync(services, ct);
        await runtime.InitializeAsync(ct);
    }
}

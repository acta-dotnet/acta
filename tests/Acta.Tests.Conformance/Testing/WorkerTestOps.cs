using Acta.Features.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support entry point into the Workers feature: the atomic bootstrap through the store port
/// with production namespace canonicalization and catalog-hash computation.
/// </summary>
internal static class WorkerTestOps
{
    public static Task<StartWorkerRow> StartAsync(
        IServiceProvider services,
        string namespaceName,
        string? ownerTeam,
        string? description,
        string hostName,
        string deploymentVersion,
        string? engineVersion,
        string? dotnetVersion,
        int processId,
        int maxConcurrency,
        CancellationToken ct
    ) =>
        services
            .GetRequiredService<IWorkerStore>()
            .StartWorkerAsync(
                StartWorkerCommand.Create(
                    namespaceName,
                    ownerTeam,
                    description,
                    hostName,
                    deploymentVersion,
                    engineVersion,
                    dotnetVersion,
                    processId,
                    maxConcurrency
                ),
                ct
            );
}

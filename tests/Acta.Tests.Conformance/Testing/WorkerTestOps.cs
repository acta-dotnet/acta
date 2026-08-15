using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support entry point into the Workers feature: the atomic bootstrap through the store port
/// with production namespace canonicalization and catalog-hash computation. A caller that needs to
/// address the registration afterwards supplies <c>workerRef</c>; otherwise a fresh one is minted.
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
        CancellationToken ct,
        Guid? workerRef = null
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
                    maxConcurrency,
                    workerRef ?? WorkerRef.New().Value
                ),
                ct
            );
}

using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Test-support entry point into the Workers feature: the atomic bootstrap through the store port
/// with production namespace canonicalization. A caller that needs to address the registration
/// afterwards supplies <c>workerRef</c>; otherwise a fresh one is minted.
/// </summary>
internal static class WorkerTestOps
{
    /// <summary>The definition id a started runtime resolved for one of its jobs, read from its descriptor index.</summary>
    public static bool TryGetDefinitionId(this WorkerRuntime runtime, string jobName, out int definitionId)
    {
        foreach (var (id, descriptor) in runtime.Descriptors)
        {
            if (descriptor.JobName == jobName)
            {
                definitionId = id;
                return true;
            }
        }

        definitionId = 0;
        return false;
    }

    /// <summary>
    /// Starts a worker in a namespace the helper first leaves the way a start under <c>MigrateOnStartup</c> does:
    /// present, with the given owner and description, and with no recorded deployment, so any version may run there.
    /// </summary>
    public static async Task<(int NamespaceId, int WorkerId)> StartAsync(
        IServiceProvider services,
        string namespaceName,
        string? ownerTeam,
        string? description,
        string hostName,
        string deploymentVersion,
        string? engineVersion,
        string? dotnetVersion,
        int processId,
        int maxConcurrentExecutors,
        CancellationToken ct,
        Guid? workerRef = null
    )
    {
        await EnsureNamespaceAsync(services, namespaceName, ownerTeam, description, ct);
        var started = await services
            .GetRequiredService<WorkersOperations>()
            .StartWorkerAsync(
                StartWorkerCommand.Create(
                    namespaceName,
                    hostName,
                    deploymentVersion,
                    engineVersion,
                    dotnetVersion,
                    processId,
                    maxConcurrentExecutors,
                    workerRef ?? WorkerRef.New().Value,
                    requiresDeployment: true
                ),
                ct
            );
        return (started!.NamespaceId, started.WorkerId!.Value);
    }

    /// <summary>
    /// The namespace as an unrecorded deploy leaves it: inserted when the name is absent, its owner and description
    /// written when they differ, no version recorded.
    /// </summary>
    public static Task<int> EnsureNamespaceAsync(
        IServiceProvider services,
        string namespaceName,
        string? ownerTeam,
        string? description,
        CancellationToken ct
    )
    {
        var deployments = services.GetRequiredService<DeploymentsOperations>();
        var name = NameSyntax.Canonicalize(namespaceName, nameof(namespaceName), reserveSystem: true);
        return services
            .GetRequiredService<IDbSession>()
            .RunInTransactionAsync(
                async (transaction, token) =>
                {
                    var opened = (await deployments.StartDeploymentAsync(transaction, [name], "unrecorded", token)).Namespaces[0];
                    await deployments.RecordDeploymentAsync(
                        new(transaction, opened.NamespaceId, null, ownerTeam, description, null, false, [], []),
                        token
                    );
                    return opened.NamespaceId;
                },
                ct
            );
    }
}

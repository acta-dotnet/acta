using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// Deploys a host's catalog: every namespace it runs a worker for gets its definitions and schedules written by
/// <see cref="CatalogRegistrar"/> and the deploying version recorded, all in one transaction, so a failure in any
/// namespace leaves every one as it was. A version that deployed in a namespace before and is not its current one is an
/// older build: it writes nothing there unless the deploy is forced.
/// </summary>
internal sealed class CatalogDeployer(
    CatalogRegistrar registrar,
    DeploymentsOperations deployments,
    IDbSession session,
    IOptions<JobsOptions> options
)
{
    /// <summary>
    /// Deploys <paramref name="registrations"/> and answers what happened in each namespace, in name order. The
    /// transaction's first statement takes every namespace's schedule lock, so two deploys that share a namespace run
    /// one after the other.
    /// </summary>
    public async Task<IReadOnlyList<NamespaceDeployment>> DeployAsync(
        IReadOnlyList<WorkerRegistration> registrations,
        bool force,
        CancellationToken ct
    )
    {
        if (registrations.Count == 0)
        {
            return [];
        }

        var version = WorkerRuntimeInitializer.DeploymentVersionOf(options.Value);

        // Every namespace's descriptors pass their shape checks before the transaction opens.
        var planned = registrations
            .OrderBy(r => r.NamespaceName, StringComparer.Ordinal)
            .Select(r => (Registration: r, Descriptors: registrar.CollectDescriptors(r, r.NamespaceName)))
            .ToList();

        return await session.RunInTransactionAsync(
            (transaction, token) => DeployOnceAsync(transaction, planned, version, force, token),
            ct
        );
    }

    private async Task<IReadOnlyList<NamespaceDeployment>> DeployOnceAsync(
        DbTransaction transaction,
        List<(WorkerRegistration Registration, ImmutableArray<JobDescriptor> Descriptors)> planned,
        string version,
        bool force,
        CancellationToken ct
    )
    {
        var environment = options.Value.EnvironmentName;
        var started = (
            await deployments.StartDeploymentAsync(transaction, [.. planned.Select(p => p.Registration.NamespaceName)], version, ct)
        ).Namespaces.ToDictionary(n => n.NamespaceName, StringComparer.Ordinal);

        var results = new List<NamespaceDeployment>(planned.Count);
        foreach (var (registration, descriptors) in planned)
        {
            var ns = registration.NamespaceName;
            var start = started[ns];
            if (!force && start.IsDeployed && !string.Equals(start.CurrentDeploymentVersion, version, StringComparison.Ordinal))
            {
                results.Add(new(ns, version, environment, start.CurrentDeploymentVersion, IsOlder: true, [], [], 0, []));
                continue;
            }

            var (definitions, undeclared) = await registrar.RegisterAsync(
                registration,
                start.NamespaceId,
                registeredCatalogHash: null,
                descriptors,
                transaction,
                ct
            );
            await deployments.RecordDeploymentAsync(
                new(
                    transaction,
                    start.NamespaceId,
                    version,
                    registration.OwnerTeam,
                    registration.Description,
                    start.CurrentDeploymentVersion,
                    force,
                    definitions.Added,
                    definitions.Changed
                ),
                ct
            );
            results.Add(
                new(
                    ns,
                    version,
                    environment,
                    version,
                    IsOlder: false,
                    definitions.Added,
                    definitions.Changed,
                    descriptors.Length - definitions.Added.Count - definitions.Changed.Count,
                    undeclared
                )
            );
        }

        return results;
    }
}

using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// Deploys the host's catalog: every namespace it runs a worker for gets its definitions and schedules written by
/// <see cref="CatalogRegistrar"/>, all in one transaction, so a failure in any namespace leaves every one as it was.
/// A worker start writes none of it.
/// </summary>
internal sealed class CatalogDeployer(
    CatalogRegistrar registrar,
    DeploymentsOperations deployments,
    IDbSession session,
    IActaClock clock,
    IOptions<JobsOptions> options,
    IReadOnlyList<WorkerRegistration> registrations,
    ILogger log
)
{
    /// <summary>
    /// What <c>jobs deploy</c> and <c>jobs migrate</c> run: deploys each namespace and records the deploying version
    /// there, and answers what happened in each, in name order. A version that deployed in a namespace before and is
    /// not its current one is an older build: it writes nothing there unless <paramref name="force"/> is set.
    /// </summary>
    public Task<IReadOnlyList<NamespaceDeployment>> DeployAsync(bool force, CancellationToken ct) => DeployAsync(recorded: true, force, ct);

    /// <summary>
    /// What a start under <c>MigrateOnStartup</c> runs, before any worker starts: deploys each namespace no verb has
    /// deployed and records nothing, so the namespace stays open to any build. A namespace with a recorded version is
    /// left as it is.
    /// </summary>
    public Task DeployUnrecordedAsync(CancellationToken ct) => DeployAsync(recorded: false, force: false, ct);

    private async Task<IReadOnlyList<NamespaceDeployment>> DeployAsync(bool recorded, bool force, CancellationToken ct)
    {
        if (registrations.Count == 0)
        {
            return [];
        }

        var version = WorkerRuntimeInitializer.DeploymentVersionOf(options.Value);

        // Every namespace's descriptors pass their shape checks before the transaction opens.
        var planned = registrations
            .OrderBy(r => r.NamespaceName, StringComparer.Ordinal)
            .Select(r => (Registration: r, Descriptors: CatalogRegistrar.CollectDescriptors(r, options.Value)))
            .ToList();

        // The clock is read before the transaction opens, so the deploy holds one connection, its transaction's, from
        // its first statement to its last.
        var nowUtc = await clock.GetUtcNowAsync(ct);
        return await session.RunInTransactionAsync(
            (transaction, token) => DeployOnceAsync(transaction, planned, version, nowUtc, recorded, force, token),
            ct
        );
    }

    /// <summary>
    /// One attempt. The first statement takes every namespace's schedule lock, so two deploys that share a namespace
    /// run one after the other.
    /// </summary>
    private async Task<IReadOnlyList<NamespaceDeployment>> DeployOnceAsync(
        DbTransaction transaction,
        List<(WorkerRegistration Registration, ImmutableArray<JobDescriptor> Descriptors)> planned,
        string version,
        DateTime nowUtc,
        bool recorded,
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
            if (!recorded && start.CurrentDeploymentVersion is { } deployedVersion)
            {
                if (log.IsEnabled(LogLevel.Information))
                {
                    var detail = $"version {deployedVersion}";
                    ActaLog.StartupDeploySkipped(log, ns, detail);
                }
                continue;
            }

            if (recorded && !force && start.IsDeployed && !string.Equals(start.CurrentDeploymentVersion, version, StringComparison.Ordinal))
            {
                results.Add(new(ns, version, environment, start.CurrentDeploymentVersion, IsOlder: true, [], [], 0, []));
                continue;
            }

            var (definitions, undeclared) = await registrar.RegisterAsync(
                registration,
                start.NamespaceId,
                descriptors,
                version,
                nowUtc,
                transaction,
                ct
            );
            await deployments.RecordDeploymentAsync(
                new(
                    transaction,
                    start.NamespaceId,
                    recorded ? version : null,
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

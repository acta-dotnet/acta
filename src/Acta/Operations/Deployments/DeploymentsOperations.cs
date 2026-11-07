using System.Data.Common;

namespace Acta;

/// <summary>The Deployments operations: the two ends of a deploy's transaction.</summary>
internal sealed class DeploymentsOperations(StartDeploymentHandler startDeployment, RecordDeploymentHandler recordDeployment)
{
    /// <summary>
    /// Opens a deploy inside <paramref name="transaction"/>: inserts each namespace whose name is absent, takes every
    /// namespace's schedule lock until the transaction ends, and answers for each the version it stands at and whether
    /// <paramref name="deploymentVersion"/> has deployed there before.
    /// </summary>
    internal Task<StartDeploymentResponse> StartDeploymentAsync(
        DbTransaction transaction,
        IReadOnlyList<string> namespaceNames,
        string deploymentVersion,
        CancellationToken ct
    ) => startDeployment.HandleAsync(new StartDeploymentCommand(transaction, namespaceNames, deploymentVersion), ct);

    /// <summary>
    /// Closes one namespace's deploy: keeps a row for the version, makes it the current one, stores the declared owner
    /// and description, and writes the <c>namespace.deployed</c> event. Only the event is written when the rest
    /// already stands.
    /// </summary>
    internal Task RecordDeploymentAsync(RecordDeploymentCommand command, CancellationToken ct) => recordDeployment.HandleAsync(command, ct);
}

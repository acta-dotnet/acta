namespace Acta;

/// <summary>
/// <see cref="DeploymentsOperations.RecordDeploymentAsync"/>: the deploy's last write, which makes the version the
/// namespace's current one, keeps a row for it, and leaves a <c>namespace.deployed</c> event; with no version it
/// writes the declared owner and description alone.
/// </summary>
internal sealed class RecordDeploymentHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(RecordDeploymentCommand command, CancellationToken ct) =>
        session.ExecuteInTransactionAsync(
            command.Transaction,
            new StoreCommand("RecordDeployment"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Deployment.NamespaceId, command.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.DeploymentVersion, command.DeploymentVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.OwnerTeam, command.OwnerTeam));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.Description, command.Description));
                cmd.AddEventFields(
                    dialect,
                    Message(command).Truncate(ActaTextLimits.EventMessage),
                    command.DeploymentVersion is null
                        ? null
                        : EventDetails.Write(
                            ("deploymentVersion", command.DeploymentVersion),
                            ("previousDeploymentVersion", command.PreviousDeploymentVersion),
                            ("forced", command.Forced ? "true" : null),
                            ("added", Names(command.Added)),
                            ("changed", Names(command.Changed))
                        )
                );
            },
            static _ => 0,
            ct
        );

    private static string? Message(RecordDeploymentCommand command) =>
        command.PreviousDeploymentVersion switch
        {
            _ when command.DeploymentVersion is null => null,
            null => $"{command.DeploymentVersion} deployed",
            var previous when previous == command.DeploymentVersion => $"{command.DeploymentVersion} deployed again",
            var previous => $"{command.DeploymentVersion} deployed over {previous}",
        };

    private static string? Names(IReadOnlyList<string> names) => names.Count == 0 ? null : string.Join(", ", names);
}

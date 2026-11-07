using System.Text;
using System.Text.Json;

namespace Acta;

/// <summary>
/// <see cref="DeploymentsOperations.StartDeploymentAsync"/>: ensures every namespace's row, takes their schedule locks
/// for the rest of the caller's transaction, and reads what the deploy decides on.
/// </summary>
internal sealed class StartDeploymentHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<StartDeploymentResponse> HandleAsync(StartDeploymentCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteInTransactionAsync(
            command.Transaction,
            new StoreCommand("StartDeployment"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceNamesJson, NamesJson(command.NamespaceNames)));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.NamespaceStatusCode, NamespaceStatusCode.Active));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Deployment.DeploymentVersion, command.DeploymentVersion));
            },
            DbProjectionResolver.Resolve<StartDeploymentRow>(),
            ct
        );
        return rows.Count == command.NamespaceNames.Count
            ? new StartDeploymentResponse(rows)
            : throw new InvalidOperationException(
                $"StartDeployment returned {rows.Count} rows for {command.NamespaceNames.Count} namespaces. It must return one per namespace."
            );
    }

    private static string NamesJson(IReadOnlyList<string> names)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var name in names)
            {
                json.WriteStringValue(name);
            }
            json.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

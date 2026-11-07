using System.Data.Common;

namespace Acta;

/// <summary>The namespaces a deploy is about to write and the version deploying, in the deploy's own transaction.</summary>
internal sealed record StartDeploymentCommand(DbTransaction Transaction, IReadOnlyList<string> NamespaceNames, string DeploymentVersion);

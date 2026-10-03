namespace Acta.Runtime;

/// <summary>The definition to read, by its natural key.</summary>
internal sealed record GetJobDefinitionQuery(string JobNamespace, string JobName);

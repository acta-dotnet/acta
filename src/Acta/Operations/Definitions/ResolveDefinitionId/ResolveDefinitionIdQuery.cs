namespace Acta;

/// <summary>The definition whose catalog id to find, by its natural key.</summary>
internal sealed record ResolveDefinitionIdQuery(string JobNamespace, string JobName);

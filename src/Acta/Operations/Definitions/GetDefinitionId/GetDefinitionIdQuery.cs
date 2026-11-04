namespace Acta;

/// <summary>The definition whose catalog id to find, by its natural key.</summary>
internal sealed record GetDefinitionIdQuery(string JobNamespace, string JobName);

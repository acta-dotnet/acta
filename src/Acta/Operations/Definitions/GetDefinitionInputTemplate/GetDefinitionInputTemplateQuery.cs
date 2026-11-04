namespace Acta;

/// <summary>The registered job whose input contract to read.</summary>
internal sealed record GetDefinitionInputTemplateQuery(string JobNamespace, string JobName);

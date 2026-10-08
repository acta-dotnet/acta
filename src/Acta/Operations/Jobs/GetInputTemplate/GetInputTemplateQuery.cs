namespace Acta;

/// <summary>The registered job whose input contract to read.</summary>
internal sealed record GetInputTemplateQuery(string JobNamespace, string JobName);

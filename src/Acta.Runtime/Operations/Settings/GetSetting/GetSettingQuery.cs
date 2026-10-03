namespace Acta.Runtime;

/// <summary>One setting by name at the exact scope its targets name.</summary>
internal sealed record GetSettingQuery(string Name, string? NamespaceName, string? JobName);

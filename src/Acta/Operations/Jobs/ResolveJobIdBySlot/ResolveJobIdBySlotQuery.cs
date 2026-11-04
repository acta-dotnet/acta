namespace Acta;

/// <summary>The scheduled definition whose permanent slot is wanted, by its path.</summary>
internal sealed record ResolveJobIdBySlotQuery(string JobNamespace, string JobName);

namespace Acta;

/// <summary>One job found by its ref, with the namespace and definition it runs under.</summary>
[DbProjection(ByName = true)]
internal sealed record ListJobsByRefItem(Guid JobRef, string JobNamespace, string JobName);

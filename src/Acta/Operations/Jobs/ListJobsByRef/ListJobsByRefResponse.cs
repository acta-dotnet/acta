namespace Acta;

/// <summary>The jobs holding the refs asked for; a ref no job holds is absent.</summary>
internal sealed record ListJobsByRefResponse(IReadOnlyList<ListJobsByRefItem> Jobs);

namespace Acta;

/// <summary>The parent's children under the names asked for; a name no child holds is absent.</summary>
internal sealed record ListChildJobsByNameResponse(IReadOnlyList<ListChildJobsByNameItem> Children);

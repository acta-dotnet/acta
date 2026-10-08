namespace Acta;

/// <summary>One normalized tag attached to a target.</summary>
public sealed record GetTagsItem(string Name, string? Value = null);

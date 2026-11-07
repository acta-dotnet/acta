namespace Acta;

/// <summary>One normalized tag attached to a target.</summary>
/// <param name="Name">The tag's name, trimmed and lowercased.</param>
/// <param name="Value">The tag's value; null for a tag that is only a name.</param>
public sealed record ListTagsItem(string Name, string? Value = null);

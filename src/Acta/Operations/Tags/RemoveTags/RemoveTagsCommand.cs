namespace Acta;

/// <summary>One tag name to remove from one target.</summary>
internal sealed record RemoveTagsCommand(TagTarget Target, string Name);

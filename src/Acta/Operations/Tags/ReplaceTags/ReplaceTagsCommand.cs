namespace Acta;

/// <summary>The whole tag set to put on one target; an empty list clears it.</summary>
internal sealed record ReplaceTagsCommand(TagTarget Target, IReadOnlyList<TagInput> Tags);

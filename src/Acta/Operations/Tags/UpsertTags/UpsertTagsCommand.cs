namespace Acta;

/// <summary>One tag to insert or replace on one target.</summary>
internal sealed record UpsertTagsCommand(TagTarget Target, TagInput Tag);

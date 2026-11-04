namespace Acta.AspNetCore;

/// <summary>The body of a tag upsert on any target: the tag's name, which is normalized, and its optional value.</summary>
internal interface IUpsertTagRequest
{
    string? Name { get; }

    string? Value { get; }
}

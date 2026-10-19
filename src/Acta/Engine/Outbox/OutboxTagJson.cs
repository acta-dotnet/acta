namespace Acta;

/// <summary>One <c>meta.tags</c> entry: a required <c>name</c> and a string-or-null <c>value</c>.</summary>
internal sealed record OutboxTagJson(string? Name, string? Value);

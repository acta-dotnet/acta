namespace Acta;

/// <summary>
/// One bound parameter for a provider command. Production code binds through
/// <c>dialect.CreateParameter(ActaSchema..., value)</c> so generated metadata owns name, kind,
/// and width; code that needs the spec itself (a parameter list) calls <c>DbParams.For</c>.
/// </summary>
internal sealed record DbParameterSpec(string Name, object? Value, DbKind Kind, int? Size = null);

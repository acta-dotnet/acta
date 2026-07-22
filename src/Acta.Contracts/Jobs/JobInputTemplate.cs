namespace Acta;

/// <summary>
/// The input contract of a registered job as operator tooling sees it: the CLR input type name, the
/// wire format its input serializes to, and (Json inputs only) a compile-time JSON skeleton the
/// enqueue form can seed its editor with. Compile-time facts from the generated manifest; the
/// template is a shape hint, never a contract and never a default value.
/// </summary>
public sealed record JobInputTemplate(string InputTypeName, JobPayloadFormat InputFormat, string? TemplateJson);

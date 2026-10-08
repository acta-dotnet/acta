using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The input contract of a registered job as operator tooling sees it: the job it was asked for, the CLR input type
/// name, the wire format its input serializes to, and (Json inputs only) a compile-time JSON skeleton the enqueue form
/// can seed its editor with. Compile-time facts from the generated manifest; the template is a shape hint, never a
/// contract and never a default value.
/// </summary>
public sealed record GetInputTemplateResponse(
    string JobNamespace,
    string JobName,
    string InputTypeName,
    [property: JsonIgnore] JobPayloadFormat InputFormat,
    string? TemplateJson
)
{
    /// <summary>The name of <see cref="InputFormat"/>, such as <c>json</c>, which is how the wire names a format.</summary>
    public string InputFormatName => InputFormat.Name;
}

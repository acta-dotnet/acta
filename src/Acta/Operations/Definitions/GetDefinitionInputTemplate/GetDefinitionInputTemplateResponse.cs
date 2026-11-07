using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The input contract of a registered job as operator tooling sees it: the job it was asked for, the CLR input type
/// name, the wire format its input serializes to, and (Json inputs only) a compile-time JSON skeleton the enqueue form
/// can seed its editor with. Compile-time facts from the generated manifest; the template is a shape hint, never a
/// contract and never a default value.
/// </summary>
/// <param name="InputFormat">The input's payload format; the JSON omits it.</param>
/// <param name="JobNamespace">The definition's namespace.</param>
/// <param name="JobName">The definition's name within its namespace.</param>
/// <param name="InputTypeName">The full name of the .NET type the definition's handler takes as input.</param>
/// <param name="TemplateJson">A JSON template of the input to start from; null when the input has no JSON form.</param>
public sealed record GetDefinitionInputTemplateResponse(
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

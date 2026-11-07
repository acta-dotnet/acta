using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// A definition's input contract: the name of the type its handler takes, the format its input is stored in, and, for a JSON input,
/// a template the enqueue form can start its editor from. The template comes from the deployed code and shows the input's shape; it
/// is no contract and no default value.
/// </summary>
/// <param name="InputFormat">The input's payload format; the JSON omits it.</param>
/// <param name="JobNamespace">The definition's namespace.</param>
/// <param name="JobName">The definition's name within its namespace.</param>
/// <param name="InputTypeName">The full name of the type the definition's handler takes as input, as its code spells it.</param>
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

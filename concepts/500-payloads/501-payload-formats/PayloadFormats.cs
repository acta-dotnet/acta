using Acta.Payloads;

namespace Acta.Concepts.PayloadFormats;

/// <summary>
/// Format names and ids for the three custom serializers. The kebab-case names are <c>const string</c>
/// because <c>[Job(Format = ...)]</c> needs a compile-time constant, and hand-written because the
/// source generator reads them as ordinary source while building descriptors.
/// </summary>
public static class PayloadFormats
{
    public const string ScalarV1 = "scalar-v1";
    public const string Msgpack = "msgpack";
    public const string JsonGzip = "json-gzip";

    public static readonly JobPayloadFormat ScalarV1Format = JobPayloadFormat.Custom(128, ScalarV1);
    public static readonly JobPayloadFormat MsgpackFormat = JobPayloadFormat.Custom(129, Msgpack);
    public static readonly JobPayloadFormat JsonGzipFormat = JobPayloadFormat.Custom(130, JsonGzip);
}

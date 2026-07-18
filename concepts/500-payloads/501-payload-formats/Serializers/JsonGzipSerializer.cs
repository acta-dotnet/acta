using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Acta.Payloads;

namespace Acta.Concepts.PayloadFormats;

[JobPayloadFormatDeclaration(130, "json-gzip")]
public sealed class JsonGzipSerializer : IJobPayloadSerializer
{
    private static readonly JsonSerializerOptions DefaultJson = new(JsonSerializerDefaults.Web);

    private readonly JsonSerializerOptions _json;

    public JsonGzipSerializer()
        : this(DefaultJson) { }

    public JsonGzipSerializer(JsonSerializerOptions json)
    {
        _json = json;
    }

    public JobPayloadFormat Format => PayloadFormats.JsonGzipFormat;

    public JobPayload Serialize<T>(T value)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, _json);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(jsonBytes);
        }
        return JobPayload.FromBytes(Format, output.ToArray());
    }

    public T Deserialize<T>(JobPayload payload)
    {
        if (payload.Format.Id != Format.Id)
        {
            throw new InvalidOperationException($"Expected payload format {Format}, got {payload.Format}.");
        }

        if (!MemoryMarshal.TryGetArray(payload.Data, out var segment) || segment.Array is null)
        {
            throw new InvalidOperationException("json-gzip expects array-backed payload memory.");
        }

        using var input = new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false, publiclyVisible: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);

        return JsonSerializer.Deserialize<T>(gzip, _json) ?? throw new JsonException($"Could not deserialize {typeof(T).FullName}.");
    }
}

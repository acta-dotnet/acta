using System.Text;
using System.Text.Json;
using Xunit;

namespace Acta.Tests.Wire;

public class JobPayloadTests
{
    [Fact]
    public void None_HasNoneFormatAndEmptyBytes()
    {
        var payload = JobPayload.None;

        Assert.Equal(JobPayloadFormat.None, payload.Format);
        Assert.True(payload.Format.IsNone);
        Assert.True(payload.Data.IsEmpty);
        Assert.True(payload.IsNone);
        Assert.True(payload.IsEmpty);
    }

    [Fact]
    public void Default_EqualsNone()
    {
        var payload = default(JobPayload);

        Assert.Equal(JobPayloadFormat.None, payload.Format);
        Assert.True(payload.Data.IsEmpty);
        Assert.True(payload.IsNone);
    }

    [Fact]
    public void FromBytes_TakesOwnershipWithoutCopying()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var payload = JobPayload.FromBytes(JobPayloadFormat.Bytes, bytes);

        bytes[0] = 9;

        Assert.Equal(9, payload.Data.Span[0]);
    }

    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("42")]
    [InlineData("\"\"")]
    [InlineData("  [1, 2]  ")]
    [InlineData("null")]
    public void FromBytes_OneStrictJsonValue_IsAccepted(string json) =>
        Assert.Equal(JobPayloadFormat.Json, JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes(json)).Format);

    // Depth is the serializer's to police, so a value deeper than the reader's default of 64 still passes the check.
    [Fact]
    public void FromBytes_DeeplyNestedJson_IsAccepted() =>
        Assert.Equal(
            JobPayloadFormat.Json,
            JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes(new string('[', 100) + new string(']', 100))).Format
        );

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{\"a\":1} // note")]
    [InlineData("1 2")]
    [InlineData("{broken")]
    [InlineData("hello")]
    public void FromBytes_AnythingButOneStrictJsonValue_Throws(string json)
    {
        Assert.Throws<ArgumentException>(() => JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes(json)));
        Assert.Throws<ArgumentException>(() => JobPayload.CopyBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("[1,2,]")]
    [InlineData("[1,2] /* note */")]
    public void Serializer_reads_strict_json_only(string json) =>
        Assert.ThrowsAny<JsonException>(() =>
            JsonJobPayloadSerializer.Default.Deserialize<int[]>(JobPayload.Trusted(JobPayloadFormat.Json, Encoding.UTF8.GetBytes(json)))
        );

    [Fact]
    public void Trusted_SkipsTheJsonCheck() =>
        Assert.Equal("{broken"u8.ToArray(), JobPayload.Trusted(JobPayloadFormat.Json, "{broken"u8.ToArray()).Data.ToArray());

    [Fact]
    public void Json_StringHoldingNul_IsEscaped()
    {
        Assert.Equal("\"a\\u0000b\""u8.ToArray(), JobPayload.Json("a\0b").Data.ToArray());
    }

    [Fact]
    public void FromBytes_InvalidUtf8UnderJson_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => JobPayload.FromBytes(JobPayloadFormat.Json, [0x7B, 0xFF, 0x7D]));

        Assert.Contains("json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyBytes_NulUnderJson_Throws()
    {
        Assert.Throws<ArgumentException>(() => JobPayload.CopyBytes(JobPayloadFormat.Json, "{\"a\":\"\0\"}"u8));
    }

    [Fact]
    public void Json_MultiByteUtf8_RoundTrips()
    {
        const string text = "Vončina 東京 \U0001F600";

        Assert.Equal(text, JsonJobPayloadSerializer.Default.Deserialize<string>(JobPayload.Json(text)));
    }

    [Fact]
    public void BinaryFormats_CarryNulAndInvalidUtf8()
    {
        byte[] bytes = [0x00, 0xFF, 0x00];

        Assert.Equal(bytes, JobPayload.Bytes(bytes).Data.ToArray());
        Assert.Equal(bytes, JobPayload.CopyBytes(JobPayloadFormat.Custom(200, "msgpack"), bytes).Data.ToArray());
    }
}

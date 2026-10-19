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
        var payload = JobPayload.FromBytes(JobPayloadFormat.Json, bytes);

        bytes[0] = 9;

        Assert.Equal(9, payload.Data.Span[0]);
    }

    [Fact]
    public void Text_HoldingNul_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => JobPayload.Text("a\0b"));

        Assert.Contains("text", ex.Message, StringComparison.Ordinal);
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
    public void Text_MultiByteUtf8_IsAccepted()
    {
        const string text = "Vončina 東京 \U0001F600";

        Assert.Equal(text, TextJobPayloadSerializer.Default.Deserialize<string>(JobPayload.Text(text)));
    }

    [Fact]
    public void BinaryFormats_CarryNulAndInvalidUtf8()
    {
        byte[] bytes = [0x00, 0xFF, 0x00];

        Assert.Equal(bytes, JobPayload.Bytes(bytes).Data.ToArray());
        Assert.Equal(bytes, JobPayload.CopyBytes(JobPayloadFormat.Custom(200, "msgpack"), bytes).Data.ToArray());
    }
}

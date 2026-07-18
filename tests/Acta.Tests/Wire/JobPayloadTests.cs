using Acta.Payloads;
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
}

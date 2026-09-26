using Acta.Runtime.Modules.Execution.Jobs;
using Xunit;

namespace Acta.Tests.Wire;

/// <summary>
/// A lane id follows the concurrency-key rule at every entry point: trimmed, lowercased, 1 to 128
/// characters of the key alphabet, and never under the reserved <c>sys.</c> prefix.
/// </summary>
public sealed class LaneOptionTests
{
    public static TheoryData<string> InvalidLanes => ["", "   ", "has space", "café", "sys.reserved", new string('l', 129)];

    [Fact]
    public void Options_builder_canonicalizes_the_lane()
    {
        Assert.Equal("customer-42", new JobEnqueueOptionsBuilder().Lane("  Customer-42 ").Build().Lane);
    }

    [Fact]
    public void Options_builder_leaves_the_lane_unset_by_default()
    {
        Assert.Null(new JobEnqueueOptionsBuilder().Build().Lane);
    }

    [Theory]
    [MemberData(nameof(InvalidLanes))]
    public void Options_builder_rejects_an_invalid_lane(string lane)
    {
        Assert.ThrowsAny<ArgumentException>(() => new JobEnqueueOptionsBuilder().Lane(lane));
    }

    [Fact]
    public void Options_builder_rejects_a_null_lane()
    {
        Assert.Throws<ArgumentNullException>(() => new JobEnqueueOptionsBuilder().Lane(null!));
    }

    [Fact]
    public void Request_builder_canonicalizes_the_lane()
    {
        Assert.Equal("customer-42", JobRequestBuilder.Create("billing", "send-invoice").Lane("Customer-42").Build().Lane);
    }

    [Theory]
    [MemberData(nameof(InvalidLanes))]
    public void Request_builder_rejects_an_invalid_lane(string lane)
    {
        Assert.ThrowsAny<ArgumentException>(() => JobRequestBuilder.Create("billing", "send-invoice").Lane(lane));
    }

    [Fact]
    public void Request_validation_canonicalizes_the_lane()
    {
        var request = new JobEnqueueRequest("billing", "send-invoice", Lane: " Customer-42 ");

        Assert.Equal("customer-42", JobEnqueueRequestValidation.NormalizeAndValidate(request, nameof(request)).Lane);
    }

    [Theory]
    [MemberData(nameof(InvalidLanes))]
    public void Request_validation_rejects_an_invalid_lane(string lane)
    {
        var request = new JobEnqueueRequest("billing", "send-invoice", Lane: lane);

        Assert.ThrowsAny<ArgumentException>(() => JobEnqueueRequestValidation.NormalizeAndValidate(request, nameof(request)));
    }

    [Fact]
    public void Enqueue_row_canonicalization_folds_the_lane()
    {
        var row = JobEnqueueRows.Canonicalize(new JobEnqueueRow("billing", "send-invoice", JobPayload.None, Lane: "Customer-42"));

        Assert.Equal("customer-42", row.Lane);
    }

    [Theory]
    [MemberData(nameof(InvalidLanes))]
    public void Enqueue_row_canonicalization_rejects_an_invalid_lane(string lane)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            JobEnqueueRows.Canonicalize(new JobEnqueueRow("billing", "send-invoice", JobPayload.None, Lane: lane))
        );
    }
}

using System.Text.Json;
using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// Freezes the public JobRef format: "job_" plus the Guid's 32 lowercase hex digits, its N format. The golden pair
/// below is the format contract; changing either value breaks every externally stored job ref.
/// </summary>
public class JobRefTests
{
    private static readonly Guid GoldenGuid = Guid.Parse("01a14f4e-954e-7bf6-8ac6-09f3fcad8e3d");
    private const string GoldenRef = "job_01a14f4e954e7bf68ac609f3fcad8e3d";

    [Fact]
    public void Formats_golden_guid_stably()
    {
        Assert.Equal(GoldenRef, new JobRef(GoldenGuid).ToString());
    }

    [Fact]
    public void Parses_golden_ref_back_to_guid()
    {
        Assert.True(JobRef.TryParse(GoldenRef, out var jobRef));
        Assert.Equal(GoldenGuid, jobRef.Value);
    }

    [Fact]
    public void Parses_uppercase_hex_and_emits_lowercase()
    {
        Assert.True(JobRef.TryParse("job_01A14F4E954E7BF68AC609F3FCAD8E3D", out var jobRef));
        Assert.Equal(GoldenGuid, jobRef.Value);
        Assert.Equal(GoldenRef, jobRef.ToString());
    }

    [Fact]
    public void Rejects_the_crockford_form()
    {
        Assert.False(JobRef.TryParse("job_2n1t201rmv87aae5j4csam8000", out _));
        Assert.False(JobRef.TryParse("job_01m3f3jf65ettsq0k29zbzjk0h", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("job_")]
    [InlineData("01a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("01a14f4e-954e-7bf6-8ac6-09f3fcad8e3d")]
    [InlineData("job_01a14f4e-954e-7bf6-8ac6-09f3fcad8e3d")]
    [InlineData("job_{01a14f4e954e7bf68ac609f3fcad8e3d}")]
    [InlineData("JOB_01a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("Job_01a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("job_01a14f4e954e7bf68ac609f3fcad8e3")]
    [InlineData("job_01a14f4e954e7bf68ac609f3fcad8e3d0")]
    [InlineData("job_01a14f4e954e7bf68ac609f3fcad8e3g")]
    [InlineData("job_+1a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("job_0xa14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData(" job_01a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("job_01a14f4e954e7bf68ac609f3fcad8e3d ")]
    [InlineData("job_ 1a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("job-01a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("ref_01a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("alr_01a14f4e954e7bf68ac609f3fcad8e3d")]
    public void Rejects_malformed_refs(string? value)
    {
        Assert.False(JobRef.TryParse(value, out _));
    }

    [Fact]
    public void Parse_throws_on_malformed_ref()
    {
        Assert.Throws<FormatException>(() => JobRef.Parse("job_nope"));
    }

    [Fact]
    public void New_creates_unique_round_trippable_refs()
    {
        var a = JobRef.New();
        var b = JobRef.New();
        Assert.NotEqual(a, b);
        Assert.True(JobRef.TryParse(a.ToString(), out var parsed));
        Assert.Equal(a, parsed);
    }

    [Fact]
    public void Round_trips_arbitrary_guids()
    {
        for (var i = 0; i < 100; i++)
        {
            var guid = Guid.NewGuid();
            Assert.Equal(JobRef.Prefix + guid.ToString("N"), new JobRef(guid).ToString());
            Assert.True(JobRef.TryParse(new JobRef(guid).ToString(), out var parsed));
            Assert.Equal(guid, parsed.Value);
        }
    }

    [Fact]
    public void Json_round_trips_as_canonical_string()
    {
        var json = JsonSerializer.Serialize(new JobRef(GoldenGuid));
        Assert.Equal($"\"{GoldenRef}\"", json);
        Assert.Equal(new JobRef(GoldenGuid), JsonSerializer.Deserialize<JobRef>(json));
    }

    [Fact]
    public void Json_rejects_malformed_ref()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JobRef>("\"job_nope\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JobRef>("\"job_2n1t201rmv87aae5j4csam8000\""));
    }
}

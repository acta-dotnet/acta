using System.Text.Json;
using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// Freezes the public JobRef format: "job_" plus 26 lowercase Crockford Base32 characters over
/// the canonical big-endian UUID bytes. The golden pair below is the format contract; changing
/// either value breaks every externally stored job ref.
/// </summary>
public class JobRefTests
{
    private static readonly Guid GoldenGuid = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
    private const string GoldenRef = "job_2n1t201rmv87aae5j4csam8000";

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
    public void Parses_case_insensitively_and_emits_canonical_lowercase()
    {
        Assert.True(JobRef.TryParse(GoldenRef.ToUpperInvariant(), out var jobRef));
        Assert.Equal(GoldenGuid, jobRef.Value);
        Assert.Equal(GoldenRef, jobRef.ToString());
    }

    [Theory]
    [InlineData("job_2n1t2o1rmv87aae5j4csam8000")]
    [InlineData("job_2n1t20lrmv87aae5j4csam8000")]
    [InlineData("job_2n1t20irmv87aae5j4csam8000")]
    [InlineData("job_2N1T2O1RMV87AAE5J4CSAM8000")]
    public void Parses_crockford_aliases(string aliased)
    {
        Assert.True(JobRef.TryParse(aliased, out var jobRef));
        Assert.Equal(GoldenGuid, jobRef.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("job_")]
    [InlineData("2n1t201rmv87aae5j4csam8000")]
    [InlineData("job_2n1t201rmv87aae5j4csam800")]
    [InlineData("job_2n1t201rmv87aae5j4csam80000")]
    [InlineData("job_2n1t201rmv87aae5j4csam800u")]
    [InlineData("job_zn1t201rmv87aae5j4csam8000")]
    [InlineData("ref_2n1t201rmv87aae5j4csam8000")]
    [InlineData("job-2n1t201rmv87aae5j4csam8000")]
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
    }
}

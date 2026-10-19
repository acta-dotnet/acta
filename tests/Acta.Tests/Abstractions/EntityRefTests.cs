using System.Text.Json;
using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// Freezes the properties every minted entity ref shares through the common codec: an exactly three-letter
/// lowercase prefix plus an underscore, type-checked parsing that rejects a sibling type's value, the documented
/// Guid.Empty policy, hex parsing in any casing with lowercase emission, and JSON round-tripping. JobRefTests pins
/// the job_ golden pair; this suite pins the shape that alr_ and wrk_ share with it.
/// </summary>
public class EntityRefTests
{
    private static readonly Guid GoldenGuid = Guid.Parse("01a14f4e-954e-7bf6-8ac6-09f3fcad8e3d");
    private const string GoldenPayload = "01a14f4e954e7bf68ac609f3fcad8e3d";
    private const string EmptyPayload = "00000000000000000000000000000000";

    /// <summary>
    /// Every ref prefix is exactly three lowercase letters and a trailing underscore, so a pasted
    /// value's type is readable from its first four characters.
    /// </summary>
    [Theory]
    [InlineData(JobRef.Prefix)]
    [InlineData(AlertRef.Prefix)]
    [InlineData(WorkerRef.Prefix)]
    public void Prefix_is_three_lowercase_letters_and_an_underscore(string prefix)
    {
        Assert.Equal(4, prefix.Length);
        Assert.All(prefix[..3], c => Assert.True(c is >= 'a' and <= 'z', $"'{prefix}' must be lowercase letters."));
        Assert.Equal('_', prefix[3]);
    }

    [Fact]
    public void Prefixes_are_distinct_per_entity_type()
    {
        Assert.Equal("job_", JobRef.Prefix);
        Assert.Equal("alr_", AlertRef.Prefix);
        Assert.Equal("wrk_", WorkerRef.Prefix);
    }

    [Theory]
    [InlineData(AlertRef.Prefix)]
    [InlineData(WorkerRef.Prefix)]
    public void Job_ref_rejects_other_prefixes(string foreignPrefix)
    {
        Assert.False(JobRef.TryParse(foreignPrefix + GoldenPayload, out _));
    }

    [Theory]
    [InlineData(JobRef.Prefix)]
    [InlineData(WorkerRef.Prefix)]
    public void Alert_ref_rejects_other_prefixes(string foreignPrefix)
    {
        Assert.False(AlertRef.TryParse(foreignPrefix + GoldenPayload, out _));
    }

    [Theory]
    [InlineData(JobRef.Prefix)]
    [InlineData(AlertRef.Prefix)]
    public void Worker_ref_rejects_other_prefixes(string foreignPrefix)
    {
        Assert.False(WorkerRef.TryParse(foreignPrefix + GoldenPayload, out _));
    }

    /// <summary>
    /// A rendered ref of one type never parses as another: the rendered form is the input, so this
    /// covers the paste-into-the-wrong-endpoint case end to end.
    /// </summary>
    [Fact]
    public void Rendered_refs_never_cross_parse()
    {
        var job = new JobRef(GoldenGuid).ToString();
        var alert = new AlertRef(GoldenGuid).ToString();
        var worker = new WorkerRef(GoldenGuid).ToString();

        Assert.False(AlertRef.TryParse(job, out _));
        Assert.False(WorkerRef.TryParse(job, out _));
        Assert.False(JobRef.TryParse(alert, out _));
        Assert.False(WorkerRef.TryParse(alert, out _));
        Assert.False(JobRef.TryParse(worker, out _));
        Assert.False(AlertRef.TryParse(worker, out _));
    }

    [Fact]
    public void Parse_throws_across_types()
    {
        Assert.Throws<FormatException>(() => AlertRef.Parse(new WorkerRef(GoldenGuid).ToString()));
        Assert.Throws<FormatException>(() => WorkerRef.Parse(new AlertRef(GoldenGuid).ToString()));
    }

    /// <summary>
    /// Documented policy: an all-zero ref stays VALID at the struct level for all three types. Rejecting an empty
    /// value is the job of the callers that mean it (job lookup, the operator facades), and this pins that the codec
    /// itself does not silently take on that role.
    /// </summary>
    [Fact]
    public void Guid_empty_round_trips_as_a_valid_ref()
    {
        Assert.True(JobRef.TryParse(JobRef.Prefix + EmptyPayload, out var job));
        Assert.Equal(Guid.Empty, job.Value);
        Assert.Equal(JobRef.Prefix + EmptyPayload, job.ToString());

        Assert.True(AlertRef.TryParse(AlertRef.Prefix + EmptyPayload, out var alert));
        Assert.Equal(Guid.Empty, alert.Value);
        Assert.Equal(AlertRef.Prefix + EmptyPayload, alert.ToString());

        Assert.True(WorkerRef.TryParse(WorkerRef.Prefix + EmptyPayload, out var worker));
        Assert.Equal(Guid.Empty, worker.Value);
        Assert.Equal(WorkerRef.Prefix + EmptyPayload, worker.ToString());
    }

    /// <summary>
    /// The minting side never produces the empty value: New() is a UUIDv7, so an all-zero ref can
    /// only arrive from outside.
    /// </summary>
    [Fact]
    public void New_never_mints_the_empty_ref()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.NotEqual(Guid.Empty, JobRef.New().Value);
            Assert.NotEqual(Guid.Empty, AlertRef.New().Value);
            Assert.NotEqual(Guid.Empty, WorkerRef.New().Value);
        }
    }

    [Fact]
    public void New_creates_unique_round_trippable_refs()
    {
        var alertA = AlertRef.New();
        var alertB = AlertRef.New();
        Assert.NotEqual(alertA, alertB);
        Assert.True(AlertRef.TryParse(alertA.ToString(), out var parsedAlert));
        Assert.Equal(alertA, parsedAlert);

        var workerA = WorkerRef.New();
        var workerB = WorkerRef.New();
        Assert.NotEqual(workerA, workerB);
        Assert.True(WorkerRef.TryParse(workerA.ToString(), out var parsedWorker));
        Assert.Equal(workerA, parsedWorker);
    }

    /// <summary>The hex digits are the Guid's own, so a UUIDv7 ref minted a millisecond later sorts after.</summary>
    [Fact]
    public void Uuidv7_refs_sort_by_creation_time()
    {
        var at = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var earlier = new JobRef(Guid.CreateVersion7(at)).ToString();
        var later = new JobRef(Guid.CreateVersion7(at.AddMilliseconds(1))).ToString();

        Assert.True(string.CompareOrdinal(earlier, later) < 0, $"{earlier} must sort before {later}.");
    }

    [Fact]
    public void Alert_ref_parses_uppercase_hex_and_emits_lowercase()
    {
        Assert.True(AlertRef.TryParse(AlertRef.Prefix + GoldenPayload.ToUpperInvariant(), out var alertRef));
        Assert.Equal(GoldenGuid, alertRef.Value);
        Assert.Equal(AlertRef.Prefix + GoldenPayload, alertRef.ToString());
    }

    [Fact]
    public void Worker_ref_parses_uppercase_hex_and_emits_lowercase()
    {
        Assert.True(WorkerRef.TryParse(WorkerRef.Prefix + GoldenPayload.ToUpperInvariant(), out var workerRef));
        Assert.Equal(GoldenGuid, workerRef.Value);
        Assert.Equal(WorkerRef.Prefix + GoldenPayload, workerRef.ToString());
    }

    [Fact]
    public void Crockford_form_never_parses()
    {
        Assert.False(AlertRef.TryParse("alr_2n1t201rmv87aae5j4csam8000", out _));
        Assert.False(WorkerRef.TryParse("wrk_2n1t201rmv87aae5j4csam8000", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("alr_")]
    [InlineData("01a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("alr_01a14f4e-954e-7bf6-8ac6-09f3fcad8e3d")]
    [InlineData("ALR_01a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("alr_01a14f4e954e7bf68ac609f3fcad8e3")]
    [InlineData("alr_01a14f4e954e7bf68ac609f3fcad8e3d0")]
    [InlineData("alr_01a14f4e954e7bf68ac609f3fcad8e3g")]
    [InlineData("alr-01a14f4e954e7bf68ac609f3fcad8e3d")]
    public void Alert_ref_rejects_malformed_refs(string? value)
    {
        Assert.False(AlertRef.TryParse(value, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrk_")]
    [InlineData("01a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("wrk_01a14f4e-954e-7bf6-8ac6-09f3fcad8e3d")]
    [InlineData("WRK_01a14f4e954e7bf68ac609f3fcad8e3d")]
    [InlineData("wrk_01a14f4e954e7bf68ac609f3fcad8e3")]
    [InlineData("wrk_01a14f4e954e7bf68ac609f3fcad8e3d0")]
    [InlineData("wrk_01a14f4e954e7bf68ac609f3fcad8e3g")]
    [InlineData("wrk-01a14f4e954e7bf68ac609f3fcad8e3d")]
    public void Worker_ref_rejects_malformed_refs(string? value)
    {
        Assert.False(WorkerRef.TryParse(value, out _));
    }

    [Fact]
    public void Round_trips_arbitrary_guids()
    {
        for (var i = 0; i < 100; i++)
        {
            var guid = Guid.NewGuid();

            Assert.Equal(AlertRef.Prefix + guid.ToString("N"), new AlertRef(guid).ToString());
            Assert.True(AlertRef.TryParse(new AlertRef(guid).ToString(), out var alertRef));
            Assert.Equal(guid, alertRef.Value);

            Assert.Equal(WorkerRef.Prefix + guid.ToString("N"), new WorkerRef(guid).ToString());
            Assert.True(WorkerRef.TryParse(new WorkerRef(guid).ToString(), out var workerRef));
            Assert.Equal(guid, workerRef.Value);
        }
    }

    [Fact]
    public void Alert_ref_json_round_trips_as_canonical_string()
    {
        var json = JsonSerializer.Serialize(new AlertRef(GoldenGuid));
        Assert.Equal($"\"{AlertRef.Prefix}{GoldenPayload}\"", json);
        Assert.Equal(new AlertRef(GoldenGuid), JsonSerializer.Deserialize<AlertRef>(json));
    }

    [Fact]
    public void Worker_ref_json_round_trips_as_canonical_string()
    {
        var json = JsonSerializer.Serialize(new WorkerRef(GoldenGuid));
        Assert.Equal($"\"{WorkerRef.Prefix}{GoldenPayload}\"", json);
        Assert.Equal(new WorkerRef(GoldenGuid), JsonSerializer.Deserialize<WorkerRef>(json));
    }

    [Fact]
    public void Json_rejects_malformed_and_cross_type_refs()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AlertRef>("\"alr_nope\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkerRef>("\"wrk_nope\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AlertRef>($"\"{WorkerRef.Prefix}{GoldenPayload}\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkerRef>($"\"{AlertRef.Prefix}{GoldenPayload}\""));
    }
}

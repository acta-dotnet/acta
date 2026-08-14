using Xunit;

namespace Acta.Tests.Wire;

/// <summary>
/// Unit tests for <see cref="JobRequestBuilder"/>. Covers happy-path construction, eager validation
/// of identifiers and lengths, last-write-wins tag dedup, the null-when-empty Tags convention, and
/// repeatability isolation between consecutive <see cref="JobRequestBuilder.Build"/> calls. Also
/// exercises the new <see cref="IdentifierSyntax.ValidateUserDottedKebab"/> helper and the
/// <see cref="JobPayload.Text"/> / <see cref="JobPayload.Bytes"/> factories independently of the
/// builder.
/// </summary>
public class JobRequestBuilderTests
{
    private const string Ns = "samples";
    private const string Name = "fetch-joke";

    // ----- happy-path equivalence -----

    [Fact]
    public void Create_with_namespace_and_name_builds_equivalent_to_record_constructor()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Build();
        var direct = new JobEnqueueRequest(Ns, Name);
        Assert.Equal(direct, built);
    }

    [Fact]
    public void Default_input_is_None_and_default_tags_is_null()
    {
        var request = JobRequestBuilder.Create(Ns, Name).Build();
        Assert.True(request.Input.IsNone);
        Assert.Null(request.Tags);
    }

    // ----- payload setters -----

    [Fact]
    public void Json_sets_payload_via_JobPayload_Json()
    {
        var value = new { Id = 7, Name = "tag" };
        var request = JobRequestBuilder.Create(Ns, Name).Json(value).Build();
        var expected = JobPayload.Json(value);
        Assert.Equal(expected.Format.Id, request.Input.Format.Id);
        Assert.True(request.Input.Data.Span.SequenceEqual(expected.Data.Span));
    }

    [Fact]
    public void Text_sets_payload_via_JobPayload_Text()
    {
        var request = JobRequestBuilder.Create(Ns, Name).Text("hello").Build();
        var expected = JobPayload.Text("hello");
        Assert.Equal(expected.Format.Id, request.Input.Format.Id);
        Assert.True(request.Input.Data.Span.SequenceEqual(expected.Data.Span));
    }

    [Fact]
    public void Bytes_sets_payload_via_JobPayload_Bytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var request = JobRequestBuilder.Create(Ns, Name).Bytes(bytes).Build();
        var expected = JobPayload.Bytes([1, 2, 3, 4]);
        Assert.Equal(expected.Format.Id, request.Input.Format.Id);
        Assert.True(request.Input.Data.Span.SequenceEqual(expected.Data.Span));
    }

    [Fact]
    public void Payload_accepts_None_default_and_arbitrary_payload()
    {
        var built1 = JobRequestBuilder.Create(Ns, Name).Payload(JobPayload.None).Build();
        Assert.True(built1.Input.IsNone);

        var built2 = JobRequestBuilder.Create(Ns, Name).Payload(default).Build();
        Assert.True(built2.Input.IsNone);

        var json = JobPayload.Json(new { x = 1 });
        var built3 = JobRequestBuilder.Create(Ns, Name).Payload(json).Build();
        Assert.False(built3.Input.IsNone);
    }

    [Fact]
    public void NoPayload_resets_input_to_None_after_a_prior_setter()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Json(new { x = 1 }).NoPayload().Build();
        Assert.True(built.Input.IsNone);
    }

    [Fact]
    public void Last_payload_setter_wins()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Json(new { x = 1 }).Text("hello").Build();
        var expected = JobPayload.Text("hello");
        Assert.Equal(expected.Format.Id, built.Input.Format.Id);
    }

    // ----- wire-field setters -----

    [Fact]
    public void DeduplicationKey_assigns_field()
    {
        var built = JobRequestBuilder.Create(Ns, Name).DeduplicationKey("invoice-2026-05-22").Build();
        Assert.Equal("invoice-2026-05-22", built.DeduplicationKey);
    }

    [Fact]
    public void CorrelationKey_assigns_field()
    {
        var built = JobRequestBuilder.Create(Ns, Name).CorrelationKey("trace-1").Build();
        Assert.Equal("trace-1", built.CorrelationKey);
    }

    [Fact]
    public void Priority_assigns_field()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Priority(JobPriorityCode.High).Build();
        Assert.Equal(JobPriorityCode.High, built.Priority);
    }

    [Fact]
    public void ParentId_assigns_field()
    {
        var built = JobRequestBuilder.Create(Ns, Name).ParentJobId(42).Build();
        Assert.Equal(42, built.ParentJobId);
    }

    [Fact]
    public void Default_ParentId_is_null()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Build();
        Assert.Null(built.ParentJobId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ParentId_rejects_non_positive(long parentJobId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JobRequestBuilder.Create(Ns, Name).ParentJobId(parentJobId));
    }

    [Fact]
    public void TenantKey_assigns_field()
    {
        var built = JobRequestBuilder.Create(Ns, Name).TenantKey("cust-abc").Build();
        Assert.Equal("cust-abc", built.TenantKey);
    }

    [Fact]
    public void Default_TenantKey_is_null()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Build();
        Assert.Null(built.TenantKey);
    }

    [Fact]
    public void Invalid_TenantKey_throws()
    {
        Assert.Throws<ArgumentNullException>(() => JobRequestBuilder.Create(Ns, Name).TenantKey(null!));
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(Ns, Name).TenantKey("  "));
        Assert.Throws<ArgumentException>(() =>
            JobRequestBuilder.Create(Ns, Name).TenantKey(new string('a', IdentifierSyntax.ExtendedMaxLength + 1))
        );
    }

    // ----- delayed enqueue -----

    [Fact]
    public void Default_NextRunAtUtc_is_null()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Build();
        Assert.Null(built.NextRunAtUtc);
    }

    [Fact]
    public void NextExecutionAt_normalizes_to_UTC()
    {
        var local = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.FromHours(2));
        var built = JobRequestBuilder.Create(Ns, Name).NextRunAt(local).Build();
        Assert.Equal(local.UtcDateTime, built.NextRunAtUtc);
        Assert.Equal(DateTimeKind.Utc, built.NextRunAtUtc!.Value.Kind);
    }

    [Fact]
    public void Delayed_sets_relative_delay_seconds_not_a_caller_instant()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Delayed(TimeSpan.FromHours(1)).Build();
        Assert.Equal(3600, built.DelaySeconds);
        Assert.Null(built.NextRunAtUtc);
    }

    [Fact]
    public void Delayed_rounds_sub_second_up()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Delayed(TimeSpan.FromMilliseconds(1)).Build();
        Assert.Equal(1, built.DelaySeconds);
    }

    [Fact]
    public void Delayed_zero_is_allowed()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Delayed(TimeSpan.Zero).Build();
        Assert.Equal(0, built.DelaySeconds);
        Assert.Null(built.NextRunAtUtc);
    }

    [Fact]
    public void Delayed_negative_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JobRequestBuilder.Create(Ns, Name).Delayed(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void NextExecutionAt_and_Delayed_are_last_call_wins_and_clear_each_other()
    {
        var at = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var absoluteWins = JobRequestBuilder.Create(Ns, Name).Delayed(TimeSpan.FromHours(1)).NextRunAt(at).Build();
        Assert.Equal(at.UtcDateTime, absoluteWins.NextRunAtUtc);
        Assert.Null(absoluteWins.DelaySeconds);

        var relativeWins = JobRequestBuilder.Create(Ns, Name).NextRunAt(at).Delayed(TimeSpan.FromHours(1)).Build();
        Assert.Equal(3600, relativeWins.DelaySeconds);
        Assert.Null(relativeWins.NextRunAtUtc);
    }

    // ----- tag setters -----

    [Fact]
    public void Tag_appends_to_tags_list()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Tag("env", "prod").Build();
        Assert.NotNull(built.Tags);
        var tag = Assert.Single(built.Tags!);
        Assert.Equal("env", tag.Name);
        Assert.Equal("prod", tag.Value);
    }

    [Fact]
    public void Tag_presence_only_accepts_null_value()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Tag("important").Build();
        var tag = Assert.Single(built.Tags!);
        Assert.Equal("important", tag.Name);
        Assert.Null(tag.Value);
    }

    [Fact]
    public void Tag_last_write_wins_dedupes_by_name()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Tag("env", "prod").Tag("env", "test").Build();
        var tag = Assert.Single(built.Tags!);
        Assert.Equal("env", tag.Name);
        Assert.Equal("test", tag.Value);
    }

    [Fact]
    public void Tags_bulk_appends_and_dedupes_with_existing()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Tag("a", "1").Tags(new TagInput("a", "2"), new TagInput("b", "3")).Build();
        Assert.NotNull(built.Tags);
        Assert.Equal(2, built.Tags!.Count);
        Assert.Equal(new TagInput("a", "2"), built.Tags[0]);
        Assert.Equal(new TagInput("b", "3"), built.Tags[1]);
    }

    [Theory]
    [InlineData("env")]
    [InlineData("env.prod")]
    [InlineData("com.acme.tier")]
    [InlineData("com.acta.priority")]
    public void Tag_accepts_dotted_kebab_name(string name)
    {
        var built = JobRequestBuilder.Create(Ns, Name).Tag(name, "x").Build();
        Assert.Equal(name, built.Tags![0].Name);
    }

    [Theory]
    [InlineData("env_prod")] // underscore segment
    [InlineData("env..prod")] // empty middle segment
    [InlineData(".env")] // leading dot
    [InlineData("env.")] // trailing dot
    [InlineData("-env")] // leading hyphen
    [InlineData("env-")] // trailing hyphen
    public void Tag_rejects_invalid_name_shapes(string name)
    {
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(Ns, Name).Tag(name, "x"));
    }

    [Fact]
    public void Tag_rejects_name_over_length_limit()
    {
        var tooLong = new string('a', IdentifierSyntax.ExtendedMaxLength + 1);
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(Ns, Name).Tag(tooLong, "x"));
    }

    [Theory]
    [InlineData("sys.batch")]
    [InlineData("sys.internal")]
    [InlineData("sys.something")]
    public void Tag_rejects_system_prefix(string name)
    {
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(Ns, Name).Tag(name, "x"));
    }

    [Fact]
    public void Tag_value_at_length_limit_is_accepted()
    {
        var atLimit = new string('a', IdentifierSyntax.ExtendedMaxLength);
        var built = JobRequestBuilder.Create(Ns, Name).Tag("name", atLimit).Build();
        Assert.Equal(atLimit, built.Tags![0].Value);
    }

    [Fact]
    public void Tag_value_over_length_limit_throws()
    {
        var tooLong = new string('a', IdentifierSyntax.ExtendedMaxLength + 1);
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(Ns, Name).Tag("name", tooLong));
    }

    [Fact]
    public void Tags_rejects_null_array()
    {
        Assert.Throws<ArgumentNullException>(() => JobRequestBuilder.Create(Ns, Name).Tags(null!));
    }

    [Fact]
    public void Tags_rejects_null_element()
    {
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(Ns, Name).Tags(new TagInput("a", "1"), null!));
    }

    // ----- Batch() convenience overload -----

    [Fact]
    public void Batch_without_id_adds_presence_only_tag()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Batch().Build();
        var tag = Assert.Single(built.Tags!);
        Assert.Equal("batch", tag.Name);
        Assert.Null(tag.Value);
    }

    [Fact]
    public void Batch_with_id_adds_keyed_tag()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Batch("q1-2026").Build();
        var tag = Assert.Single(built.Tags!);
        Assert.Equal("batch", tag.Name);
        Assert.Equal("q1-2026", tag.Value);
    }

    [Fact]
    public void Batch_and_Tag_converge_on_same_slot()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Batch("first").Tag("batch", "second").Build();
        var tag = Assert.Single(built.Tags!);
        Assert.Equal("batch", tag.Name);
        Assert.Equal("second", tag.Value);
    }

    [Fact]
    public void Batch_last_write_wins()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Batch("a").Batch("b").Build();
        var tag = Assert.Single(built.Tags!);
        Assert.Equal("b", tag.Value);
    }

    [Fact]
    public void Batch_rejects_value_over_length_limit()
    {
        var tooLong = new string('a', IdentifierSyntax.ExtendedMaxLength + 1);
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(Ns, Name).Batch(tooLong));
    }

    // ----- deduplication convenience -----

    [Fact]
    public void Deduplicate_composes_DeduplicationKey_from_jobName_and_businessKey()
    {
        var built = JobRequestBuilder.Create(Ns, Name).Deduplicate("user-123").Build();
        Assert.Equal(DeduplicationKey.ForDefinition(Name, "user-123"), built.DeduplicationKey);
    }

    [Fact]
    public void Deduplicate_and_DeduplicationKey_are_last_call_wins()
    {
        var built1 = JobRequestBuilder.Create(Ns, Name).Deduplicate("a").DeduplicationKey("explicit-key").Build();
        Assert.Equal("explicit-key", built1.DeduplicationKey);

        var built2 = JobRequestBuilder.Create(Ns, Name).DeduplicationKey("explicit-key").Deduplicate("a").Build();
        Assert.Equal(DeduplicationKey.ForDefinition(Name, "a"), built2.DeduplicationKey);
    }

    // ----- identifier validation -----

    [Theory]
    [InlineData("")] // empty
    [InlineData("bad namespace")]
    public void Invalid_namespace_throws_at_Create(string ns)
    {
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(ns, Name));
    }

    [Fact]
    public void Namespace_over_length_limit_throws()
    {
        var tooLong = new string('a', IdentifierSyntax.DefaultMaxLength + 1);
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(tooLong, Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sys.fetch")]
    public void Invalid_jobName_throws_at_Create(string name)
    {
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(Ns, name));
    }

    [Fact]
    public void JobName_over_extended_length_limit_throws()
    {
        var tooLong = new string('a', IdentifierSyntax.ExtendedMaxLength + 1);
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(Ns, tooLong));
    }

    [Fact]
    public void Invalid_DeduplicationKey_throws()
    {
        Assert.Throws<ArgumentNullException>(() => JobRequestBuilder.Create(Ns, Name).DeduplicationKey(null!));
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(Ns, Name).DeduplicationKey("  "));
        Assert.Throws<ArgumentException>(() =>
            JobRequestBuilder.Create(Ns, Name).DeduplicationKey(new string('a', IdentifierSyntax.ExtendedMaxLength + 1))
        );
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(Ns, Name).DeduplicationKey("sys.reserved"));
    }

    [Fact]
    public void Invalid_CorrelationKey_throws()
    {
        Assert.Throws<ArgumentNullException>(() => JobRequestBuilder.Create(Ns, Name).CorrelationKey(null!));
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(Ns, Name).CorrelationKey("  "));
        Assert.Throws<ArgumentException>(() =>
            JobRequestBuilder.Create(Ns, Name).CorrelationKey(new string('a', IdentifierSyntax.ExtendedMaxLength + 1))
        );
    }

    // ----- name casing -----

    [Fact]
    public void Builder_rejects_mixed_case_job_name()
    {
        Assert.Throws<ArgumentException>(() => JobRequestBuilder.Create(Ns, "Add-Numbers"));
    }

    // ----- Build() repeatability -----

    [Fact]
    public void Build_is_repeatable_and_isolates_subsequent_mutation()
    {
        var builder = JobRequestBuilder.Create(Ns, Name);
        var snapshotA = builder.Build();
        Assert.Null(snapshotA.Tags);

        builder.Tag("env", "prod");
        var snapshotB = builder.Build();

        // Prior result is unchanged by the new tag.
        Assert.Null(snapshotA.Tags);
        // New result reflects the new tag.
        Assert.NotNull(snapshotB.Tags);
        Assert.Equal("prod", snapshotB.Tags![0].Value);
    }

    [Fact]
    public void Build_returns_array_snapshot_not_internal_container()
    {
        var builder = JobRequestBuilder.Create(Ns, Name).Tag("env", "prod");
        var firstBuild = builder.Build();

        builder.Tag("region", "eu-west-1");
        var secondBuild = builder.Build();

        // Prior Tags reference is not extended.
        Assert.Single(firstBuild.Tags!);
        Assert.Equal(2, secondBuild.Tags!.Count);
    }

    // ----- direct tests for new IdentifierSyntax helper -----

    public class ValidateUserDottedKebabTests
    {
        [Theory]
        [InlineData("env")]
        [InlineData("env.prod")]
        [InlineData("com.acme.tier")]
        [InlineData("a-b.c-d.e-f")]
        public void Accepts_valid_dotted_kebab(string value)
        {
            IdentifierSyntax.ValidateUserDottedKebab(value, nameof(value), IdentifierSyntax.ExtendedMaxLength);
        }

        [Theory]
        [InlineData("sys.env")]
        [InlineData("sys.")]
        public void Rejects_system_prefix(string value)
        {
            Assert.Throws<ArgumentException>(() =>
                IdentifierSyntax.ValidateUserDottedKebab(value, nameof(value), IdentifierSyntax.ExtendedMaxLength)
            );
        }

        [Theory]
        [InlineData("")]
        [InlineData("Env")]
        [InlineData("env_prod")]
        [InlineData(".env")]
        [InlineData("env.")]
        [InlineData("env..prod")]
        public void Rejects_invalid_shape(string value)
        {
            Assert.Throws<ArgumentException>(() =>
                IdentifierSyntax.ValidateUserDottedKebab(value, nameof(value), IdentifierSyntax.ExtendedMaxLength)
            );
        }
    }

    // ----- direct tests for new JobPayload factories -----

    public class JobPayloadConvenienceFactoryTests
    {
        [Fact]
        public void Text_roundtrip_via_serializer()
        {
            var payload = JobPayload.Text("hello world");
            Assert.Equal(JobPayloadFormat.Text.Id, payload.Format.Id);
            Assert.False(payload.IsNone);
            Assert.False(payload.IsEmpty);
        }

        [Fact]
        public void Text_empty_string_produces_zero_byte_Text_payload()
        {
            var payload = JobPayload.Text(string.Empty);
            Assert.Equal(JobPayloadFormat.Text.Id, payload.Format.Id);
            Assert.False(payload.IsNone);
            Assert.True(payload.IsEmpty);
        }

        [Fact]
        public void Bytes_wraps_byte_array_as_Bytes_format()
        {
            var bytes = new byte[] { 0x01, 0x02, 0x03 };
            var payload = JobPayload.Bytes(bytes);
            Assert.Equal(JobPayloadFormat.Bytes.Id, payload.Format.Id);
            Assert.False(payload.IsNone);
            Assert.True(payload.Data.Span.SequenceEqual(bytes));
        }

        [Fact]
        public void Bytes_empty_array_produces_zero_byte_Bytes_payload()
        {
            var payload = JobPayload.Bytes([]);
            Assert.Equal(JobPayloadFormat.Bytes.Id, payload.Format.Id);
            Assert.True(payload.IsEmpty);
        }
    }
}

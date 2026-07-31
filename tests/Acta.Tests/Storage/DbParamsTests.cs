using Xunit;

namespace Acta.Tests.Storage;

/// <summary>
/// Unit tests for <see cref="DbParams"/>. The helper must carry generated parameter metadata into
/// <see cref="DbParameterSpec"/> without operation code spelling parameter names or sizes.
/// </summary>
public class DbParamsTests
{
    [Fact]
    public void For_column_uses_generated_parameter_name()
    {
        var p = DbParams.For(ActaSchema.JobRuntime.StatusCode, JobStatusCode.Ready);

        Assert.Equal("p_status_code", p.Name);
    }

    [Fact]
    public void For_column_inherits_size_from_generated_metadata()
    {
        var p = DbParams.For(ActaSchema.JobEvent.ReasonMessage, "boom");

        Assert.Equal(DbKind.UnicodeString, p.Kind);
        Assert.Equal(512, p.Size);
    }

    [Fact]
    public void For_coded_column_uses_the_enums_physical_width()
    {
        var byteBacked = DbParams.For(ActaSchema.JobRuntime.StatusCode, JobStatusCode.Executing);
        var priority = DbParams.For(ActaSchema.JobRuntime.PriorityCode, JobPriorityCode.Normal);

        Assert.Equal(DbKind.Byte, byteBacked.Kind);
        Assert.Equal(DbKind.Byte, priority.Kind);
    }

    [Fact]
    public void For_value_uses_synthetic_metadata()
    {
        var p = DbParams.For(ActaSchema.Sql.ClaimLimit, 10);

        Assert.Equal("p_claim_limit", p.Name);
        Assert.Equal(DbKind.Int32, p.Kind);
        Assert.Equal(10, p.Value);
    }

    [Fact]
    public void For_column_accepts_null_value()
    {
        var p = DbParams.For(ActaSchema.Job.DeduplicationKey, null);

        Assert.Null(p.Value);
        Assert.Equal(128, p.Size);
    }

    [Fact]
    public void Validate_int_passes()
    {
        var p = new DbParameterSpec("p_x", 1, DbKind.Int32);
        DbParams.Validate(p);
    }

    [Fact]
    public void Validate_string_without_size_throws()
    {
        var p = new DbParameterSpec("p_x", "v", DbKind.AsciiString);
        var ex = Assert.Throws<InvalidOperationException>(() => DbParams.Validate(p));
        Assert.Contains("requires explicit Size", ex.Message);
    }

    // Coded values arrive as their CLR enum and coerce through the column's physical integer Kind.
    [Fact]
    public void Coerce_byte_backed_enum_returns_byte()
    {
        var p = new DbParameterSpec("p_x", JobStatusCode.Executing, DbKind.Byte);
        Assert.IsType<byte>(DbParams.Coerce(p));
    }

    [Fact]
    public void Coerce_short_backed_enum_returns_short()
    {
        var p = new DbParameterSpec("p_x", JobPriorityCode.Normal, DbKind.Int16);
        Assert.IsType<short>(DbParams.Coerce(p));
    }

    [Fact]
    public void Coerce_int_returns_int()
    {
        var p = new DbParameterSpec("p_x", 70_000, DbKind.Int32);
        Assert.IsType<int>(DbParams.Coerce(p));
    }

    [Fact]
    public void Coerce_utc_instant_keeps_utc_datetime()
    {
        var utc = new DateTime(2026, 7, 8, 8, 0, 0, DateTimeKind.Utc);
        var coerced = Assert.IsType<DateTime>(DbParams.Coerce(new DbParameterSpec("p_x", utc, DbKind.UtcInstant)));

        Assert.Equal(utc, coerced);
        Assert.Equal(DateTimeKind.Utc, coerced.Kind);
    }

    [Fact]
    public void Coerce_utc_instant_converts_local_datetime()
    {
        var local = new DateTime(2026, 7, 8, 10, 0, 0, DateTimeKind.Local);
        var coerced = Assert.IsType<DateTime>(DbParams.Coerce(new DbParameterSpec("p_x", local, DbKind.UtcInstant)));

        Assert.Equal(local.ToUniversalTime(), coerced);
        Assert.Equal(DateTimeKind.Utc, coerced.Kind);
    }

    [Fact]
    public void Coerce_utc_instant_tags_unspecified_datetime_as_utc()
    {
        var unspecified = new DateTime(2026, 7, 8, 8, 0, 0, DateTimeKind.Unspecified);
        var coerced = Assert.IsType<DateTime>(DbParams.Coerce(new DbParameterSpec("p_x", unspecified, DbKind.UtcInstant)));

        Assert.Equal(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), coerced);
        Assert.Equal(DateTimeKind.Utc, coerced.Kind);
    }
}

using Xunit;

namespace Acta.Tests.Storage;

public sealed class DbScalarCoercionTests
{
    [Fact]
    public void Required_scalars_coerce_supported_types()
    {
        var guid = Guid.NewGuid();

        Assert.Equal((byte)7, DbScalarCoercion.Resolve<byte>("op")(7L));
        Assert.Equal((short)7, DbScalarCoercion.Resolve<short>("op")(7L));
        Assert.Equal(7, DbScalarCoercion.Resolve<int>("op")(7L));
        Assert.Equal(7L, DbScalarCoercion.Resolve<long>("op")(7));
        Assert.True(DbScalarCoercion.Resolve<bool>("op")(1));
        Assert.Equal("7", DbScalarCoercion.Resolve<string>("op")(7));
        Assert.Equal(guid, DbScalarCoercion.Resolve<Guid>("op")(guid.ToString()));

        var instant = DbScalarCoercion.Resolve<DateTime>("op")(0L);
        Assert.Equal(DateTimeKind.Utc, instant.Kind);
        Assert.Equal(DateTime.UnixEpoch, instant);
    }

    [Fact]
    public void Nullable_scalars_return_null_for_empty_values()
    {
        Assert.Null(DbScalarCoercion.Resolve<byte?>("op")(null));
        Assert.Null(DbScalarCoercion.Resolve<short?>("op")(DBNull.Value));
        Assert.Null(DbScalarCoercion.Resolve<int?>("op")(null));
        Assert.Null(DbScalarCoercion.Resolve<long?>("op")(null));
        Assert.Null(DbScalarCoercion.Resolve<bool?>("op")(null));
        Assert.Null(DbScalarCoercion.Resolve<DateTime?>("op")(null));
        Assert.Null(DbScalarCoercion.Resolve<Guid?>("op")(null));
    }

    [Fact]
    public void Required_scalar_empty_value_uses_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DbScalarCoercion.Resolve<int>("StartExecution", "missing action")(null));

        Assert.Equal("missing action", ex.Message);
    }

    [Fact]
    public void Unsupported_scalar_type_fails_clearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DbScalarCoercion.Resolve<decimal>("op"));

        Assert.Contains("Acta scalar result type", ex.Message);
        Assert.Contains("decimal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

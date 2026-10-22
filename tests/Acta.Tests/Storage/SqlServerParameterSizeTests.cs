using Acta.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Acta.Tests.Storage;

/// <summary>
/// <see cref="SqlServerDialect.CreateParameter"/> binds a binary payload with no declared size at max, so every
/// call of a statement declares the same <c>varbinary(max)</c> whatever the payload's length.
/// </summary>
public class SqlServerParameterSizeTests
{
    private static readonly SqlServerDialect Dialect = new();

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(9000)]
    public void Unsized_binary_payload_binds_at_max(int length)
    {
        var parameter = Bind(new DbParameterSpec("p_detail", new byte[length], DbKind.BinaryPayload));

        Assert.Equal(System.Data.SqlDbType.VarBinary, parameter.SqlDbType);
        Assert.Equal(-1, parameter.Size);
    }

    [Fact]
    public void Null_binary_payload_binds_at_max()
    {
        Assert.Equal(-1, Bind(new DbParameterSpec("p_detail", null, DbKind.BinaryPayload)).Size);
    }

    [Fact]
    public void Declared_size_is_kept()
    {
        Assert.Equal(16, Bind(new DbParameterSpec("p_detail", new byte[4], DbKind.BinaryPayload, Size: 16)).Size);
        Assert.Equal(256, Bind(new DbParameterSpec("p_lock_key", "k", DbKind.AsciiString, Size: 256)).Size);
    }

    private static SqlParameter Bind(DbParameterSpec spec) => (SqlParameter)Dialect.CreateParameter(spec);
}

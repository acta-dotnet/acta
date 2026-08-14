using Acta.Tests.Conformance.Sql;
using Xunit;

namespace Acta.Tests.Conformance;

public sealed class ConstantCatalogTests
{
    [Theory]
    [InlineData("JobStatusCode.Ready", 10)]
    [InlineData("TagMutationAction.NotFound", 2)]
    [InlineData("StartExecutionAction.LeaseExpired", 5)]
    [InlineData("JobPayloadFormat.Json", 1)]
    public void Resolves_every_supported_constant_shape(string symbol, int expected)
    {
        Assert.True(ConstantCatalog.CodeConstants.TryGetValue(symbol, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Rejects_an_unknown_enum_member()
    {
        Assert.False(ConstantCatalog.IsKnownCode("StartExecutionAction.NoSuchThing"));
    }

    [Fact]
    public void Constant_names_are_case_sensitive()
    {
        Assert.False(ConstantCatalog.IsKnownCode("startExecutionAction.LeaseExpired"));
    }

    [Fact]
    public void Rejects_code_kind_syntax_in_sql_constant_comments()
    {
        Assert.DoesNotMatch(ConstantCatalog.VerifiableConstantName, "job-status:ready");
    }
}

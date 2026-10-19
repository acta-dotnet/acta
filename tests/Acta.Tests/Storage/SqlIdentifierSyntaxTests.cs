using Xunit;

namespace Acta.Tests.Storage;

/// <summary>
/// The bare-identifier validator that gates every <c>{{schema}}</c> substitution site. An unsafe schema name is an
/// SQL-injection vector, so these tests anchor the predicate against a later widening of the shape.
/// </summary>
public class SqlIdentifierSyntaxBareIdentifierTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("acta")]
    [InlineData("acta_test")]
    [InlineData("users")]
    [InlineData("t_abc123")]
    [InlineData("signup_v2")]
    [InlineData("a_b_c_d_e")]
    public void Accepts_ValidBareIdentifier(string value)
    {
        SqlIdentifierSyntax.ValidateBareIdentifier(value, nameof(value));
        Assert.True(SqlIdentifierSyntax.IsBareIdentifier(value));
    }

    [Theory]
    [InlineData("")] // empty
    [InlineData("Acta")] // uppercase
    [InlineData("acta-test")] // hyphen
    [InlineData("_acta")] // leading underscore
    [InlineData("1acta")] // leading digit
    [InlineData("acta!")] // special char
    [InlineData("acta test")] // space
    [InlineData("acta.test")] // dot
    [InlineData("ACTA")] // all caps
    [InlineData("'; DROP TABLE x; --")] // SQL-injection shape
    public void Rejects_InvalidBareIdentifier(string value)
    {
        Assert.False(SqlIdentifierSyntax.IsBareIdentifier(value));
        Assert.Throws<ArgumentException>(() => SqlIdentifierSyntax.ValidateBareIdentifier(value, nameof(value)));
    }

    [Fact]
    public void Rejects_OversizedInput()
    {
        var oversize = new string('a', SqlIdentifierSyntax.BareIdentifierMaxLength + 1);
        Assert.Throws<ArgumentException>(() => SqlIdentifierSyntax.ValidateBareIdentifier(oversize, nameof(oversize)));
    }

    [Fact]
    public void Accepts_AtBoundaryLength()
    {
        var boundary = new string('a', SqlIdentifierSyntax.BareIdentifierMaxLength);
        SqlIdentifierSyntax.ValidateBareIdentifier(boundary, nameof(boundary));
    }
}

/// <summary>
/// The database-name validator that gates the operator-supplied database name interpolated into the dev-convenience
/// CREATE DATABASE DDL. It is wider than a bare identifier (hyphens permitted, so the in-use 'acta-pg' / 'acta-mssql'
/// names pass) but still rejects the SQL delimiter characters that would let a name break out of the bracket /
/// literal contexts.
/// </summary>
public class SqlIdentifierSyntaxDatabaseNameTests
{
    [Theory]
    [InlineData("acta")]
    [InlineData("acta-pg")]
    [InlineData("acta-mssql")]
    [InlineData("acta_test")]
    [InlineData("mydb01")]
    [InlineData("a")]
    public void Accepts_ValidDatabaseName(string value)
    {
        SqlIdentifierSyntax.ValidateDatabaseName(value, nameof(value));
    }

    [Theory]
    [InlineData("")] // empty
    [InlineData("Acta")] // uppercase
    [InlineData("-acta")] // leading hyphen
    [InlineData("_acta")] // leading underscore
    [InlineData("1acta")] // leading digit
    [InlineData("acta db")] // space
    [InlineData("acta.db")] // dot
    [InlineData("my]db")] // SQL Server identifier delimiter
    [InlineData("bob's-db")] // string-literal delimiter
    [InlineData("my\"db")] // Postgres identifier delimiter
    [InlineData("acta]; DROP DATABASE x; --")] // injection shape
    public void Rejects_InvalidDatabaseName(string value)
    {
        Assert.Throws<ArgumentException>(() => SqlIdentifierSyntax.ValidateDatabaseName(value, nameof(value)));
    }

    [Fact]
    public void Rejects_OversizedInput()
    {
        var oversize = new string('a', SqlIdentifierSyntax.BareIdentifierMaxLength + 1);
        Assert.Throws<ArgumentException>(() => SqlIdentifierSyntax.ValidateDatabaseName(oversize, nameof(oversize)));
    }
}

using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// Unit tests for the bare-identifier validator that gates every <c>{{schema}}</c> substitution
/// site. The reviewer flagged unsafe schema names as a real SQL-injection vector - these tests
/// anchor the predicate so future expansions to the syntax don't accidentally widen the surface.
/// </summary>
public class IdentifierSyntaxBareIdentifierTests
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
        IdentifierSyntax.ValidateBareIdentifier(value, nameof(value));
        Assert.True(IdentifierSyntax.IsBareIdentifier(value));
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
        Assert.False(IdentifierSyntax.IsBareIdentifier(value));
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.ValidateBareIdentifier(value, nameof(value)));
    }

    [Fact]
    public void Rejects_OversizedInput()
    {
        var oversize = new string('a', IdentifierSyntax.BareIdentifierMaxLength + 1);
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.ValidateBareIdentifier(oversize, nameof(oversize)));
    }

    [Fact]
    public void Accepts_AtBoundaryLength()
    {
        var boundary = new string('a', IdentifierSyntax.BareIdentifierMaxLength);
        IdentifierSyntax.ValidateBareIdentifier(boundary, nameof(boundary));
    }

    [Fact]
    public void CustomMaxLength_Enforced()
    {
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.ValidateBareIdentifier("abcde", "x", maxLength: 4));
        IdentifierSyntax.ValidateBareIdentifier("abcd", "x", maxLength: 4);
    }
}

/// <summary>
/// Unit tests for the database-name validator that gates the operator-supplied database name
/// interpolated into the dev-convenience CREATE DATABASE DDL. It is wider than a bare identifier
/// (hyphens permitted, so the in-use 'acta-pg' / 'acta-mssql' names pass) but still rejects the
/// SQL delimiter characters that would let a name break out of the bracket / literal contexts.
/// </summary>
public class IdentifierSyntaxDatabaseNameTests
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
        IdentifierSyntax.ValidateDatabaseName(value, nameof(value));
        Assert.True(IdentifierSyntax.IsDatabaseName(value));
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
        Assert.False(IdentifierSyntax.IsDatabaseName(value));
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.ValidateDatabaseName(value, nameof(value)));
    }

    [Fact]
    public void Rejects_OversizedInput()
    {
        var oversize = new string('a', IdentifierSyntax.BareIdentifierMaxLength + 1);
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.ValidateDatabaseName(oversize, nameof(oversize)));
    }
}

/// <summary>
/// Unit tests for the canonicalization helpers that validate Acta names and normalize Acta keys.
/// </summary>
public class IdentifierSyntaxCanonicalizeTests
{
    [Fact]
    public void NormalizeLowerInvariant_folds_ascii_to_lowercase()
    {
        Assert.Equal("add-numbers", IdentifierSyntax.NormalizeLowerInvariant("Add-Numbers"));
        Assert.Equal("marko", IdentifierSyntax.NormalizeLowerInvariant("MARKO"));
    }

    [Fact]
    public void NormalizeLowerInvariant_null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => IdentifierSyntax.NormalizeLowerInvariant(null!));
    }

    [Fact]
    public void CanonicalizeUserKebab_rejects_mixed_case()
    {
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.CanonicalizeUserKebab("Add-Numbers", "name"));
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.CanonicalizeUserKebab("customerId", "name"));
    }

    [Fact]
    public void CanonicalizeUserKebab_rejects_structural_violations()
    {
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.CanonicalizeUserKebab("Add_Numbers", "name"));
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.CanonicalizeUserDottedKebab("Sys.Recovery", "name"));
    }

    [Fact]
    public void NormalizeKey_folds_and_validates()
    {
        Assert.Equal("invoice-7", IdentifierSyntax.NormalizeKey("Invoice-7", "key"));
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.NormalizeKey("   ", "key"));
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.NormalizeKey("café", "key"));
    }

    [Fact]
    public void External_tokens_and_display_values_preserve_case_and_unicode()
    {
        IdentifierSyntax.ValidateExternalToken("Trace-A", "token");
        IdentifierSyntax.ValidateDisplayValue("EU-West résumé", "value");
    }
}

/// <summary>
/// Unit tests for the bare <c>sys</c> reservation: registration validators (<see
/// cref="IdentifierSyntax.ValidateUserKebab"/> / <see cref="IdentifierSyntax.CanonicalizeUserKebab"/>)
/// reject the exact name <c>sys</c> (collides with the seeded system namespace row) without
/// over-reaching onto names that merely start with "sys".
/// </summary>
public class IdentifierSyntaxReservedSystemNameTests
{
    [Fact]
    public void IsReservedSystemName_true_for_bare_sys_and_sys_dot_prefixed()
    {
        Assert.True(IdentifierSyntax.IsReservedSystemName("sys"));
        Assert.True(IdentifierSyntax.IsReservedSystemName("sys.retention"));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("sysx")]
    [InlineData("sys-a")]
    public void IsReservedSystemName_false_for_lookalikes(string value)
    {
        Assert.False(IdentifierSyntax.IsReservedSystemName(value));
    }

    [Fact]
    public void ValidateUserKebab_rejects_bare_sys()
    {
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.ValidateUserKebab("sys", "name"));
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.CanonicalizeUserKebab("sys", "name"));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("sysx")]
    [InlineData("sys-a")]
    public void ValidateUserKebab_accepts_names_that_merely_start_with_sys(string value)
    {
        IdentifierSyntax.ValidateUserKebab(value, "name");
        Assert.Equal(value, IdentifierSyntax.CanonicalizeUserKebab(value, "name"));
    }

    [Fact]
    public void ValidateUserDottedKebab_still_rejects_sys_dot_prefixed_names()
    {
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.ValidateUserDottedKebab("sys.retention", "name"));
        Assert.Throws<ArgumentException>(() => IdentifierSyntax.CanonicalizeUserDottedKebab("sys.retention", "name"));
    }
}

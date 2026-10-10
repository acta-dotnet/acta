using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// Keys keep their case and hold to printable ASCII without whitespace; display values are stored as given.
/// </summary>
public class KeySyntaxTests
{
    [Theory]
    [InlineData("Invoice-7", "Invoice-7")]
    [InlineData(" ORDER#42 ", "ORDER#42")]
    [InlineData("auth0|123", "auth0|123")]
    [InlineData("sys.retention", "sys.retention")]
    [InlineData("!~", "!~")]
    public void Canonicalize_trims_and_keeps_the_case(string value, string expected) =>
        Assert.Equal(expected, KeySyntax.Canonicalize(value, "key"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("tab\there")]
    [InlineData("bell\u0007")]
    [InlineData("café")]
    [InlineData(" ")]
    public void Canonicalize_rejects_whitespace_control_and_non_ascii(string value) =>
        Assert.Throws<ArgumentException>(() => KeySyntax.Canonicalize(value, "key"));

    [Fact]
    public void Canonicalize_caps_the_trimmed_length()
    {
        Assert.Equal(128, KeySyntax.Canonicalize($" {new string('K', 128)} ", "key").Length);
        Assert.Throws<ArgumentException>(() => KeySyntax.Canonicalize(new string('K', 129), "key"));
        Assert.Throws<ArgumentException>(() => KeySyntax.Canonicalize(new string('K', 65), "key", NameSyntax.DefaultMaxLength));
    }

    [Fact]
    public void Display_values_preserve_case_and_unicode() => TextLimits.ValidateDisplayValue("EU-West résumé", "value");
}

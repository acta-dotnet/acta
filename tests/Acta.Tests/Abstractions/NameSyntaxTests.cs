using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// Names fold: <see cref="NameSyntax.Canonicalize"/> trims and lowercases, then holds the result to dotted kebab.
/// </summary>
public class NameSyntaxCanonicalizeTests
{
    [Theory]
    [InlineData(" Billing.Send-Invoice ", "billing.send-invoice")]
    [InlineData("Add-Numbers", "add-numbers")]
    [InlineData("ORDER-1", "order-1")]
    [InlineData("env.prod", "env.prod")]
    public void Canonicalize_trims_and_folds_to_lowercase(string value, string expected)
    {
        Assert.Equal(expected, NameSyntax.Canonicalize(value, "name"));
        Assert.Equal(expected, NameSyntax.Canonicalize(value, "name", reserveSystem: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("send_invoice")]
    [InlineData("customerId 7")]
    [InlineData("1-billing")]
    [InlineData("billing-")]
    [InlineData(".billing")]
    [InlineData("billing.")]
    [InlineData("billing..send")]
    [InlineData("café")]
    public void Canonicalize_rejects_anything_but_dotted_kebab(string value) =>
        Assert.Throws<ArgumentException>(() => NameSyntax.Canonicalize(value, "name"));

    [Fact]
    public void Canonicalize_caps_the_folded_length()
    {
        Assert.Equal(new string('a', 64), NameSyntax.Canonicalize($" {new string('A', 64)} ", "name"));
        Assert.Throws<ArgumentException>(() => NameSyntax.Canonicalize(new string('a', 65), "name"));
        Assert.Equal(128, NameSyntax.Canonicalize(new string('a', 128), "name", NameSyntax.ExtendedMaxLength).Length);
    }

    [Fact]
    public void Canonicalize_rejects_null() => Assert.Throws<ArgumentNullException>(() => NameSyntax.Canonicalize(null!, "name"));
}

/// <summary>
/// The <c>sys</c> reservation under <c>reserveSystem</c>: the exact name <c>sys</c> (the seeded system namespace) and
/// anything under <c>sys.</c>, in any case, without over-reaching onto names that merely start with "sys". Without
/// the reservation, lookups still accept <c>sys</c>.
/// </summary>
public class NameSyntaxReservedSystemNameTests
{
    [Theory]
    [InlineData("sys")]
    [InlineData("SYS")]
    [InlineData("sys.retention")]
    [InlineData("SYS.x")]
    public void Reservation_rejects_sys_and_the_sys_prefix(string value) =>
        Assert.Throws<ArgumentException>(() => NameSyntax.Canonicalize(value, "name", reserveSystem: true));

    [Theory]
    [InlineData("system")]
    [InlineData("sysx")]
    [InlineData("sys-a")]
    [InlineData("systems.billing")]
    public void Reservation_accepts_names_that_merely_start_with_sys(string value) =>
        Assert.Equal(value, NameSyntax.Canonicalize(value, "name", reserveSystem: true));

    [Fact]
    public void Lookups_without_the_reservation_accept_sys()
    {
        Assert.Equal("sys", NameSyntax.Canonicalize("sys", "name"));
        Assert.Equal("sys.retention", NameSyntax.Canonicalize("Sys.Retention", "name"));
    }
}

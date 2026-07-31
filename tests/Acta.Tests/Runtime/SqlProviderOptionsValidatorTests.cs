using Acta.Sqlite.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Proves invalid provider settings are rejected before a
/// host can boot. Exercised against a local subclass so the test stays provider-package-free; each
/// concrete provider (SqlServer, Postgres, Sqlite) registers the same generic validator.
/// </summary>
public sealed class SqlProviderOptionsValidatorTests
{
    private sealed class TestProviderOptions : SqlProviderOptions;

    private static readonly SqlProviderOptionsValidator<TestProviderOptions> Validator = new();

    private static ValidateOptionsResult Validate(Action<TestProviderOptions> mutate)
    {
        var options = ValidOptions();
        mutate(options);
        return Validator.Validate(name: null, options);
    }

    private static TestProviderOptions ValidOptions() => new() { ConnectionString = "test" };

    [Fact]
    public void Valid_options_pass()
    {
        var result = Validator.Validate(name: null, ValidOptions());
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_connection_string_fails(string connectionString)
    {
        var result = Validate(o => o.ConnectionString = connectionString);
        Assert.True(result.Failed);
        Assert.Contains("ConnectionString", result.FailureMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Acta")]
    [InlineData("acta-schema")]
    [InlineData("1acta")]
    public void Invalid_schema_fails(string schema)
    {
        var result = Validate(o => o.Schema = schema);
        Assert.True(result.Failed);
        Assert.Contains("Schema", result.FailureMessage);
    }

    [Fact]
    public void Overlong_schema_fails()
    {
        var result = Validate(o => o.Schema = "a" + new string('b', IdentifierSyntax.BareIdentifierMaxLength));
        Assert.True(result.Failed);
        Assert.Contains("Schema", result.FailureMessage);
    }

    [Fact]
    public void DeadlockRetryAttempts_below_one_fails()
    {
        var result = Validate(o => o.DeadlockRetryAttempts = 0);
        Assert.True(result.Failed);
        Assert.Contains("DeadlockRetryAttempts", result.FailureMessage);
    }

    [Fact]
    public void DeadlockRetryAttempts_of_one_passes()
    {
        var result = Validate(o => o.DeadlockRetryAttempts = 1);
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void DeadlockRetryAttempts_above_ceiling_fails()
    {
        var result = Validate(o => o.DeadlockRetryAttempts = SqlProviderOptions.MaxDeadlockRetryAttempts + 1);
        Assert.True(result.Failed);
        Assert.Contains("DeadlockRetryAttempts", result.FailureMessage);
    }

    [Fact]
    public void CommandTimeout_zero_fails()
    {
        var result = Validate(o => o.CommandTimeout = TimeSpan.Zero);
        Assert.True(result.Failed);
        Assert.Contains("CommandTimeout", result.FailureMessage);
    }

    [Fact]
    public void CommandTimeout_negative_fails()
    {
        var result = Validate(o => o.CommandTimeout = TimeSpan.FromSeconds(-1));
        Assert.True(result.Failed);
        Assert.Contains("CommandTimeout", result.FailureMessage);
    }

    [Fact]
    public void CommandTimeout_above_ado_net_limit_fails()
    {
        var result = Validate(o => o.CommandTimeout = TimeSpan.FromSeconds((double)int.MaxValue + 1));
        Assert.True(result.Failed);
        Assert.Contains("CommandTimeout", result.FailureMessage);
    }

    [Fact]
    public void Sqlite_schema_must_be_main()
    {
        var validator = new SqliteProviderOptionsValidator();

        Assert.True(validator.Validate(null, new SqliteProviderOptions { ConnectionString = "test" }).Succeeded);
        var result = validator.Validate(null, new SqliteProviderOptions { ConnectionString = "test", Schema = "acta" });
        Assert.True(result.Failed);
        Assert.Contains("main", result.FailureMessage);
    }
}

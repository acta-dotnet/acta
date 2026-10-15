using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>Proves invalid provider settings are rejected before a host can boot.</summary>
public sealed class SqlProviderOptionsValidatorTests
{
    private static readonly SqlProviderOptionsValidator Validator = new();

    private static ValidateOptionsResult Validate(Action<SqlProviderOptions> mutate)
    {
        var options = ValidOptions();
        mutate(options);
        return Validator.Validate(name: null, options);
    }

    private static SqlProviderOptions ValidOptions() => new() { ConnectionString = "test" };

    private static ServiceProvider SqliteHost(Action<SqlProviderOptions> configure)
    {
        var services = new ServiceCollection();
        services.UseActa(acta =>
            acta.UseSqlite(o =>
            {
                o.ConnectionString = "Data Source=:memory:";
                configure(o);
            })
        );
        return services.BuildServiceProvider();
    }

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
        Assert.Contains("SqlProviderOptions.ConnectionString", result.FailureMessage);
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
        var result = Validate(o => o.Schema = "a" + new string('b', SqlIdentifierSyntax.BareIdentifierMaxLength));
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
    public void UseSqlite_rejects_a_schema_other_than_main()
    {
        using var provider = SqliteHost(static o => o.Schema = "x");

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<SqlProviderOptions>>().Value);
        Assert.Contains("main", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseSqlite_defaults_the_schema_to_main()
    {
        using var provider = SqliteHost(static _ => { });

        Assert.Equal("main", provider.GetRequiredService<SqlProviderOptions>().Schema);
    }
}

using Microsoft.Extensions.Options;

namespace Acta.Relational.Connections;

/// <summary>
/// Validates any <see cref="SqlProviderOptions"/> subclass at host startup (paired with
/// <c>ValidateOnStart</c>), one instance per concrete provider options type. Mirrors
/// <c>JobsOptionsValidator</c>'s aggregate-and-report shape.
/// </summary>
internal sealed class SqlProviderOptionsValidator<TOptions> : IValidateOptions<TOptions>
    where TOptions : SqlProviderOptions
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var prefix = typeof(TOptions).Name;

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add($"{prefix}.ConnectionString must not be blank.");
        }

        if (
            string.IsNullOrEmpty(options.Schema)
            || options.Schema.Length > IdentifierSyntax.BareIdentifierMaxLength
            || !IdentifierSyntax.IsBareIdentifier(options.Schema)
        )
        {
            failures.Add(
                $"{prefix}.Schema must be a bare SQL identifier (`[a-z][a-z0-9_]*`) no longer than {IdentifierSyntax.BareIdentifierMaxLength} characters."
            );
        }

        if (options.DeadlockRetryAttempts is < 1 or > SqlProviderOptions.MaxDeadlockRetryAttempts)
        {
            failures.Add(
                $"{prefix}.DeadlockRetryAttempts must be between 1 and {SqlProviderOptions.MaxDeadlockRetryAttempts}: set 1 to disable retry."
            );
        }

        if (options.CommandTimeout <= TimeSpan.Zero)
        {
            failures.Add(
                $"{prefix}.CommandTimeout must be > 0: 0 is ADO.NET's infinite-timeout sentinel, which conflicts with the lease model."
            );
        }
        else if (options.CommandTimeout.TotalSeconds > int.MaxValue)
        {
            failures.Add($"{prefix}.CommandTimeout must not exceed {int.MaxValue} seconds, the ADO.NET command-timeout limit.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

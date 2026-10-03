namespace Acta.Runtime;

/// <summary>
/// Checks the shape of a setting's scope targets, the rule <see cref="ISettings"/> states. They name
/// registered entities, so resolving them is the SQL's job.
/// </summary>
internal static class SettingScope
{
    public static void Validate(string? namespaceName, string? jobName)
    {
        if (jobName is not null && namespaceName is null)
        {
            throw new ArgumentException("A job-definition scope needs its namespace: pass namespaceName with jobName.", nameof(jobName));
        }
        if (namespaceName is not null)
        {
            IdentifierSyntax.ValidateKebab(namespaceName, nameof(namespaceName));
        }
        if (jobName is not null)
        {
            IdentifierSyntax.ValidateKebab(jobName, nameof(jobName));
        }
    }
}

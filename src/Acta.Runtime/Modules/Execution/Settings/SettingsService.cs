using System.Text;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Execution.Settings;

/// <summary>
/// Settings feature behavior: name and scope-target validation, the inline-payload cap on the value
/// (a caller-controlled write, so it hard-throws), and operator actor stamping for the evidence
/// event. Scope is inferred from the targets: none is Global, a namespace alone is Namespace, and a
/// namespace plus job name is Definition; the store resolves targets and reports NotFound.
/// </summary>
internal sealed class SettingsService(ISettingStore store, IOptions<JobsOptions> options)
{
    // Operator/manual only: the actor is stamped here, never accepted from the caller.
    private static JobControlActor Operator(string? actorKey) =>
        new(ActorCode.Operator, JobControlActor.SanitizeActorKey(actorKey).Truncate(ActaTextLimits.ActorKey));

    public async ValueTask<SettingDetail?> GetAsync(string name, string? namespaceName, string? jobName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        // Read paths validate shape only and stay permissive of the sys prefix.
        IdentifierSyntax.ValidateDottedKebab(name, nameof(name), AdminTextLimits.SettingName);
        ValidateScope(namespaceName, jobName);
        var row = await store.GetSettingAsync(new SettingPointLookup(name, namespaceName, jobName), ct);
        return row?.ToSnapshot();
    }

    public async ValueTask<AdminControlResult> SetAsync(
        string name,
        string value,
        string? description,
        string? namespaceName,
        string? jobName,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        IdentifierSyntax.ValidateUserDottedKebab(name, nameof(name), AdminTextLimits.SettingName);
        ValidateScope(namespaceName, jobName);
        CatalogValidation.ValidateSetting(description);

        var bytes = Encoding.UTF8.GetBytes(value);
        var cap = options.Value.MaxInlinePayloadBytes;
        if (bytes.Length > cap)
        {
            throw new PayloadTooLargeException($"setting '{name}'", bytes.Length, cap);
        }

        var outcome = await store.SetSettingAsync(
            new SetSettingCommand(
                name,
                JobPayloadFormat.Text.Id,
                bytes,
                description,
                namespaceName,
                jobName,
                Operator(actorKey),
                reasonMessage.Truncate(ActaTextLimits.ReasonMessage)
            ),
            ct
        );
        return new AdminControlResult(outcome.Action, outcome.Version);
    }

    // Scope targets are lookups of already-registered entities, so shape-only validation suffices.
    private static void ValidateScope(string? namespaceName, string? jobName)
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

namespace Acta.Runtime.Modules.Execution.Settings;

/// <summary><see cref="ISettings"/> implementation: thin delegation to the settings feature service.</summary>
internal sealed class SettingsApi(SettingsService service) : ISettings
{
    public ValueTask<SettingSnapshot?> GetAsync(
        string name,
        string? namespaceName = null,
        string? jobName = null,
        CancellationToken ct = default
    ) => service.GetAsync(name, namespaceName, jobName, ct);

    public ValueTask<AdminControlResult> SetAsync(
        string name,
        string value,
        string? description = null,
        string? namespaceName = null,
        string? jobName = null,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => service.SetAsync(name, value, description, namespaceName, jobName, reasonMessage, actorKey, ct);
}

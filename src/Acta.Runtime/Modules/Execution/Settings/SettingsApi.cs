namespace Acta.Runtime.Modules.Execution.Settings;

/// <summary><see cref="ISettings"/> implementation: thin delegation to the settings feature service.</summary>
internal sealed class SettingsApi(SettingsService service) : ISettings
{
    public ValueTask<SettingDetail?> GetAsync(
        string name,
        string? namespaceName = null,
        string? jobName = null,
        CancellationToken ct = default
    ) => service.GetAsync(name, namespaceName, jobName, ct);

    public ValueTask<AdminControlResult> SetAsync(
        string name,
        string value,
        int? expectedVersion = null,
        string? description = null,
        string? namespaceName = null,
        string? jobName = null,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => service.SetAsync(name, value, expectedVersion, description, namespaceName, jobName, reasonMessage, actorKey, ct);
}

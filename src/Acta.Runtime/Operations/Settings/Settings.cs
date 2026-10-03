namespace Acta.Runtime;

/// <summary><see cref="ISettings"/>, one line per operation.</summary>
internal sealed class Settings(GetSettingHandler getSetting, SetSettingHandler setSetting) : ISettings
{
    public ValueTask<SettingDetail?> GetAsync(
        string name,
        string? namespaceName = null,
        string? jobName = null,
        CancellationToken ct = default
    ) => getSetting.HandleAsync(new GetSettingQuery(name, namespaceName, jobName), ct);

    public ValueTask<SetSettingResponse> SetAsync(
        string name,
        string value,
        int? expectedVersion = null,
        string? description = null,
        string? namespaceName = null,
        string? jobName = null,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) =>
        setSetting.HandleAsync(
            new SetSettingCommand(name, value, expectedVersion, description, namespaceName, jobName, reasonMessage, actorKey),
            ct
        );
}

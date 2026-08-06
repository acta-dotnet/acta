using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Execution.Settings;

/// <summary>Persistence port for the settings feature: one exact-scope point read, one upsert write.</summary>
internal interface ISettingStore
{
    /// <summary>Point-reads one setting by name at the lookup's exact scope; null when it does not exist.</summary>
    Task<SettingRow?> GetSettingAsync(SettingPointLookup lookup, CancellationToken ct);

    /// <summary>Writes one setting at the command's scope (insert when absent, overwrite when present;
    /// last write wins) with a version bump, and emits setting.updated carrying the name as detail.
    /// NotFound when the scope's namespace or definition is not registered.</summary>
    Task<AdminControlOutcome> SetSettingAsync(SetSettingCommand command, CancellationToken ct);
}

/// <summary>One exact-scope setting lookup; null targets mean Global, a namespace alone means
/// Namespace scope, and a namespace plus job name means Definition scope.</summary>
internal sealed record SettingPointLookup(string Name, string? NamespaceName, string? JobName);

/// <summary>One settings write: the validated name, the encoded value pair, the scope targets, and
/// the acting operator.</summary>
internal sealed record SetSettingCommand(
    string Name,
    byte ValueFormatId,
    byte[] Value,
    string? Description,
    string? NamespaceName,
    string? JobName,
    JobControlActor Actor,
    string? ReasonMessage
);

/// <summary>Flat setting row projected from storage.</summary>
internal sealed record SettingRow(
    string Name,
    byte ValueFormatId,
    byte[]? Value,
    string? Description,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
)
{
    public SettingSnapshot ToSnapshot() =>
        new(Name, Value is null ? null : System.Text.Encoding.UTF8.GetString(Value), Description, CreatedAtUtc, ModifiedAtUtc, Version);
}

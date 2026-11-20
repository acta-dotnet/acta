using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// Refuses a reflection-off (Native AOT) start while a registered job's JSON input or result type has no metadata in the
/// JSON serializer that will run, listing every such job in one failure. It rides <see cref="JobsOptions"/> validation,
/// which a host runs as it starts. With reflection on it answers at once, and the registry is resolved only when it is off.
/// </summary>
internal sealed class JsonMetadataValidator(IEnumerable<CatalogDeclaration> catalogs, IServiceProvider services)
    : IValidateOptions<JobsOptions>
{
    public ValidateOptionsResult Validate(string? name, JobsOptions options) =>
        JsonSerializer.IsReflectionEnabledByDefault
        || Refusal(catalogs, services.GetRequiredService<IJobPayloadSerializerRegistry>()) is not { } refusal
            ? ValidateOptionsResult.Skip
            : ValidateOptionsResult.Fail(refusal);

    /// <summary>
    /// The refusal for the registered jobs whose JSON types <paramref name="serializers"/> cannot resolve, or null when
    /// there are none. Only Acta's own JSON serializer can be asked; one the host registered for the format is trusted.
    /// </summary>
    internal static string? Refusal(IEnumerable<CatalogDeclaration> catalogs, IJobPayloadSerializerRegistry serializers)
    {
        if (
            !serializers.IsRegistered(JobPayloadFormat.Json.Id)
            || serializers.Resolve(JobPayloadFormat.Json.Id) is not JsonJobPayloadSerializer json
        )
        {
            return null;
        }

        var missing = new List<string>();
        foreach (var descriptor in catalogs.SelectMany(c => c.Manifests).SelectMany(m => m.GetDescriptors().Descriptors).Distinct())
        {
            if (descriptor.InputPayloadFormat.Id == JobPayloadFormat.Json.Id && !json.Options.TryGetTypeInfo(descriptor.InputType, out _))
            {
                missing.Add($"job '{descriptor.JobName}' input '{descriptor.InputType.FullName}'");
            }
            if (
                descriptor.ResultType is { } result
                && descriptor.ResultPayloadFormat?.Id == JobPayloadFormat.Json.Id
                && !json.Options.TryGetTypeInfo(result, out _)
            )
            {
                missing.Add($"job '{descriptor.JobName}' result '{result.FullName}'");
            }
        }

        return missing.Count == 0
            ? null
            : "Native AOT runs System.Text.Json without reflection, and the JSON serializer has no metadata for these job "
                + $"payload types, so the first use of each would throw: {string.Join("; ", missing)}. Add each type to the "
                + "JsonSerializerContext the host registers with UseJsonPayloads; for a handler that takes several input "
                + "parameters, declare its input record and give the handler that one parameter. Step, variable and signal "
                + "types are checked when first used.";
    }
}

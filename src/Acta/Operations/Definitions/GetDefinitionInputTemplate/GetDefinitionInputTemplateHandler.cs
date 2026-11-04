namespace Acta;

/// <summary><see cref="IDefinitions.GetInputTemplate"/>: reads the generated manifest in process; no SQL, no reflection.</summary>
internal sealed class GetDefinitionInputTemplateHandler(JobDescriptorRegistry descriptorRegistry)
{
    public GetDefinitionInputTemplateResponse? Handle(GetDefinitionInputTemplateQuery query) =>
        descriptorRegistry.Find(query.JobNamespace, query.JobName) is { } descriptor
            ? new GetDefinitionInputTemplateResponse(
                query.JobNamespace,
                query.JobName,
                JobDefinitionRegistration.ContractTypeName(descriptor.InputType),
                descriptor.InputPayloadFormat,
                descriptor.InputTemplateJson
            )
            : null;
}

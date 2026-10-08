namespace Acta.Runtime;

/// <summary><see cref="IJobs.GetInputTemplate"/>: reads the generated manifest in process; no SQL, no reflection.</summary>
internal sealed class GetInputTemplateHandler(JobDescriptorIndex descriptorIndex)
{
    public GetInputTemplateResponse? Handle(GetInputTemplateQuery query) =>
        descriptorIndex.Find(query.JobNamespace, query.JobName) is { } descriptor
            ? new GetInputTemplateResponse(
                query.JobNamespace,
                query.JobName,
                descriptor.InputType.FullName ?? descriptor.InputType.Name,
                descriptor.InputPayloadFormat,
                descriptor.InputTemplateJson
            )
            : null;
}

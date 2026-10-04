namespace Acta.Runtime;

/// <summary><see cref="IJobs.GetInputTemplate"/>: reads the generated manifest in process; no SQL, no reflection.</summary>
internal sealed class GetInputTemplateHandler(JobDescriptorIndex descriptorIndex)
{
    public JobInputTemplate? Handle(GetInputTemplateQuery query) =>
        descriptorIndex.Find(query.JobNamespace, query.JobName) is { } descriptor
            ? new JobInputTemplate(
                descriptor.InputType.FullName ?? descriptor.InputType.Name,
                descriptor.InputPayloadFormat,
                descriptor.InputTemplateJson
            )
            : null;
}

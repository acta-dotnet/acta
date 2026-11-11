namespace Acta;

/// <summary><see cref="IDefinitions.GetInputTemplateAsync"/>: reads the generated manifest in process; no SQL, no reflection.</summary>
internal sealed class GetDefinitionInputTemplateHandler(JobDescriptorRegistry descriptorRegistry)
{
    public GetDefinitionInputTemplateResponse? Handle(GetDefinitionInputTemplateQuery query)
    {
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, "jobNamespace");
        var jobName = QueryValidation.ValidateJobName(query.JobName, jobNamespace, "jobName");
        if (jobNamespace is null || jobName is null)
        {
            throw new InvalidQueryException("jobNamespace and jobName are both required.");
        }

        return descriptorRegistry.Find(jobNamespace, jobName) is { } descriptor
            ? new GetDefinitionInputTemplateResponse(
                jobNamespace,
                jobName,
                JobDefinitionRegistration.ContractTypeName(descriptor.InputType),
                descriptor.InputPayloadFormat,
                descriptor.InputTemplateJson
            )
            : null;
    }
}

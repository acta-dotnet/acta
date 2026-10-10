using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>Conformance for the input-template read: the in-process manifest answers, and a job this host did not register reads null.</summary>
[ConformanceSpec(
    "get-input-template.manifest",
    "The input template comes from this host's generated manifest",
    Area = "Reads",
    Contract = "GetInputTemplate returns a registered job's input type, format, and JSON skeleton from this host's manifest, and null for a job the host did not register."
)]
public sealed class GetInputTemplateSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    [Fact(DisplayName = "A Json job reports its input type, format, and a skeleton of its members")]
    public void Json_job_reports_its_template()
    {
        var template = Jobs.GetInputTemplate(TestNamespace, "add-numbers");

        Assert.NotNull(template);
        Assert.Equal(typeof(AddNumbers).FullName, template.InputTypeName);
        Assert.Equal(JobPayloadFormat.Json, template.InputFormat);
        Assert.Equal("""{"left":0,"right":0}""", template.TemplateJson);
    }

    [Fact(DisplayName = "A job that takes no input reports the NoInput type and no skeleton")]
    public void No_input_job_has_no_template()
    {
        var template = Jobs.GetInputTemplate(TestNamespace, "job-wait-signal");

        Assert.NotNull(template);
        Assert.Equal(typeof(NoInput).FullName, template.InputTypeName);
        Assert.Null(template.TemplateJson);
    }

    [Fact(DisplayName = "An unknown job or a namespace this host did not register reads null")]
    public void Unregistered_job_reads_null()
    {
        Assert.Null(Jobs.GetInputTemplate(TestNamespace, "no-such-job"));
        Assert.Null(Jobs.GetInputTemplate(Unique("other-ns"), "add-numbers"));
    }

    [Fact(DisplayName = "A definition stored by another host but absent from this manifest reads null")]
    public async Task Stored_definition_alone_reads_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("stored-only");
        await new ActaTestSeeder(Db).SeedJobDefinitionAsync(Runtime.RegisteredNamespaceIds[TestNamespace], name: jobName, ct: ct);

        Assert.Null(Jobs.GetInputTemplate(TestNamespace, jobName));
    }
}

using Acta.Kernel;
using Acta.Relational.Schema;
using Xunit;

namespace Acta.Tests.Architecture;

public sealed class TextLimitParityTests
{
    [Fact]
    public void Provider_independent_text_limits_match_relational_columns()
    {
        Assert.Equal(ActaSchema.Tenant.DisplayName.Size, CatalogMetadataLimits.TenantDisplayName);
        Assert.Equal(ActaSchema.Tenant.Description.Size, CatalogMetadataLimits.TenantDescription);
        Assert.Equal(ActaSchema.JobNamespace.OwnerTeam.Size, CatalogMetadataLimits.NamespaceOwnerTeam);
        Assert.Equal(ActaSchema.JobNamespace.Description.Size, CatalogMetadataLimits.NamespaceDescription);
        Assert.Equal(ActaSchema.JobEvent.ActorKey.Size, ActaTextLimits.ActorKey);
        Assert.Equal(ActaSchema.JobEvent.ReasonMessage.Size, ActaTextLimits.ReasonMessage);
        Assert.Equal(ActaSchema.JobStep.ReasonMessage.Size, ActaTextLimits.ReasonMessage);
        Assert.Equal(ActaSchema.JobAlert.ChannelName.Size, ActaTextLimits.AlertChannelName);
        Assert.Equal(ActaSchema.JobAlert.DeduplicationKey.Size, ActaTextLimits.AlertDeduplicationKey);
        Assert.Equal(ActaSchema.JobAlert.Title.Size, ActaTextLimits.AlertTitle);
        Assert.Equal(ActaSchema.JobAlert.Message.Size, ActaTextLimits.AlertMessage);
        Assert.Equal(ActaSchema.JobDefinition.Backoff.Size, ActaTextLimits.DefinitionBackoff);
        Assert.Equal(ActaSchema.JobDefinition.BackoffOverride.Size, ActaTextLimits.DefinitionBackoff);
        Assert.Equal(ActaSchema.JobSchedule.Note.Size, ActaTextLimits.ScheduleNote);
    }
}

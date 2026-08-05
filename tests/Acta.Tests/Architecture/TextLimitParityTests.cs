using System.Collections.Generic;
using System.Linq;
using Acta.Runtime.Kernel;
using Xunit;

namespace Acta.Tests.Architecture;

public sealed class TextLimitParityTests
{
    /// <summary>
    /// Column names that repeat across tables while meaning genuinely different things, each with the
    /// reason it is exempt from the one-width-per-concept rule. Keep this list short: a new entry is
    /// usually a sign the column should be renamed instead, which is what happened to the alert dedupe
    /// key (<c>alerts.dedupe_key</c>, Acta-composed, 512) once it collided with the caller-supplied
    /// <c>jobs.deduplication_key</c> (128).
    /// </summary>
    private static readonly HashSet<string> DistinctConceptsSharingAName = [];

    [Fact(DisplayName = "A column name means one width everywhere it appears")]
    public void Same_column_name_carries_the_same_size()
    {
        var offenders = ActaSchema
            .Entities.SelectMany(e => e.Columns.Select(c => (e.TableName, c.Name, c.Size)))
            .Where(c => c.Size is not null && !DistinctConceptsSharingAName.Contains(c.Name))
            .GroupBy(c => c.Name)
            .Where(g => g.Select(c => c.Size).Distinct().Count() > 1)
            .Select(g => $"{g.Key}: " + string.Join(", ", g.Select(c => $"{c.TableName}({c.Size})")))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "One width per concept. Either align these, or rename one so the names stop colliding:\n  " + string.Join("\n  ", offenders)
        );
    }

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
        Assert.Equal(ActaSchema.JobAlert.DedupeKey.Size, ActaTextLimits.AlertDedupeKey);
        Assert.Equal(ActaSchema.JobAlert.Title.Size, ActaTextLimits.AlertTitle);
        Assert.Equal(ActaSchema.JobAlert.Message.Size, ActaTextLimits.AlertMessage);
        Assert.Equal(ActaSchema.JobDefinition.Backoff.Size, ActaTextLimits.DefinitionBackoff);
        Assert.Equal(ActaSchema.JobDefinition.BackoffOverride.Size, ActaTextLimits.DefinitionBackoff);
        Assert.Equal(ActaSchema.JobSchedule.Note.Size, ActaTextLimits.ScheduleNote);
    }
}

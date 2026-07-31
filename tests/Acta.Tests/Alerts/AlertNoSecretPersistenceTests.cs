using Acta.Relational.Entities;
using Acta.Runtime.Modules.Alerting;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Alerts;

public sealed class AlertNoSecretPersistenceTests
{
    // Acta SQL may persist alert channel_name as routing metadata.
    // Acta SQL must never persist alert transport endpoint, webhook URL, credential, routing key, or opaque transport config.
    // Delivery configuration is process startup configuration resolved through IAlertChannelRegistry.

    [Fact]
    public void Alert_channel_transport_configuration_is_not_modelled_as_sql_state()
    {
        var entityTypes = typeof(JobAlert).Assembly.GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("JobAlertChannel", entityTypes);
    }

    [Fact]
    public void Deliverable_alert_projection_exposes_only_logical_channel_name()
    {
        var properties = typeof(DeliverableAlert).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(DeliverableAlert.ChannelName), properties);
        Assert.DoesNotContain("TransportKind", properties);
        Assert.DoesNotContain("Endpoint", properties);
        Assert.DoesNotContain("ConfigFormatId", properties);
        Assert.DoesNotContain("Config", properties);
    }

    [Fact]
    public void Public_alert_list_exposes_channel_name_only()
    {
        var properties = typeof(JobAlertListItem).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(JobAlertListItem.ChannelName), properties);
        Assert.DoesNotContain("TransportKind", properties);
        Assert.DoesNotContain("Endpoint", properties);
        Assert.DoesNotContain("ConfigFormatId", properties);
        Assert.DoesNotContain("Config", properties);
    }

    [Fact]
    public void Schema_artifacts_do_not_contain_alert_channel_table_or_transport_target_columns()
    {
        var root = IntegrationConfig.FindRepoRoot();
        var docs = File.ReadAllText(Path.Combine(root, "docs", "reference", "data-model.md"));
        var snapshot = File.ReadAllText(Path.Combine(root, "src", "Acta.Relational", "Schema", "schema-snapshot.json"));
        var sqlite = File.ReadAllText(Path.Combine(root, "src", "Acta.Sqlite", "Schema", "Migrations", "M001_init.sql"));
        var postgres = File.ReadAllText(Path.Combine(root, "src", "Acta.Postgres", "Schema", "Migrations", "M001_init.sql"));
        var sqlServer = File.ReadAllText(Path.Combine(root, "src", "Acta.SqlServer", "Schema", "Migrations", "M001_init.sql"));

        Assert.DoesNotContain("job_alert_channel", docs);
        Assert.DoesNotContain("view_job_alert_channel", docs);
        Assert.DoesNotContain("column-acta-job-alert-channel--endpoint", docs);
        Assert.DoesNotContain("column-acta-job-alert-channel--config", docs);
        Assert.DoesNotContain("job_alert_channel", snapshot);
        Assert.DoesNotContain("view_job_alert_channel", snapshot);
        Assert.DoesNotContain("job_alert_channel", sqlite);
        Assert.DoesNotContain("view_job_alert_channel", sqlite);
        Assert.DoesNotContain("job_alert_channel", postgres);
        Assert.DoesNotContain("view_job_alert_channel", postgres);
        Assert.DoesNotContain("job_alert_channel", sqlServer);
        Assert.DoesNotContain("view_job_alert_channel", sqlServer);
    }
}

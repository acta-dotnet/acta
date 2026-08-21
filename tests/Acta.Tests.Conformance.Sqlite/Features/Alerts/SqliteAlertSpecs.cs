using Acta.Tests.Conformance.Features.Alerts;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Alerts;

public sealed class SqliteAlertChannelRegistrySpec : AlertChannelRegistrySpec<SqliteConformanceFixture>;

public sealed class SqliteAlertChannelValidationSpec : AlertChannelValidationSpec<SqliteConformanceFixture>;

public sealed class SqliteAlertDeliverySpec : AlertDeliverySpec<SqliteConformanceFixture>;

public sealed class SqliteAlertDeliveryFailureSpec : AlertDeliveryFailureSpec<SqliteConformanceFixture>;

public sealed class SqliteAlertsProjectionSpec : AlertsProjectionSpec<SqliteConformanceFixture>;

public sealed class SqliteAlertThresholdReachedSpec : AlertThresholdReachedSpec<SqliteConformanceFixture>;

public sealed class SqliteAlertProfileMatrixSpec : AlertProfileMatrixSpec<SqliteConformanceFixture>;

public sealed class SqliteAlertAcknowledgeResolveSpec : AlertAcknowledgeResolveSpec<SqliteConformanceFixture>;

public sealed class SqliteListJobAlertsFilterMatrixSpec : ListJobAlertsFilterMatrixSpec<SqliteConformanceFixture>;

public sealed class SqliteListJobAlertsSpec : ListJobAlertsSpec<SqliteConformanceFixture>;

public sealed class SqliteRaiseJobAlertSpec : RaiseJobAlertSpec<SqliteConformanceFixture>;

public sealed class SqliteAlertRefDedupeStabilitySpec : AlertRefDedupeStabilitySpec<SqliteConformanceFixture>;

public sealed class SqliteAlertProjectionReplaySpec : AlertProjectionReplaySpec<SqliteConformanceFixture>;

public sealed class SqliteAlertProjectionDrainSpec : AlertProjectionDrainSpec<SqliteConformanceFixture>;

public sealed class SqliteRecurringFailureAlertSpec : RecurringFailureAlertSpec<SqliteConformanceFixture>;

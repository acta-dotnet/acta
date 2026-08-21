using Acta.Tests.Conformance.Features.Alerts;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Alerts;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerAlertChannelRegistrySpec : AlertChannelRegistrySpec<SqlServerConformanceFixture>;

public sealed class SqlServerAlertChannelValidationSpec : AlertChannelValidationSpec<SqlServerConformanceFixture>;

public sealed class SqlServerAlertDeliverySpec : AlertDeliverySpec<SqlServerConformanceFixture>;

public sealed class SqlServerAlertDeliveryFailureSpec : AlertDeliveryFailureSpec<SqlServerConformanceFixture>;

public sealed class SqlServerAlertsProjectionSpec : AlertsProjectionSpec<SqlServerConformanceFixture>;

public sealed class SqlServerAlertThresholdReachedSpec : AlertThresholdReachedSpec<SqlServerConformanceFixture>;

public sealed class SqlServerAlertProfileMatrixSpec : AlertProfileMatrixSpec<SqlServerConformanceFixture>;

public sealed class SqlServerAlertAcknowledgeResolveSpec : AlertAcknowledgeResolveSpec<SqlServerConformanceFixture>;

public sealed class SqlServerListJobAlertsFilterMatrixSpec : ListJobAlertsFilterMatrixSpec<SqlServerConformanceFixture>;

public sealed class SqlServerListJobAlertsSpec : ListJobAlertsSpec<SqlServerConformanceFixture>;

public sealed class SqlServerRaiseJobAlertSpec : RaiseJobAlertSpec<SqlServerConformanceFixture>;

public sealed class SqlServerAlertRefDedupeStabilitySpec : AlertRefDedupeStabilitySpec<SqlServerConformanceFixture>;

public sealed class SqlServerAlertProjectionReplaySpec : AlertProjectionReplaySpec<SqlServerConformanceFixture>;

public sealed class SqlServerAlertProjectionDrainSpec : AlertProjectionDrainSpec<SqlServerConformanceFixture>;

public sealed class SqlServerRecurringFailureAlertSpec : RecurringFailureAlertSpec<SqlServerConformanceFixture>;

using Acta.Tests.Conformance.Features.Alerts;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Alerts;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgAlertChannelRegistrySpec : AlertChannelRegistrySpec<PgConformanceFixture>;

public sealed class PgAlertChannelValidationSpec : AlertChannelValidationSpec<PgConformanceFixture>;

public sealed class PgAlertDeliverySpec : AlertDeliverySpec<PgConformanceFixture>;

public sealed class PgAlertDeliveryFailureSpec : AlertDeliveryFailureSpec<PgConformanceFixture>;

public sealed class PgAlertsProjectionSpec : AlertsProjectionSpec<PgConformanceFixture>;

public sealed class PgAlertThresholdReachedSpec : AlertThresholdReachedSpec<PgConformanceFixture>;

public sealed class PgAlertProfileMatrixSpec : AlertProfileMatrixSpec<PgConformanceFixture>;

public sealed class PgAlertAcknowledgeResolveSpec : AlertAcknowledgeResolveSpec<PgConformanceFixture>;

public sealed class PgListJobAlertsFilterMatrixSpec : ListJobAlertsFilterMatrixSpec<PgConformanceFixture>;

public sealed class PgListJobAlertsSpec : ListJobAlertsSpec<PgConformanceFixture>;

public sealed class PgRaiseJobAlertSpec : RaiseJobAlertSpec<PgConformanceFixture>;

public sealed class PgAlertRefDedupeStabilitySpec : AlertRefDedupeStabilitySpec<PgConformanceFixture>;

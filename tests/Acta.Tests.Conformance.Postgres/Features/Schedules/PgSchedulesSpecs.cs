using Acta.Tests.Conformance.Features.Schedules;
using Acta.Tests.Conformance.Postgres.Testing;

namespace Acta.Tests.Conformance.Postgres.Features.Schedules;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgGetScheduleStateSpec : GetScheduleStateSpec<PgConformanceFixture>;

public sealed class PgListJobSchedulesFilterMatrixSpec : ListJobSchedulesFilterMatrixSpec<PgConformanceFixture>;

public sealed class PgListJobSchedulesSpec : ListJobSchedulesSpec<PgConformanceFixture>;

public sealed class PgScheduleEnvironmentGatingSpec : ScheduleEnvironmentGatingSpec<PgConformanceFixture>;

public sealed class PgScheduleInsertMisfireMatrixSpec : ScheduleInsertMisfireMatrixSpec<PgConformanceFixture>;

public sealed class PgScheduleOverridesSpec : ScheduleOverridesSpec<PgConformanceFixture>;

public sealed class PgSchedulePauseResumeSpec : SchedulePauseResumeSpec<PgConformanceFixture>;

public sealed class PgScheduleTriggerNowSpec : ScheduleTriggerNowSpec<PgConformanceFixture>;

public sealed class PgScheduleSysPreviewSpec : ScheduleSysPreviewSpec<PgConformanceFixture>;

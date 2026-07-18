using Acta.Tests.Conformance.Features.Schedules;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Features.Schedules;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerGetScheduleStateSpec : GetScheduleStateSpec<SqlServerConformanceFixture>;

public sealed class SqlServerListJobSchedulesFilterMatrixSpec : ListJobSchedulesFilterMatrixSpec<SqlServerConformanceFixture>;

public sealed class SqlServerListJobSchedulesSpec : ListJobSchedulesSpec<SqlServerConformanceFixture>;

public sealed class SqlServerScheduleEnvironmentGatingSpec : ScheduleEnvironmentGatingSpec<SqlServerConformanceFixture>;

public sealed class SqlServerScheduleInsertMisfireMatrixSpec : ScheduleInsertMisfireMatrixSpec<SqlServerConformanceFixture>;

public sealed class SqlServerScheduleOverridesSpec : ScheduleOverridesSpec<SqlServerConformanceFixture>;

public sealed class SqlServerSchedulePauseResumeSpec : SchedulePauseResumeSpec<SqlServerConformanceFixture>;

public sealed class SqlServerScheduleTriggerNowSpec : ScheduleTriggerNowSpec<SqlServerConformanceFixture>;

public sealed class SqlServerScheduleSysPreviewSpec : ScheduleSysPreviewSpec<SqlServerConformanceFixture>;

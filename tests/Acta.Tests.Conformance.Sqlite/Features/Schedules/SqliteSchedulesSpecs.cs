using Acta.Tests.Conformance.Features.Schedules;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Features.Schedules;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqliteGetScheduleStateSpec : GetScheduleStateSpec<SqliteConformanceFixture>;

public sealed class SqliteListJobSchedulesFilterMatrixSpec : ListJobSchedulesFilterMatrixSpec<SqliteConformanceFixture>;

public sealed class SqliteListJobSchedulesSpec : ListJobSchedulesSpec<SqliteConformanceFixture>;

public sealed class SqliteScheduleEnvironmentGatingSpec : ScheduleEnvironmentGatingSpec<SqliteConformanceFixture>;

public sealed class SqliteScheduleInsertMisfireMatrixSpec : ScheduleInsertMisfireMatrixSpec<SqliteConformanceFixture>;

public sealed class SqliteScheduleOverridesSpec : ScheduleOverridesSpec<SqliteConformanceFixture>;

public sealed class SqliteSchedulePauseResumeSpec : SchedulePauseResumeSpec<SqliteConformanceFixture>;

public sealed class SqliteScheduleTriggerNowSpec : ScheduleTriggerNowSpec<SqliteConformanceFixture>;

public sealed class SqliteScheduleSysPreviewSpec : ScheduleSysPreviewSpec<SqliteConformanceFixture>;

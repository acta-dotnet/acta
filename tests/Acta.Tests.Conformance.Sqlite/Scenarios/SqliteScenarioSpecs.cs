using Acta.Tests.Conformance.Scenarios;
using Acta.Tests.Conformance.Sqlite.Testing;

namespace Acta.Tests.Conformance.Sqlite.Scenarios;

public sealed class SqliteStepSpec : StepSpec<SqliteConformanceFixture>;

public sealed class SqliteScenarioSessionSpec : ScenarioSessionSpec<SqliteConformanceFixture>;

public sealed class SqliteExclusiveKeyMutexSpec : ExclusiveKeyMutexSpec<SqliteConformanceFixture>;

public sealed class SqliteChildJobCrossNamespaceSpec : ChildJobCrossNamespaceSpec<SqliteConformanceFixture>;

public sealed class SqliteChildJobSpec : ChildJobSpec<SqliteConformanceFixture>;

public sealed class SqliteControlVerbsSpec : ControlVerbsSpec<SqliteConformanceFixture>;

public sealed class SqliteGoldenPathSpec : GoldenPathSpec<SqliteConformanceFixture>;

public sealed class SqliteJobContractSpec : JobContractSpec<SqliteConformanceFixture>;

public sealed class SqliteHandlerControlSpec : HandlerControlSpec<SqliteConformanceFixture>;

public sealed class SqlitePurgeExpiredDataSpec : PurgeExpiredDataSpec<SqliteConformanceFixture>;

public sealed class SqliteJobRefSurvivesPurgeSpec : JobRefSurvivesPurgeSpec<SqliteConformanceFixture>;

public sealed class SqliteWorkerRefSurvivesPurgeSpec : WorkerRefSurvivesPurgeSpec<SqliteConformanceFixture>;

public sealed class SqliteReferenceEnqueueSpec : ReferenceEnqueueSpec<SqliteConformanceFixture>;

public sealed class SqliteRelativeDelayUsesDbClockSpec : RelativeDelayUsesDbClockSpec<SqliteConformanceFixture>;

public sealed class SqliteRescheduleSleepSpec : RescheduleSleepSpec<SqliteConformanceFixture>;

public sealed class SqliteScheduleFiresOnTickSpec : ScheduleFiresOnTickSpec<SqliteConformanceFixture>;

public sealed class SqliteSchedulePauseFiringSpec : SchedulePauseFiringSpec<SqliteConformanceFixture>;

public sealed class SqliteSignalSpec : SignalSpec<SqliteConformanceFixture>;

public sealed class SqliteExplainScenarioSpec : ExplainScenarioSpec<SqliteConformanceFixture>;

public sealed class SqliteTypedEnqueueSpec : TypedEnqueueSpec<SqliteConformanceFixture>;

public sealed class SqliteVariableContextSpec : VariableContextSpec<SqliteConformanceFixture>;

public sealed class SqliteCliControlSpec : CliControlSpec<SqliteConformanceFixture>;

public sealed class SqliteIntervalScheduleFireSpec : IntervalScheduleFireSpec<SqliteConformanceFixture>;

public sealed class SqliteMultiScheduleSlotSpec : MultiScheduleSlotSpec<SqliteConformanceFixture>;

public sealed class SqliteScheduledSlotPrioritySpec : ScheduledSlotPrioritySpec<SqliteConformanceFixture>;

public sealed class SqliteStepDeferredRetrySpec : StepDeferredRetrySpec<SqliteConformanceFixture>;

public sealed class SqliteStepExhaustionSpec : StepExhaustionSpec<SqliteConformanceFixture>;

public sealed class SqliteStepAtMostOnceSpec : StepAtMostOnceSpec<SqliteConformanceFixture>;

using Acta.Tests.Conformance.Scenarios;
using Acta.Tests.Conformance.SqlServer.Testing;

namespace Acta.Tests.Conformance.SqlServer.Scenarios;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class SqlServerStepSpec : StepSpec<SqlServerConformanceFixture>;

public sealed class SqlServerScenarioSessionSpec : ScenarioSessionSpec<SqlServerConformanceFixture>;

public sealed class SqlServerChildJobCrossNamespaceSpec : ChildJobCrossNamespaceSpec<SqlServerConformanceFixture>;

public sealed class SqlServerChildJobSpec : ChildJobSpec<SqlServerConformanceFixture>;

public sealed class SqlServerExclusiveKeyMutexSpec : ExclusiveKeyMutexSpec<SqlServerConformanceFixture>;

public sealed class SqlServerControlVerbsSpec : ControlVerbsSpec<SqlServerConformanceFixture>;

public sealed class SqlServerGoldenPathSpec : GoldenPathSpec<SqlServerConformanceFixture>;

public sealed class SqlServerJobContractSpec : JobContractSpec<SqlServerConformanceFixture>;

public sealed class SqlServerHandlerControlSpec : HandlerControlSpec<SqlServerConformanceFixture>;

public sealed class SqlServerPurgeExpiredDataSpec : PurgeExpiredDataSpec<SqlServerConformanceFixture>;

public sealed class SqlServerJobRefSurvivesPurgeSpec : JobRefSurvivesPurgeSpec<SqlServerConformanceFixture>;

public sealed class SqlServerWorkerRefSurvivesPurgeSpec : WorkerRefSurvivesPurgeSpec<SqlServerConformanceFixture>;

public sealed class SqlServerReferenceEnqueueSpec : ReferenceEnqueueSpec<SqlServerConformanceFixture>;

public sealed class SqlServerRelativeDelayUsesDbClockSpec : RelativeDelayUsesDbClockSpec<SqlServerConformanceFixture>;

public sealed class SqlServerRescheduleSleepSpec : RescheduleSleepSpec<SqlServerConformanceFixture>;

public sealed class SqlServerScheduleFiresOnTickSpec : ScheduleFiresOnTickSpec<SqlServerConformanceFixture>;

public sealed class SqlServerSchedulePauseFiringSpec : SchedulePauseFiringSpec<SqlServerConformanceFixture>;

public sealed class SqlServerSignalSpec : SignalSpec<SqlServerConformanceFixture>;

public sealed class SqlServerSignalTimeoutSpec : SignalTimeoutSpec<SqlServerConformanceFixture>;

public sealed class SqlServerExplainScenarioSpec : ExplainScenarioSpec<SqlServerConformanceFixture>;

public sealed class SqlServerTypedEnqueueSpec : TypedEnqueueSpec<SqlServerConformanceFixture>;

public sealed class SqlServerVariableContextSpec : VariableContextSpec<SqlServerConformanceFixture>;

public sealed class SqlServerCliControlSpec : CliControlSpec<SqlServerConformanceFixture>;

public sealed class SqlServerIntervalScheduleFireSpec : IntervalScheduleFireSpec<SqlServerConformanceFixture>;

public sealed class SqlServerMultiScheduleSlotSpec : MultiScheduleSlotSpec<SqlServerConformanceFixture>;

public sealed class SqlServerScheduledSlotPrioritySpec : ScheduledSlotPrioritySpec<SqlServerConformanceFixture>;

public sealed class SqlServerStepDeferredRetrySpec : StepDeferredRetrySpec<SqlServerConformanceFixture>;

public sealed class SqlServerStepExhaustionSpec : StepExhaustionSpec<SqlServerConformanceFixture>;

public sealed class SqlServerStepAtMostOnceSpec : StepAtMostOnceSpec<SqlServerConformanceFixture>;

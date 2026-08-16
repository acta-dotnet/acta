using Acta.Tests.Conformance.Postgres.Testing;
using Acta.Tests.Conformance.Scenarios;

namespace Acta.Tests.Conformance.Postgres.Scenarios;

// One concrete class per spec; xunit v3 runs each class as its own parallel test collection.

public sealed class PgStepSpec : StepSpec<PgConformanceFixture>;

public sealed class PgScenarioSessionSpec : ScenarioSessionSpec<PgConformanceFixture>;

public sealed class PgChildJobCrossNamespaceSpec : ChildJobCrossNamespaceSpec<PgConformanceFixture>;

public sealed class PgChildJobSpec : ChildJobSpec<PgConformanceFixture>;

public sealed class PgExclusiveKeyMutexSpec : ExclusiveKeyMutexSpec<PgConformanceFixture>;

public sealed class PgControlVerbsSpec : ControlVerbsSpec<PgConformanceFixture>;

public sealed class PgGoldenPathSpec : GoldenPathSpec<PgConformanceFixture>;

public sealed class PgJobContractSpec : JobContractSpec<PgConformanceFixture>;

public sealed class PgHandlerControlSpec : HandlerControlSpec<PgConformanceFixture>;

public sealed class PgPurgeExpiredDataSpec : PurgeExpiredDataSpec<PgConformanceFixture>;

public sealed class PgJobRefSurvivesPurgeSpec : JobRefSurvivesPurgeSpec<PgConformanceFixture>;

public sealed class PgWorkerRefSurvivesPurgeSpec : WorkerRefSurvivesPurgeSpec<PgConformanceFixture>;

public sealed class PgReferenceEnqueueSpec : ReferenceEnqueueSpec<PgConformanceFixture>;

public sealed class PgRelativeDelayUsesDbClockSpec : RelativeDelayUsesDbClockSpec<PgConformanceFixture>;

public sealed class PgRescheduleSleepSpec : RescheduleSleepSpec<PgConformanceFixture>;

public sealed class PgScheduleFiresOnTickSpec : ScheduleFiresOnTickSpec<PgConformanceFixture>;

public sealed class PgSchedulePauseFiringSpec : SchedulePauseFiringSpec<PgConformanceFixture>;

public sealed class PgSignalSpec : SignalSpec<PgConformanceFixture>;

public sealed class PgExplainScenarioSpec : ExplainScenarioSpec<PgConformanceFixture>;

public sealed class PgTypedEnqueueSpec : TypedEnqueueSpec<PgConformanceFixture>;

public sealed class PgVariableContextSpec : VariableContextSpec<PgConformanceFixture>;

public sealed class PgCliControlSpec : CliControlSpec<PgConformanceFixture>;

public sealed class PgIntervalScheduleFireSpec : IntervalScheduleFireSpec<PgConformanceFixture>;

public sealed class PgMultiScheduleSlotSpec : MultiScheduleSlotSpec<PgConformanceFixture>;

public sealed class PgScheduledSlotPrioritySpec : ScheduledSlotPrioritySpec<PgConformanceFixture>;

public sealed class PgStepDeferredRetrySpec : StepDeferredRetrySpec<PgConformanceFixture>;

public sealed class PgStepExhaustionSpec : StepExhaustionSpec<PgConformanceFixture>;

public sealed class PgStepAtMostOnceSpec : StepAtMostOnceSpec<PgConformanceFixture>;

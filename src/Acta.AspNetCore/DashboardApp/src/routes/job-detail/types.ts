// Field order mirrors the server-side GetJobResponse record, which in turn mirrors the jobs entity and
// then its jobs_state row: identity, scope/routing, caller keys, input format, runtime state, audit.
export interface JobDetail {
  jobRef: string;
  kind: string;
  jobNamespace: string;
  jobName: string;
  lineageRootJobRef: string | null;
  parentJobRef: string | null;
  tenantName: string | null;
  deduplicationKey: string | null;
  correlationKey: string | null;
  concurrencyKey: string | null;
  inputFormatId: number;
  status: string;
  priority: string;
  // The lane the job runs in, and while it is Blocked the lane member it waits behind.
  lane: string | null;
  blockedBehindJobRef: string | null;
  nextRunAtUtc: string | null;
  executionNumber: number;
  failureCount: number;
  leasedByWorkerRef: string | null;
  leaseExpiresAtUtc: string | null;
  retentionUntilUtc: string | null;
  createdAtUtc: string;
  modifiedAtUtc: string;
  version: number;
}

export interface JobWait {
  kind: string;
  name: string;
  dueAtUtc: string | null;
}

// One step slot; status is the kebab StepStatusCode.
export interface JobStep {
  name: string;
  status: string;
  explanation: string;
}

export interface JobExplanation {
  headline: string;
  activeWait: JobWait | null;
  lease: {
    // Both go null together once retention purged the holding worker's row.
    workerRef: string | null;
    workerName: string | null;
    expiresAtUtc: string | null;
    expired: boolean;
    workerLastSeenAtUtc: string | null;
    workerStale: boolean;
    recoveryExpectation: string;
  } | null;
  lastExecutedBy: string | null;
  steps: JobStep[];
  reason: string | null;
  nextActions: { kind: string; description: string }[];
}

export interface JobLineageNode {
  jobRef: string;
  jobName: string;
  status: string;
  createdAtUtc: string;
  modifiedAtUtc: string;
}

export interface JobLineage {
  ancestors: JobLineageNode[];
  job: JobLineageNode;
  steps: JobStep[];
  activeWait: JobWait | null;
  children: JobLineageNode[];
  childrenHasMore: boolean;
}

export interface JobWorker {
  workerRef: string;
  jobNamespace: string;
  status: string;
  hostName: string;
  deploymentVersion: string;
  engineVersion: string | null;
  dotnetVersion: string | null;
  processId: number | null;
  maxConcurrentExecutors: number;
  lastSeenAtUtc: string;
  startedAtUtc: string;
  modifiedAtUtc: string;
}

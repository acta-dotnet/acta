// Field order mirrors the server-side JobDetail record, which in turn mirrors the jobs entity and
// then its runtimes row: identity, scope/routing, caller keys, input format, runtime state, audit.
export interface JobDetail {
  jobRef: string;
  jobNamespace: string;
  jobName: string;
  lineageRootJobRef: string | null;
  parentJobRef: string | null;
  tenantKey: string | null;
  deduplicationKey: string | null;
  correlationKey: string | null;
  exclusiveKey: string | null;
  inputFormatId: number;
  status: string;
  priority: string;
  nextRunAtUtc: string | null;
  executionNumber: number;
  failureCount: number;
  leasedByWorkerRef: string | null;
  leaseExpiresAtUtc: string | null;
  retentionUntilUtc: string | null;
  createdAtUtc: string;
  modifiedAtUtc: string;
}

export interface JobEvent {
  jobEventId: number;
  eventCode: string;
  createdAtUtc: string;
  jobNamespace: string;
  jobRef: string | null;
  workerRef: string | null;
  executionNumber: number | null;
  fromStatus: string | null;
  toStatus: string | null;
  executionStatus: string | null;
  durationMs: number | null;
  reasonCode: string | null;
  reasonMessage: string | null;
}

export interface JobWait {
  kind: string;
  name: string;
  dueAtUtc: string | null;
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
    workerLastHeartbeatAtUtc: string | null;
    workerStale: boolean;
    recoveryExpectation: string;
  } | null;
  lastExecutedBy: string | null;
  steps: { name: string; state: string; explanation: string }[];
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
  steps: { name: string; state: string; explanation: string }[];
  activeWait: JobWait | null;
  children: JobLineageNode[];
  childrenHasMore: boolean;
}

export interface JobWorker {
  workerRef: string;
  jobNamespace: string;
  status: string;
  host: string;
  deploymentVersion: string;
  engineVersion: string | null;
  dotnetVersion: string | null;
  processId: number | null;
  maxConcurrency: number;
  lastHeartbeatAtUtc: string;
  createdAtUtc: string;
  modifiedAtUtc: string;
}

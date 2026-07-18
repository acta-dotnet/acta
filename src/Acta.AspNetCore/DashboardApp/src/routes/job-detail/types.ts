export interface JobSnapshot {
  jobRef: string;
  lineageRootJobRef: string | null;
  parentJobRef: string | null;
  deduplicationKey: string | null;
  correlationKey: string | null;
  jobNamespace: string;
  jobName: string;
  tenantId: number | null;
  status: string;
  priority: string;
  executionNumber: number;
  failureCount: number;
  inputFormatId: number;
  nextRunAtUtc: string | null;
  leasedByWorkerId: number | null;
  leaseExpiresAtUtc: string | null;
  exclusiveKey: string | null;
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
    workerId: number;
    workerName: string | null;
    expiresAtUtc: string | null;
    expired: boolean;
    workerLastSeenAtUtc: string | null;
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
  workerId: number;
  jobNamespace: string;
  status: string;
  host: string;
  deploymentVersion: string;
  engineVersion: string | null;
  dotnetVersion: string | null;
  processId: number | null;
  maxConcurrency: number;
  lastSeenAtUtc: string;
  createdAtUtc: string;
  modifiedAtUtc: string;
}

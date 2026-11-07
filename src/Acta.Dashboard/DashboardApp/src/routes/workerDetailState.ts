export interface WorkerDetailShape {
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

export function workerStatusInterpretation(status: string): string {
  switch (status.toLowerCase()) {
    case 'active':
      return 'Live and eligible to claim jobs in its namespace.';
    case 'draining':
      return 'Finishing in-flight work without claiming new jobs.';
    case 'stopped':
      return 'Stopped cleanly and will not claim more jobs.';
    case 'dead':
      return 'Heartbeat expired unexpectedly; this registration will not return.';
    default:
      return 'Worker state is not recognized by this dashboard version.';
  }
}

export function workerSupportSummary(worker: WorkerDetailShape): string {
  return [
    `Acta worker ${worker.workerRef}`,
    `Namespace: ${worker.jobNamespace}`,
    `Status: ${worker.status}`,
    `Host/PID: ${worker.hostName}/${worker.processId ?? 'unknown'}`,
    `Deployment: ${worker.deploymentVersion}`,
    `Engine: ${worker.engineVersion ?? 'unknown'}`,
    `.NET: ${worker.dotnetVersion ?? 'unknown'}`,
    `Executors: ${worker.maxConcurrentExecutors}`,
    `Last heartbeat: ${worker.lastSeenAtUtc}`,
    `Started: ${worker.startedAtUtc}`
  ].join('\n');
}

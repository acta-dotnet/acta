import type { JobEvent, JobExplanation, JobLineageNode, JobDetail } from './types.ts';

export function latestMeaningfulEvent(events: readonly JobEvent[]): JobEvent | null {
  if (events.length === 0) return null;
  const newest = [...events].sort(
    (a, b) => new Date(b.createdAtUtc).getTime() - new Date(a.createdAtUtc).getTime() || b.jobEventId - a.jobEventId
  );
  return newest.find((event) => event.reasonMessage || event.reasonCode) ?? newest[0];
}

/** Merge the independently polled newest page with immutable pages accumulated by the history feed. */
export function mergeJobEvents(head: readonly JobEvent[], historyPages: readonly (readonly JobEvent[])[]): JobEvent[] {
  const seen = new Set<number>();
  const merged: JobEvent[] = [];
  for (const event of [...head, ...historyPages.flat()]) {
    if (seen.has(event.jobEventId)) continue;
    seen.add(event.jobEventId);
    merged.push(event);
  }
  return merged;
}

export function childRollup(children: readonly JobLineageNode[]): [string, number][] {
  const counts = new Map<string, number>();
  for (const child of children) counts.set(child.status, (counts.get(child.status) ?? 0) + 1);
  return [...counts.entries()].sort(([aStatus, aCount], [bStatus, bCount]) => bCount - aCount || aStatus.localeCompare(bStatus));
}

/** The child-latch slot key the framework writes on the parent: `sys.child.{childJobId}`. */
const childLatchPrefix = 'sys.child.';

/**
 * How an active durable wait reads on a job panel: the word for its kind, plus the part worth setting
 * in monospace. Mirrors the server explainer's phrasing so `jobs explain` and the dashboard describe
 * the same wait the same way. A child latch's slot name is a framework key rather than anything a
 * handler chose, so `sys.child.42` reads as `child job 42`.
 */
export function activeWaitLabel(kind: string, name: string): { kind: string; name: string } {
  switch (kind.toLowerCase()) {
    case 'signal':
      return { kind: 'signal', name };
    case 'timer':
      return { kind: 'timer', name };
    case 'child-latch':
      return name.startsWith(childLatchPrefix) ? { kind: 'child job', name: name.slice(childLatchPrefix.length) } : { kind: 'child latch', name };
    default:
      // An unrecognized kind says its own name rather than borrowing another's. The panels previously
      // asked only "is it a signal?" and called everything else a timer, which is how a parent parked
      // on a child came to read as waiting on one.
      return { kind: kind.toLowerCase(), name };
  }
}

export function payloadFormatLabel(id: number): string {
  return ({ 0: 'none', 1: 'json', 2: 'bytes', 3: 'text' } as Record<number, string>)[id] ?? `format #${id}`;
}

export function buildIncidentSummary(
  job: JobDetail,
  explanation: JobExplanation | null,
  events: readonly JobEvent[],
  dashboardUrl: string
): string {
  const lines = [
    `Acta incident: ${job.jobRef}`,
    `Job: ${job.jobNamespace} / ${job.jobName}`,
    `Status: ${job.status}`,
    `Created: ${job.createdAtUtc}`,
    `Modified: ${job.modifiedAtUtc}`
  ];
  if (explanation?.headline) lines.push(`Explain: ${explanation.headline}`);
  if (explanation?.reason) lines.push(`Reason: ${explanation.reason}`);
  if (explanation?.activeWait) lines.push(`Active wait: ${explanation.activeWait.kind} ${explanation.activeWait.name}`);
  if (explanation?.lease) {
    lines.push(
      `Lease: worker ${explanation.lease.workerName ?? explanation.lease.workerRef ?? '(purged)'}, expires ${explanation.lease.expiresAtUtc ?? 'unknown'}, expired=${explanation.lease.expired}`
    );
  }

  const latest = [...events]
    .sort((a, b) => new Date(b.createdAtUtc).getTime() - new Date(a.createdAtUtc).getTime() || b.jobEventId - a.jobEventId)
    .slice(0, 5);
  if (latest.length > 0) {
    lines.push('Latest events:');
    for (const event of latest) {
      const reason = event.reasonMessage ?? event.reasonCode;
      lines.push(`- ${event.createdAtUtc} ${event.eventCode}${reason ? `: ${reason}` : ''}`);
    }
  }
  lines.push(`Dashboard: ${dashboardUrl}`);
  return lines.join('\n');
}

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

export function payloadFormatLabel(id: number): string {
  return ({ 0: 'none', 1: 'json', 2: 'bytes', 3: 'text' } as Record<number, string>)[id] ?? `format #${id}`;
}

export function buildIncidentSummary(
  snapshot: JobDetail,
  explanation: JobExplanation | null,
  events: readonly JobEvent[],
  dashboardUrl: string
): string {
  const lines = [
    `Acta incident: ${snapshot.jobRef}`,
    `Job: ${snapshot.jobNamespace} / ${snapshot.jobName}`,
    `Status: ${snapshot.status}`,
    `Created: ${snapshot.createdAtUtc}`,
    `Modified: ${snapshot.modifiedAtUtc}`
  ];
  if (explanation?.headline) lines.push(`Explain: ${explanation.headline}`);
  if (explanation?.reason) lines.push(`Reason: ${explanation.reason}`);
  if (explanation?.activeWait) lines.push(`Active wait: ${explanation.activeWait.kind} ${explanation.activeWait.name}`);
  if (explanation?.lease) {
    lines.push(
      `Lease: worker ${explanation.lease.workerName ?? explanation.lease.workerId}, expires ${explanation.lease.expiresAtUtc ?? 'unknown'}, expired=${explanation.lease.expired}`
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

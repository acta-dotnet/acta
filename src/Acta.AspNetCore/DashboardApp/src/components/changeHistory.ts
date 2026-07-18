// Shared shape and merge for the per-entity "Change history" panels: several small event queries
// (one per relevant event code) collapse into one newest-first list.
export interface HistoryEvent {
  jobEventId: number;
  eventCode: string;
  createdAtUtc: string;
  actorCode: string;
  actorKey: string | null;
  reasonMessage: string | null;
}

export function mergeHistory(pages: HistoryEvent[][], limit = 20): HistoryEvent[] {
  const byId = new Map<number, HistoryEvent>();
  for (const page of pages) {
    for (const event of page) byId.set(event.jobEventId, event);
  }
  return [...byId.values()]
    .sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc) || b.jobEventId - a.jobEventId)
    .slice(0, limit);
}

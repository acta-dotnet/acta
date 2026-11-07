import type { EventItem } from '../api.ts';

// Shared shape and merge for the per-entity "Change history" panels: several small event queries
// (one per relevant event code) collapse into one newest-first list.
export type HistoryEvent = Pick<EventItem, 'eventId' | 'eventCode' | 'createdAtUtc' | 'message' | 'detailsText'>;

export function mergeHistory(pages: HistoryEvent[][], limit = 20): HistoryEvent[] {
  const byId = new Map<number, HistoryEvent>();
  for (const page of pages) {
    for (const event of page) byId.set(event.eventId, event);
  }
  return [...byId.values()]
    .sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc) || b.eventId - a.eventId)
    .slice(0, limit);
}

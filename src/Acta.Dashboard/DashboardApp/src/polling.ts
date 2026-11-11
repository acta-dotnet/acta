import { writable } from 'svelte/store';
import { LIST_REFETCH_MS } from './query.ts';

// Global "live updates paused" flag. The freshness indicator's pause toggle flips this; every polled
// grid reads it into its query options, so a single pause quiets the whole dashboard. Refetch-on-focus
// and manual refresh still work while paused - pausing only stops the background interval.
export const livePaused = writable(false);

export const DETAIL_REFETCH_MS = 5000;

export function detailRefetchInterval(active: boolean, paused: boolean): number | false {
  return active && !paused ? DETAIL_REFETCH_MS : false;
}

const LIST_JITTER_MS = 1500;

// The wait before a polled list refetches: the base cadence plus a little jitter, so a fleet of grids does not
// stampede the backend in lockstep, backing off exponentially while a query keeps failing (so a down backend is
// probed ~10s, 20s, 40s… rather than hammered every 10s). Capped at two minutes. The jitter is read off the data's
// own timestamp rather than rolled, so the freshness meter drains over exactly the interval the query waits.
export function listRefetchDelay(dataUpdatedAt: number, failures = 0): number {
  const base = failures > 0 ? Math.min(LIST_REFETCH_MS * 2 ** failures, 120_000) : LIST_REFETCH_MS;
  return base + (dataUpdatedAt % LIST_JITTER_MS);
}

export function listRefetchInterval(query: { state: { fetchFailureCount: number; dataUpdatedAt: number } }): number {
  return listRefetchDelay(query.state.dataUpdatedAt, query.state.fetchFailureCount);
}

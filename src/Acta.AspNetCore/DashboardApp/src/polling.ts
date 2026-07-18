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

// Refetch interval for a polled list: the base cadence plus a little jitter so a fleet of grids does
// not stampede the backend in lockstep, backing off exponentially while a query keeps failing (so a
// down backend is probed ~10s, 20s, 40s… rather than hammered every 10s). Capped at two minutes.
export function listRefetchInterval(query: { state: { fetchFailureCount: number } }): number {
  const fails = query.state.fetchFailureCount;
  const base = fails > 0 ? Math.min(LIST_REFETCH_MS * 2 ** fails, 120_000) : LIST_REFETCH_MS;
  return base + Math.floor(Math.random() * 1500);
}

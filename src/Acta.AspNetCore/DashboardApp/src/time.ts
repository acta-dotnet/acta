import { readable, writable, type Readable } from 'svelte/store';

// Ordinary relative timestamps only need minute precision. Components that explicitly show
// sub-minute freshness use secondNow; its timer exists only while at least one component subscribes.
const clock = writable(Date.now(), (set) => {
  set(Date.now());
  const id = setInterval(() => set(Date.now()), 60_000);
  return () => clearInterval(id);
});

export const now: Readable<number> = { subscribe: clock.subscribe };

export const secondNow: Readable<number> = readable(Date.now(), (set) => {
  set(Date.now());
  const id = setInterval(() => set(Date.now()), 1000);
  return () => clearInterval(id);
});

// Query responses can arrive more frequently than the minute tick. Advancing the shared clock when
// one arrives prevents freshly observed timestamps from being compared with an older local instant.
export function advanceNow(): void {
  clock.set(Date.now());
}

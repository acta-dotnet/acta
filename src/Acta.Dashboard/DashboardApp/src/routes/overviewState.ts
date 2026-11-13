// How far back the overview compares a value with itself, the least history it speaks from, and the least move it
// names: under ten, jobs or seconds, a value only jitters between two polls.
const WINDOW_MS = 5 * 60_000;
const MIN_SPAN_MS = 60_000;
const FLOOR = 10;

export type OverviewSample = { atMs: number; readyCount: number; oldestReadyAgeSeconds: number };

// The samples kept once `sample` joins them, oldest first: those of the five minutes before it. After a longer gap
// the new sample stands alone, so the history starts over.
export function keepSamples(samples: OverviewSample[], sample: OverviewSample): OverviewSample[] {
  return [...samples.filter((kept) => kept.atMs < sample.atMs && sample.atMs - kept.atMs <= WINDOW_MS), sample];
}

// Which way a value moved between the oldest sample kept and the newest: the arrow that takes its card's icon, and
// the words for the card's hint. The words name the span as it was, so a pause never reads as five minutes.
export function direction(
  samples: OverviewSample[],
  value: 'readyCount' | 'oldestReadyAgeSeconds',
  format: (amount: number) => string
): { icon: 'arrow-down' | 'arrow-up'; text: string } | null {
  if (samples.length < 2) return null;
  const first = samples[0];
  const last = samples[samples.length - 1];
  const delta = last[value] - first[value];
  const spanMs = last.atMs - first.atMs;
  if (spanMs < MIN_SPAN_MS || Math.abs(delta) < FLOOR) return null;
  const minutes = Math.round(spanMs / 60_000);
  return {
    icon: delta < 0 ? 'arrow-down' : 'arrow-up',
    text: `${delta < 0 ? 'Down' : 'Up'} ${format(Math.abs(delta))} in ${minutes} ${minutes === 1 ? 'minute' : 'minutes'}`
  };
}

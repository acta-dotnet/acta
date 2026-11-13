import type { DefinitionPerformance, DefinitionPerformanceBin, DefinitionPerformanceExecutions } from '../../api.ts';
import { displayFormatter } from '../../format.ts';

export type PerformanceWindow = 'hour' | 'day' | 'week' | 'month';
export type Outcome = 'succeeded' | 'failed';

// The outcomes the panel tells apart, each by the word that names it in a heading or a choice.
export const OUTCOMES: Record<Outcome, string> = { succeeded: 'Succeeded', failed: 'Failed' };

// The windows the panel offers: how many intervals of how long, and every how many intervals the time axis names one.
export const WINDOWS: Record<PerformanceWindow, { label: string; intervals: number; intervalSeconds: number; tickEvery: number }> = {
  hour: { label: 'Hour', intervals: 60, intervalSeconds: 60, tickEvery: 15 },
  day: { label: 'Day', intervals: 48, intervalSeconds: 1800, tickEvery: 12 },
  week: { label: 'Week', intervals: 56, intervalSeconds: 10_800, tickEvery: 8 },
  month: { label: 'Month', intervals: 60, intervalSeconds: 43_200, tickEvery: 10 }
};

// The percentiles the panel shows, and the member of a read that holds each.
export const PERCENTILES = [
  { name: 'p50', key: 'p50Ms' },
  { name: 'p95', key: 'p95Ms' },
  { name: 'p99', key: 'p99Ms' }
] as const;

// Under this many executions of the shown outcome, p99 is simply the slowest one, so the trend draws every interval on
// its own instead of joining intervals of one or two executions into lines.
const FEW_EXECUTIONS = 100;

// Durations the axes name: the 1-2-5 series in each unit the duration form uses, so every tick is a round number of it.
const TICK_MS = [
  0, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1_000, 2_000, 5_000, 10_000, 20_000, 50_000, 60_000, 120_000, 300_000, 600_000,
  1_200_000, 3_000_000, 3_600_000, 7_200_000, 18_000_000, 36_000_000, 72_000_000, 86_400_000, 172_800_000, 432_000_000,
  864_000_000, 1_728_000_000
];

// The range a window reads: whole intervals ending at the first interval boundary after now, so every read of a window
// cuts its intervals at the same instants.
export function windowRange(window: PerformanceWindow, nowMs: number): { fromUtc: string; toUtc: string; intervalSeconds: number } {
  const { intervals, intervalSeconds } = WINDOWS[window];
  const step = intervalSeconds * 1000;
  const toMs = (Math.floor(nowMs / step) + 1) * step;
  return { fromUtc: new Date(toMs - intervals * step).toISOString(), toUtc: new Date(toMs).toISOString(), intervalSeconds };
}

// The window a read was made for, known by its interval, which no two windows share.
export function windowOf(read: DefinitionPerformance): PerformanceWindow {
  return (Object.keys(WINDOWS) as PerformanceWindow[]).find((window) => WINDOWS[window].intervalSeconds === read.intervalSeconds)!;
}

// The duration scale both charts share, counted in bins: one to each millisecond below 10 ms, then ten to a decade.
// That lays the server's bins side by side at nearly equal width and gives a zero-millisecond execution a place.
export function scaleAt(ms: number): number {
  return ms < 10 ? ms : 10 + 10 * Math.log10(ms / 10);
}

// Where a recorded duration sits on the scale. One below 10 ms is a whole millisecond that stands for its bin, so it
// sits mid-bin.
export function durationAt(ms: number): number {
  return ms < 10 ? ms + 0.5 : scaleAt(ms);
}

// A tick's label. A tick is a round number of its unit, so the duration form's empty remainder ("1m 0s") is left off.
export function tickLabel(ms: number): string {
  return ms === 0 ? '0' : displayFormatter.durationMs(ms).replace(/ 0[smh]$/, '');
}

// A bin as the readout and the Values table name it.
export function binLabel(bin: DefinitionPerformanceBin): string {
  if (bin.fromMs === 0) return 'Under 1 ms';
  if (bin.fromMs < 10) return displayFormatter.durationMs(bin.fromMs);
  return `${displayFormatter.durationMs(bin.fromMs)} to ${displayFormatter.durationMs(bin.toMs)}`;
}

// The top of a count axis: the first value of the 1-2-5 series at or above the count.
export function countCeiling(count: number): number {
  let power = 1;
  while (power * 10 <= count) power *= 10;
  return (count <= power ? 1 : count <= 2 * power ? 2 : count <= 5 * power ? 5 : 10) * power;
}

// Labels at one place on an axis merge into one that names them all, so none is drawn over another. Positions are in
// percent, in the order given, which for percentiles is ascending: they never cross, but they can coincide.
export function mergeLabels(items: { name: string; at: number }[], gap: number): { text: string; at: number }[] {
  const groups: { names: string[]; positions: number[] }[] = [];
  for (const item of items) {
    const last = groups[groups.length - 1];
    if (last && item.at - last.positions[last.positions.length - 1] < gap) {
      last.names.push(item.name);
      last.positions.push(item.at);
    } else {
      groups.push({ names: [item.name], positions: [item.at] });
    }
  }
  return groups.map((group) => ({
    text: group.names.join(', '),
    at: group.positions.reduce((sum, at) => sum + at, 0) / group.positions.length
  }));
}

interface Histogram {
  bars: { bin: DefinitionPerformanceBin; left: number; width: number; height: number }[];
  // The count axis's top, and the bar the readout names when nothing is pointed at: the most common duration.
  top: number;
  modal: number;
  // A tick at an end of the axis is drawn inward, so its label stays over the plot.
  ticks: { label: string; at: number; edge: 'first' | 'last' | ''; alt: boolean }[];
  // A duration's place on the plot, in percent from its left edge.
  at(ms: number): number;
}

// The histogram's geometry, in percent of its plot. A bin of margin stands on each side of the bins, and the plot is
// at least twelve bins wide, so a single bin never fills it.
export function histogram(bins: DefinitionPerformanceBin[]): Histogram {
  const lo = Math.max(0, scaleAt(bins[0].fromMs) - 1);
  const hi = Math.max(scaleAt(bins[bins.length - 1].toMs) + 1, lo + 12);
  const x = (position: number) => ((position - lo) / (hi - lo)) * 100;
  const counts = bins.map((bin) => bin.executionCount);
  const top = countCeiling(Math.max(...counts));

  // Ticks closer than a ninth of the plot would touch; every other one is marked, for a narrow plot to drop.
  const ticks: Histogram['ticks'] = [];
  for (const ms of TICK_MS) {
    const at = x(scaleAt(ms));
    if (at < 0 || at > 100 || (ticks.length > 0 && at - ticks[ticks.length - 1].at < 11)) continue;
    ticks.push({ label: tickLabel(ms), at, edge: at < 3 ? 'first' : at > 97 ? 'last' : '', alt: ticks.length % 2 === 1 });
  }

  return {
    bars: bins.map((bin) => ({
      bin,
      left: x(scaleAt(bin.fromMs)),
      width: x(scaleAt(bin.toMs)) - x(scaleAt(bin.fromMs)),
      height: (bin.executionCount / top) * 100
    })),
    top,
    modal: counts.indexOf(Math.max(...counts)),
    ticks,
    at: (ms) => x(durationAt(ms))
  };
}

// The last interval a capped read did not count whole: every one that starts at or before the oldest execution it
// counted, the one the cut ran through included. -1 when the read was not capped.
export function notReadThrough(read: DefinitionPerformance): number {
  if (!read.executionsHasMore) return -1;
  const oldest = Date.parse(read.oldestAtUtc!);
  const firstRead = read.intervals.findIndex((interval) => Date.parse(interval.startAtUtc) > oldest);
  return (firstRead < 0 ? read.intervals.length : firstRead) - 1;
}

// What each interval of the trend draws: the outcome's executions, or null where it had none or was not read.
export function trendPoints(read: DefinitionPerformance, outcome: Outcome): (DefinitionPerformanceExecutions | null)[] {
  const through = notReadThrough(read);
  return read.intervals.map((interval, index) =>
    index <= through || interval[outcome].executionCount === 0 ? null : interval[outcome]
  );
}

// Lines read as a trend only with enough executions behind them; fewer stand as range marks, one per interval.
export function trendForm(executionCount: number): 'lines' | 'marks' {
  return executionCount < FEW_EXECUTIONS ? 'marks' : 'lines';
}

interface TrendScale {
  ticks: { label: string; at: number }[];
  // A duration's height on the plot, in percent from its floor.
  at(ms: number): number;
}

// The trend's duration axis: from a bin under the fastest p50 to a bin over the slowest p99, and at least five bins
// tall, so a constant duration has room above and below it. The whole range's percentiles count too, so a read whose
// executions all fell where it was cut still has an axis.
export function trendScale(points: (DefinitionPerformanceExecutions | null)[], whole: DefinitionPerformanceExecutions): TrendScale {
  const shown = [whole, ...points].filter((point): point is DefinitionPerformanceExecutions => point?.p50Ms != null);
  let lo = Math.max(0, Math.min(...shown.map((point) => durationAt(point.p50Ms!))) - 1);
  let hi = Math.max(...shown.map((point) => durationAt(point.p99Ms!))) + 1;
  if (hi - lo < 5) {
    lo = Math.max(0, (hi + lo) / 2 - 2.5);
    hi = lo + 5;
  }
  const y = (position: number) => ((position - lo) / (hi - lo)) * 100;
  let values = TICK_MS.filter((ms) => (ms === 0 ? lo === 0 : scaleAt(ms) >= lo && scaleAt(ms) <= hi));
  // A tall axis keeps the ticks that start a unit's decade.
  if (values.length > 6) values = values.filter((ms) => tickLabel(ms).startsWith('1'));
  return {
    ticks: values.map((ms) => ({ label: tickLabel(ms), at: y(scaleAt(ms)) })),
    at: (ms) => y(durationAt(ms))
  };
}

// The trend's lines as paths in a 100 by 100 box that the plot stretches, one segment to each run of neighbouring
// intervals, with the wash between p50 and p99. An interval with no neighbour has no line to join, so it stands as a
// dot.
export function trendLines(
  points: (DefinitionPerformanceExecutions | null)[],
  at: (ms: number) => number
): { p50: string; p95: string; p99: string; band: string; dots: number[] } {
  const x = (index: number) => (((index + 0.5) / points.length) * 100).toFixed(2);
  const y = (ms: number) => (100 - at(ms)).toFixed(2);
  const runs: number[][] = [];
  points.forEach((point, index) => {
    if (!point) return;
    const last = runs[runs.length - 1];
    if (last && last[last.length - 1] === index - 1) last.push(index);
    else runs.push([index]);
  });
  const path = (run: number[], key: 'p50Ms' | 'p95Ms' | 'p99Ms') =>
    run.map((index, k) => `${k ? 'L' : 'M'}${x(index)} ${y(points[index]![key]!)}`).join(' ');
  const joined = runs.filter((run) => run.length > 1);
  return {
    p50: joined.map((run) => path(run, 'p50Ms')).join(' '),
    p95: joined.map((run) => path(run, 'p95Ms')).join(' '),
    p99: joined.map((run) => path(run, 'p99Ms')).join(' '),
    band: joined
      .map((run) => `${path(run, 'p99Ms')} ${[...run].reverse().map((index) => `L${x(index)} ${y(points[index]!.p50Ms!)}`).join(' ')} Z`)
      .join(' '),
    dots: runs.filter((run) => run.length === 1).map((run) => run[0])
  };
}

// One interval drawn as a range mark: a dot at p50 and ticks at p95 and p99 on a stem from p50 to p99, heights in
// percent. A tick that would sit on the dot or on the other tick is left out, as is the stem of a single duration.
export function rangeMark(point: DefinitionPerformanceExecutions, at: (ms: number) => number): { p50: number; p95: number | null; p99: number | null } {
  const p50 = at(point.p50Ms!);
  const p99 = at(point.p99Ms!);
  return {
    p50,
    p95: point.p95Ms === point.p50Ms || point.p95Ms === point.p99Ms ? null : at(point.p95Ms!),
    p99: p99 > p50 ? p99 : null
  };
}

// The sentence for a sample too small for its upper percentiles. By nearest rank, p95 is the slowest of fewer than
// twenty executions and p99 the slowest of fewer than a hundred.
export function smallSampleNote(executionCount: number, outcome: Outcome): string | null {
  if (executionCount === 0 || executionCount >= FEW_EXECUTIONS) return null;
  if (executionCount === 1) return `One ${outcome} execution: p50, p95 and p99 are all its duration.`;
  return executionCount < 20
    ? `With ${executionCount} ${outcome} executions, p95 and p99 are both the slowest one.`
    : `With ${executionCount} ${outcome} executions, p99 is the slowest one.`;
}

// The forms the time axis names an instant in: the time of day, and the day as a week's axis and a month's name it.
const TIME_FORMS = {
  clock: { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' },
  week: { weekday: 'short' },
  month: { day: 'numeric', month: 'short' }
} satisfies Record<string, Intl.DateTimeFormatOptions>;

// One formatter to each form and zone: a Values table names every interval, and building a formatter costs far more
// than using one.
const timeFormatters = new Map<string, Intl.DateTimeFormat>();

function timeLabel(form: keyof typeof TIME_FORMS, atMs: number, timeZone: string): string {
  const key = `${form} ${timeZone}`;
  let formatter = timeFormatters.get(key);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat('en-GB', { timeZone, ...TIME_FORMS[form] });
    timeFormatters.set(key, formatter);
  }
  return formatter.format(atMs);
}

// The form a window's axis names a day in. An hour's and a day's name none.
const dayForm = (window: PerformanceWindow) => (window === 'week' || window === 'month' ? window : null);

// A tick on the time axis: the day on a week's or a month's axis, the time of day on a shorter one.
export function axisLabel(atUtc: string, window: PerformanceWindow, timeZone = displayFormatter.localTimeZone): string {
  return timeLabel(dayForm(window) ?? 'clock', Date.parse(atUtc), timeZone);
}

// An instant in the words of its window's time axis: the time of day, after the weekday on a week and the date on a
// month.
export function instantLabel(atUtc: string, window: PerformanceWindow, timeZone = displayFormatter.localTimeZone): string {
  const atMs = Date.parse(atUtc);
  const day = dayForm(window);
  return (day ? timeLabel(day, atMs, timeZone) + ' ' : '') + timeLabel('clock', atMs, timeZone);
}

// An interval, from its start to its end.
export function intervalLabel(startAtUtc: string, window: PerformanceWindow, timeZone = displayFormatter.localTimeZone): string {
  const endMs = Date.parse(startAtUtc) + WINDOWS[window].intervalSeconds * 1000;
  return `${instantLabel(startAtUtc, window, timeZone)} to ${timeLabel('clock', endMs, timeZone)}`;
}

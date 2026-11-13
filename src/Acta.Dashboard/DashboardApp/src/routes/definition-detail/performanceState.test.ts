import assert from 'node:assert/strict';
import test from 'node:test';
import type { DefinitionPerformance, DefinitionPerformanceExecutions, EventItem } from '../../api.ts';
import {
  axisLabel,
  binLabel,
  countCeiling,
  deploymentMarkers,
  durationAt,
  histogram,
  instantLabel,
  intervalLabel,
  mergeLabels,
  notReadThrough,
  rangeMark,
  scaleAt,
  smallSampleNote,
  tickLabel,
  trendForm,
  trendLines,
  trendPoints,
  trendScale,
  windowOf,
  windowRange
} from './performanceState.ts';

const none: DefinitionPerformanceExecutions = { executionCount: 0, p50Ms: null, p95Ms: null, p99Ms: null };
const executions = (executionCount: number, p50Ms: number, p95Ms = p50Ms, p99Ms = p95Ms): DefinitionPerformanceExecutions => ({
  executionCount,
  p50Ms,
  p95Ms,
  p99Ms
});

// A day's read whose intervals hold the succeeded executions given, oldest first; null is an interval with none.
function dayRead(succeeded: (DefinitionPerformanceExecutions | null)[], capped: { oldestAtUtc: string } | null = null): DefinitionPerformance {
  const fromMs = Date.parse('2026-11-12T10:30:00Z');
  return {
    fromUtc: '2026-11-12T10:30:00Z',
    toUtc: new Date(fromMs + succeeded.length * 1_800_000).toISOString(),
    intervalSeconds: 1800,
    executionsHasMore: capped !== null,
    oldestAtUtc: capped?.oldestAtUtc ?? null,
    succeeded: none,
    failed: none,
    succeededBins: [],
    failedBins: [],
    intervals: succeeded.map((interval, index) => ({
      startAtUtc: new Date(fromMs + index * 1_800_000).toISOString(),
      succeeded: interval ?? none,
      failed: none
    }))
  };
}

test('a window reads whole intervals up to the first interval boundary after now', () => {
  assert.deepEqual(windowRange('day', Date.parse('2026-11-13T10:17:23Z')), {
    fromUtc: '2026-11-12T10:30:00.000Z',
    toUtc: '2026-11-13T10:30:00.000Z',
    intervalSeconds: 1800
  });
  // A now on a boundary is inside the interval that starts there.
  assert.equal(windowRange('hour', Date.parse('2026-11-13T10:17:00Z')).toUtc, '2026-11-13T10:18:00.000Z');
  for (const window of ['hour', 'day', 'week', 'month'] as const) {
    const range = windowRange(window, Date.parse('2026-11-13T10:17:23Z'));
    const intervals = (Date.parse(range.toUtc) - Date.parse(range.fromUtc)) / (range.intervalSeconds * 1000);
    assert.ok(intervals <= 60 && Number.isInteger(intervals), window);
    assert.equal(windowOf({ ...dayRead([]), intervalSeconds: range.intervalSeconds }), window);
  }
});

test('the duration scale is linear to 10 ms and a bin a tenth of a decade above it', () => {
  assert.equal(scaleAt(0), 0);
  assert.equal(scaleAt(10), 10);
  assert.equal(scaleAt(100), 20);
  assert.equal(scaleAt(10_000), 40);
  // A whole millisecond below 10 ms sits mid-bin; above it a duration sits where the scale puts it.
  assert.equal(durationAt(0), 0.5);
  assert.equal(durationAt(9), 9.5);
  assert.equal(durationAt(100), 20);
  // The server's bins lie side by side at nearly equal width.
  const edges = [10, 13, 16, 20, 25, 32, 40, 50, 63, 80, 100];
  edges.forEach((edge, index) => assert.ok(Math.abs(scaleAt(edge) - (10 + index)) < 0.15, String(edge)));
});

test('ticks and bins read in round units', () => {
  assert.equal(tickLabel(0), '0');
  assert.equal(tickLabel(500), '500 ms');
  assert.equal(tickLabel(2000), '2 s');
  assert.equal(tickLabel(60_000), '1m');
  assert.equal(tickLabel(3_600_000), '1h');
  assert.equal(binLabel({ fromMs: 0, toMs: 1, executionCount: 3 }), 'Under 1 ms');
  assert.equal(binLabel({ fromMs: 4, toMs: 5, executionCount: 3 }), '4 ms');
  assert.equal(binLabel({ fromMs: 200, toMs: 250, executionCount: 3 }), '200 ms to 250 ms');
  assert.equal(binLabel({ fromMs: 1600, toMs: 2000, executionCount: 3 }), '1.6 s to 2 s');
});

test('a count axis tops out at the next 1-2-5 value', () => {
  assert.deepEqual([1, 2, 3, 7, 10, 11, 150, 500_000].map(countCeiling), [1, 2, 5, 10, 10, 20, 200, 500_000]);
});

test('labels at one place merge into one that names them all', () => {
  assert.deepEqual(
    mergeLabels([{ name: 'p50', at: 40 }, { name: 'p95', at: 40 }, { name: 'p99', at: 40 }], 7),
    [{ text: 'p50, p95, p99', at: 40 }]
  );
  assert.deepEqual(
    mergeLabels([{ name: 'p50', at: 10 }, { name: 'p95', at: 60 }, { name: 'p99', at: 64 }], 7),
    [{ text: 'p50', at: 10 }, { text: 'p95, p99', at: 62 }]
  );
});

test('a histogram of executions that all took under a millisecond starts at zero and draws one bar', () => {
  const chart = histogram([{ fromMs: 0, toMs: 1, executionCount: 5 }]);

  assert.equal(chart.bars.length, 1);
  assert.equal(chart.bars[0].left, 0);
  assert.equal(chart.bars[0].height, 100);
  assert.ok(Math.abs(chart.bars[0].width - 100 / 12) < 1e-9);
  assert.deepEqual(chart.ticks[0], { label: '0', at: 0, edge: 'first', alt: false });
  // The zero-millisecond percentiles sit inside the bar, not on the axis.
  assert.ok(chart.at(0) > 0 && chart.at(0) < chart.bars[0].width);
});

test('a histogram of one constant duration keeps a margin around its bar', () => {
  const chart = histogram([{ fromMs: 200, toMs: 250, executionCount: 40 }]);
  const bar = chart.bars[0];

  assert.equal(chart.modal, 0);
  assert.equal(chart.top, 50);
  assert.ok(bar.left > 0 && bar.left + bar.width < 100);
  assert.ok(chart.at(240) > bar.left && chart.at(240) < bar.left + bar.width);
  assert.ok(chart.ticks.every((tick) => tick.at >= 0 && tick.at <= 100 && Number.isFinite(tick.at)));
});

test('a histogram names its most common bin and keeps its ticks apart', () => {
  const chart = histogram([
    { fromMs: 100, toMs: 130, executionCount: 3 },
    { fromMs: 130, toMs: 160, executionCount: 0 },
    { fromMs: 160, toMs: 200, executionCount: 9 }
  ]);

  assert.equal(chart.modal, 2);
  assert.equal(chart.bars[1].height, 0);
  chart.ticks.slice(1).forEach((tick, index) => assert.ok(tick.at - chart.ticks[index].at >= 11));
  assert.deepEqual(chart.ticks.map((tick) => tick.alt), chart.ticks.map((_, index) => index % 2 === 1));
});

test('intervals with no executions break the trend, and one with no neighbour stands as a dot', () => {
  const read = dayRead([executions(5, 200, 240, 300), executions(5, 210, 250, 320), null, executions(1, 900), null, executions(4, 220)]);
  const points = trendPoints(read, 'succeeded');
  const scale = trendScale(points, executions(15, 210, 900, 900));
  const lines = trendLines(points, scale.at);

  assert.deepEqual(points.map((point) => point !== null), [true, true, false, true, false, true]);
  assert.equal(lines.p50.split('M').length - 1, 1);
  assert.deepEqual(lines.dots, [3, 5]);
  assert.match(lines.band, /Z$/);
  assert.equal(trendPoints(read, 'failed').every((point) => point === null), true);
});

test('a capped read leaves out every interval it did not count whole, the one its cut ran through included', () => {
  const read = dayRead([executions(9, 200), executions(9, 200), executions(9, 200), executions(9, 200)], {
    oldestAtUtc: '2026-11-12T11:30:00Z'
  });

  // The third interval starts at the oldest execution counted, so it may hold uncounted ones.
  assert.equal(notReadThrough(read), 2);
  assert.deepEqual(trendPoints(read, 'succeeded').map((point) => point !== null), [false, false, false, true]);
  assert.equal(notReadThrough({ ...read, executionsHasMore: false }), -1);
  assert.equal(notReadThrough({ ...read, oldestAtUtc: '2026-11-12T12:59:00Z' }), 3);
});

test('a trend of durations that are all zero has an axis from zero', () => {
  const read = dayRead([executions(3, 0), executions(2, 0)]);
  const scale = trendScale(trendPoints(read, 'succeeded'), executions(5, 0));

  assert.deepEqual(scale.ticks.map((tick) => tick.label), ['0', '1 ms', '2 ms', '5 ms']);
  assert.ok(scale.at(0) > 0 && scale.at(0) < 100);
});

test('a constant duration has room above and below it', () => {
  const scale = trendScale([executions(500, 240), executions(500, 240)], executions(1000, 240));

  assert.ok(scale.at(240) > 30 && scale.at(240) < 70);
  assert.ok(scale.ticks.length > 0 && scale.ticks.every((tick) => tick.at >= 0 && tick.at <= 100));
});

test('a read whose executions all fell where it was cut still has an axis', () => {
  const scale = trendScale([null, null], executions(500_000, 40, 90, 120));

  assert.ok(Number.isFinite(scale.at(40)) && scale.ticks.length > 0);
});

test('a tall axis keeps the ticks that start a decade of their unit', () => {
  const scale = trendScale([executions(500, 3, 5_000, 90_000)], executions(500, 3, 5_000, 90_000));

  assert.deepEqual(scale.ticks.map((tick) => tick.label), ['10 ms', '100 ms', '1 s', '10 s', '1m']);
});

test('fewer than a hundred executions draw range marks, a single execution a dot alone', () => {
  assert.equal(trendForm(1), 'marks');
  assert.equal(trendForm(99), 'marks');
  assert.equal(trendForm(100), 'lines');

  const at = (ms: number) => ms / 10;
  assert.deepEqual(rangeMark(executions(1, 240), at), { p50: 24, p95: null, p99: null });
  assert.deepEqual(rangeMark(executions(30, 240, 600, 900), at), { p50: 24, p95: 60, p99: 90 });
  assert.deepEqual(rangeMark(executions(10, 240, 900, 900), at), { p50: 24, p95: null, p99: 90 });
});

test('a small sample says which percentiles are the slowest execution', () => {
  assert.equal(smallSampleNote(0, 'failed'), null);
  assert.equal(smallSampleNote(1, 'failed'), 'One failed execution: p50, p95 and p99 are all its duration.');
  assert.equal(smallSampleNote(19, 'succeeded'), 'With 19 succeeded executions, p95 and p99 are both the slowest one.');
  assert.equal(smallSampleNote(20, 'succeeded'), 'With 20 succeeded executions, p99 is the slowest one.');
  assert.equal(smallSampleNote(100, 'succeeded'), null);
});

// A namespace.deployed event with the details a deploy writes; null is one that set no version.
const deployed = (createdAtUtc: string, details: Record<string, string> | null): EventItem => ({
  eventId: Date.parse(createdAtUtc),
  eventCode: 'namespace.deployed',
  createdAtUtc,
  jobNamespace: 'billing',
  entityKind: 'namespace',
  entityId: 1,
  jobName: null,
  jobRef: null,
  lineageRootJobRef: null,
  tenantName: null,
  workerRef: null,
  executionNumber: null,
  durationMs: null,
  message: null,
  actor: null,
  detailsText: details && JSON.stringify(details)
});
// A day from 2026-11-12T10:30Z, the range every deployment test reads.
const day = dayRead(Array(48).fill(null));

test('a deployment that changed the version is marked at its instant, oldest first', () => {
  // The older one 24 minutes into its interval, a tenth of the way through the day.
  const [older, newer] = ['2026-11-12T12:54:00Z', '2026-11-13T04:30:00Z'];
  const markers = deploymentMarkers(
    [
      deployed(newer, { deploymentVersion: '2.4.1', previousDeploymentVersion: '2.4.0' }),
      deployed(older, { deploymentVersion: '2.4.0', previousDeploymentVersion: '2.3.7' })
    ],
    day
  );

  assert.deepEqual(markers, [
    { eventId: Date.parse(older), atUtc: older, at: 10, interval: 4, version: '2.4.0', previous: '2.3.7', edge: 'start', room: 65 },
    { eventId: Date.parse(newer), atUtc: newer, at: 75, interval: 36, version: '2.4.1', previous: '2.4.0', edge: 'start', room: 25 }
  ]);
});

test('deployments at one instant are marked in the order of their ids', () => {
  const at = '2026-11-12T16:30:00Z';
  const markers = deploymentMarkers(
    [
      { ...deployed(at, { deploymentVersion: '2.4.1', previousDeploymentVersion: '2.4.0' }), eventId: 8 },
      { ...deployed(at, { deploymentVersion: '2.4.0', previousDeploymentVersion: '2.3.7' }), eventId: 7 }
    ],
    day
  );

  assert.deepEqual(markers.map(({ eventId, version }) => [eventId, version]), [[7, '2.4.0'], [8, '2.4.1']]);
});

test('a first deployment is marked, and the same version deployed again or a deploy with no version is not', () => {
  const markers = deploymentMarkers(
    [
      deployed('2026-11-12T12:00:00Z', { deploymentVersion: '1.0.0' }),
      deployed('2026-11-12T18:00:00Z', { deploymentVersion: '1.0.0', previousDeploymentVersion: '1.0.0' }),
      deployed('2026-11-12T20:00:00Z', null)
    ],
    day
  );

  assert.deepEqual(markers.map(({ version, previous }) => [version, previous]), [['1.0.0', null]]);
});

test('a deployment outside the range is not marked, one at its start is', () => {
  const change = { deploymentVersion: '2.4.1', previousDeploymentVersion: '2.4.0' };
  const markers = deploymentMarkers(
    [deployed('2026-11-12T10:29:59Z', change), deployed('2026-11-12T10:30:00Z', change), deployed('2026-11-13T10:30:00Z', change)],
    day
  );

  assert.deepEqual(markers.map(({ at, interval, edge }) => ({ at, interval, edge })), [{ at: 0, interval: 0, edge: 'start' }]);
});

test('versions take their room from the newest back, and one with too little is left to the readout and the table', () => {
  const change = { deploymentVersion: '2.4.1', previousDeploymentVersion: '2.4.0' };
  // At 50%, 55%, 70%, 90% and 98% of the day.
  const markers = deploymentMarkers(
    ['2026-11-12T22:30:00Z', '2026-11-12T23:42:00Z', '2026-11-13T03:18:00Z', '2026-11-13T08:06:00Z', '2026-11-13T10:01:12Z'].map((at) =>
      deployed(at, change)
    ),
    day
  );

  // The newest ends at its hairline and takes the room before it, which the one at 90% would need. Each older one
  // runs to the hairline after it, and the oldest has too little room before the next.
  assert.deepEqual(markers.map(({ edge }) => edge), ['start', 'start', 'start', 'end', 'end']);
  assert.deepEqual(markers.map(({ room }) => Math.round(room)), [0, 15, 12, 0, 16]);
});

test('times read as the window\'s axis names them', () => {
  assert.equal(axisLabel('2026-11-13T09:00:00Z', 'day', 'UTC'), '09:00');
  assert.equal(axisLabel('2026-11-13T09:00:00Z', 'week', 'UTC'), 'Fri');
  assert.equal(axisLabel('2026-11-13T09:00:00Z', 'month', 'UTC'), '13 Nov');
  assert.equal(intervalLabel('2026-11-13T09:00:00Z', 'day', 'UTC'), '09:00 to 09:30');
  assert.equal(intervalLabel('2026-11-13T09:00:00Z', 'week', 'UTC'), 'Fri 09:00 to 12:00');
  assert.equal(intervalLabel('2026-11-13T12:00:00Z', 'month', 'UTC'), '13 Nov 12:00 to 00:00');
  assert.equal(instantLabel('2026-11-13T09:41:07Z', 'hour', 'Europe/Ljubljana'), '10:41');
});

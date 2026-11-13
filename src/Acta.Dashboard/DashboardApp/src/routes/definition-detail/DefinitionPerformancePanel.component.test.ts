import { fireEvent, render, screen } from '@testing-library/svelte';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import DefinitionPerformancePanelHarness from '../../test/DefinitionPerformancePanelHarness.svelte';
import type { DefinitionPerformance, DefinitionPerformanceExecutions, EventItem } from '../../api.ts';

const none: DefinitionPerformanceExecutions = { executionCount: 0, p50Ms: null, p95Ms: null, p99Ms: null };
const executions = (executionCount: number, p50Ms: number, p95Ms = p50Ms, p99Ms = p95Ms): DefinitionPerformanceExecutions => ({
  executionCount,
  p50Ms,
  p95Ms,
  p99Ms
});

type Read = Omit<DefinitionPerformance, 'fromUtc' | 'toUtc' | 'intervalSeconds' | 'intervals'> & {
  each: (index: number) => { succeeded: DefinitionPerformanceExecutions; failed: DefinitionPerformanceExecutions };
};

// A day of a busy definition: 25 successes in every interval and a failure in every fourth.
const typical: Read = {
  executionsHasMore: false,
  oldestAtUtc: null,
  succeeded: executions(1200, 240, 310, 390),
  failed: executions(12, 35, 48),
  succeededBins: [
    { fromMs: 200, toMs: 250, executionCount: 1000 },
    { fromMs: 250, toMs: 320, executionCount: 150 },
    { fromMs: 320, toMs: 400, executionCount: 50 }
  ],
  failedBins: [
    { fromMs: 32, toMs: 40, executionCount: 8 },
    { fromMs: 40, toMs: 50, executionCount: 4 }
  ],
  each: (index) => ({ succeeded: executions(25, 240, 310, 390), failed: index % 4 === 0 ? executions(1, 35) : none })
};

const empty: Read = {
  executionsHasMore: false,
  oldestAtUtc: null,
  succeeded: none,
  failed: none,
  succeededBins: [],
  failedBins: [],
  each: () => ({ succeeded: none, failed: none })
};

// The read the server answers for the range the panel asked for, every interval of it filled by the fixture.
function answer(url: URL, read: Read): DefinitionPerformance {
  const fromMs = Date.parse(url.searchParams.get('fromUtc')!);
  const toMs = Date.parse(url.searchParams.get('toUtc')!);
  const intervalSeconds = Number(url.searchParams.get('intervalSeconds'));
  const { each, ...rest } = read;
  return {
    fromUtc: new Date(fromMs).toISOString(),
    toUtc: new Date(toMs).toISOString(),
    intervalSeconds,
    ...rest,
    intervals: Array.from({ length: (toMs - fromMs) / (intervalSeconds * 1000) }, (_, index) => ({
      startAtUtc: new Date(fromMs + index * intervalSeconds * 1000).toISOString(),
      ...each(index)
    }))
  };
}

const page = (items: EventItem[], hasMore = false) =>
  new Response(JSON.stringify({ items, nextCursor: hasMore ? 'next' : null, hasMore, pageSize: 100, totalCount: null }), { status: 200 });

// A namespace.deployed event, the given time after the start of the range the deployments read asked for.
const deployed = (url: URL, afterMs: number, deploymentVersion: string, previousDeploymentVersion: string): EventItem => ({
  eventId: afterMs,
  eventCode: 'namespace.deployed',
  createdAtUtc: new Date(Date.parse(url.searchParams.get('createdFromUtc')!) + afterMs).toISOString(),
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
  detailsText: JSON.stringify({ deploymentVersion, previousDeploymentVersion })
});

// Answers the performance read with the fixture, and the deployments read with what `deployments` gives; the URLs it
// returns are the performance reads'.
function serve(read: Read | ((url: URL) => Read), deployments: (url: URL) => Response = () => page([])): URL[] {
  const calls: URL[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(input as string | URL);
      if (url.pathname.endsWith('/events')) return deployments(url);
      calls.push(url);
      return new Response(JSON.stringify(answer(url, typeof read === 'function' ? read(url) : read)), { status: 200 });
    })
  );
  return calls;
}

const fact = (label: string) => screen.getByText(label, { selector: 'dt' }).nextElementSibling?.textContent;
const intervals = (url: URL) =>
  (Date.parse(url.searchParams.get('toUtc')!) - Date.parse(url.searchParams.get('fromUtc')!)) /
  (Number(url.searchParams.get('intervalSeconds')) * 1000);

describe('DefinitionPerformancePanel', () => {
  beforeEach(() => {
    location.hash = '#/definitions/billing/send-invoice?ns=billing';
  });

  it('reads a day by default and shows its facts, both charts and their values', async () => {
    const calls = serve(typical);
    render(DefinitionPerformancePanelHarness, { eventRecording: 'audit' });

    await screen.findByText('Failure rate');
    expect(calls[0].pathname).toMatch(/\/api\/v1\/definitions\/billing\/send-invoice\/performance$/);
    expect(calls[0].searchParams.get('intervalSeconds')).toBe('1800');
    expect(intervals(calls[0])).toBe(48);
    expect(Date.parse(calls[0].searchParams.get('toUtc')!)).toBeGreaterThan(Date.now());
    expect(fact('Executions')).toBe('1212');
    expect(fact('Failure rate')).toBe('0.9%');
    expect(fact('Succeeded p50')).toBe('240 ms');
    expect(fact('Succeeded p99')).toBe('390 ms');
    expect(screen.getByRole('img', { name: /^Histogram of succeeded execution durations: 1200 executions from 200 ms to 400 ms, p50 240 ms/ })).toBeTruthy();
    expect(screen.getByRole('img', { name: /^Trend of succeeded execution durations over the last day in 48 intervals/ })).toBeTruthy();
    // A thousand successes and more draw the trend as lines.
    expect(screen.queryByText('p95 and, above it, p99')).toBeNull();
    const table = screen.getByText('Succeeded execution durations per interval').closest('table')!;
    expect(table.querySelectorAll('tbody tr')).toHaveLength(48);
    expect(screen.queryByText(/records failures only/)).toBeNull();
  });

  it('says a window with no finished executions is empty and draws no chart', async () => {
    serve(empty);
    render(DefinitionPerformancePanelHarness, { eventRecording: 'audit' });

    expect(await screen.findByText('No finished executions in this window.')).toBeTruthy();
    expect(screen.queryByText('Executions', { selector: 'dt' })).toBeNull();
    expect(screen.queryByRole('img')).toBeNull();
  });

  it('shows failures first for a definition that records only them, with no failure rate', async () => {
    serve({ ...typical, succeeded: executions(3, 120), succeededBins: [{ fromMs: 100, toMs: 130, executionCount: 3 }] });
    render(DefinitionPerformancePanelHarness, { eventRecording: 'failures' });

    await screen.findByText('Recorded executions');
    expect(fact('Recorded executions')).toBe('15');
    expect(fact('Failed')).toBe('12');
    expect(fact('Succeeded')).toBe('3');
    expect(fact('Failed p50')).toBe('35 ms');
    expect(screen.queryByText('Failure rate')).toBeNull();
    expect(screen.getByText('This definition records failures only.')).toBeTruthy();
    expect((screen.getByRole('radio', { name: 'Failed' }) as HTMLInputElement).checked).toBe(true);
    expect(screen.getByText('With 12 failed executions, p95 and p99 are both the slowest one.')).toBeTruthy();
  });

  it('names what a capped read counted and shows the intervals it did not reach as not read', async () => {
    serve((url) => ({
      ...typical,
      executionsHasMore: true,
      // Inside the eleventh interval: it and the ten before it were not counted whole.
      oldestAtUtc: new Date(Date.parse(url.searchParams.get('fromUtc')!) + 10 * 1_800_000 + 300_000).toISOString()
    }));
    const { container } = render(DefinitionPerformancePanelHarness, { eventRecording: 'audit' });

    expect(await screen.findByText('the newest in this window')).toBeTruthy();
    expect(screen.getByText(/^Showing the newest 1212 executions, back to \d\d:\d\d\.$/)).toBeTruthy();
    expect(container.querySelectorAll('td[colspan="5"]')).toHaveLength(11);
    expect(container.querySelector('.not-read')?.textContent).toBe('Not read');
  });

  it('offers a retry when the read fails, and shows the read once it answers', async () => {
    let failing = true;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = new URL(input as string | URL);
        if (url.pathname.endsWith('/events')) return page([]);
        if (failing) {
          failing = false;
          return new Response(JSON.stringify({ title: 'Service unavailable.', detail: 'The database is unreachable.' }), { status: 503 });
        }
        return new Response(JSON.stringify(answer(url, typical)), { status: 200 });
      })
    );
    render(DefinitionPerformancePanelHarness, { eventRecording: 'audit' });

    expect((await screen.findByRole('alert')).textContent).toContain('The database is unreachable.');
    await fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('Failure rate')).toBeTruthy();
  });

  it('switches the percentiles, the histogram and the trend to the other outcome without reading again', async () => {
    const calls = serve(typical);
    render(DefinitionPerformancePanelHarness, { eventRecording: 'audit' });
    await screen.findByText('Succeeded p50');

    await fireEvent.click(screen.getByRole('radio', { name: 'Failed' }));

    expect(fact('Failed p50')).toBe('35 ms');
    expect(screen.queryByText('Succeeded p50')).toBeNull();
    expect(screen.getByRole('img', { name: /^Histogram of failed execution durations: 12 executions/ })).toBeTruthy();
    // Twelve failures draw each interval on its own.
    expect(screen.getByText('p95 and, above it, p99')).toBeTruthy();
    expect(calls).toHaveLength(1);
  });

  it('marks the deployments that changed the version over the read\'s range and lists them in the values', async () => {
    const calls = serve(typical, (url) =>
      page([
        // The same version again, then a change in the latest interval, which the readout names at rest.
        deployed(url, 10 * 3_600_000, '2.4.1', '2.4.1'),
        deployed(url, 47 * 1_800_000 + 600_000, '2.4.1', '2.4.0')
      ])
    );
    const { container } = render(DefinitionPerformancePanelHarness, { eventRecording: 'audit' });

    await screen.findByText('Deployments in this window');
    const query = new URL(String(vi.mocked(fetch).mock.calls.find(([input]) => String(input).includes('/events'))![0])).searchParams;
    expect(Object.fromEntries(query)).toEqual({
      jobNamespace: 'billing',
      eventCode: 'namespace.deployed',
      createdFromUtc: new Date(Date.parse(calls[0].searchParams.get('fromUtc')!)).toISOString(),
      createdToUtc: new Date(Date.parse(calls[0].searchParams.get('toUtc')!)).toISOString(),
      pageSize: '100'
    });
    // A hairline over the plot and one over the count strip, and its version above the plot.
    expect(container.querySelectorAll('.deploy')).toHaveLength(2);
    expect(container.querySelector('.deploy-name')?.textContent).toBe('2.4.1');
    expect(screen.getByText('deployment', { selector: 'li' })).toBeTruthy();
    expect(container.querySelector('.version')?.parentElement?.textContent).toContain('2.4.1 deployed.');
    expect(screen.getByRole('img', { name: /1 deployment is marked/ })).toBeTruthy();
    const rows = screen.getByText('Deployments in this window').closest('table')!.querySelectorAll('tbody tr');
    expect([...rows].map((row) => [...row.querySelectorAll('td')].slice(1).map((cell) => cell.textContent))).toEqual([['2.4.1', '2.4.0']]);
  });

  it('says when the window holds more deployments than it read', async () => {
    serve(typical, (url) => page([deployed(url, 3_600_000, '2.4.1', '2.4.0')], true));
    render(DefinitionPerformancePanelHarness, { eventRecording: 'audit' });

    expect(await screen.findByText('Deployments before the newest 100 in this window are not marked.')).toBeTruthy();
  });

  it('keeps the charts standing when the deployments cannot be read, and says why', async () => {
    serve(typical, () => new Response(JSON.stringify({ title: 'Service unavailable.', detail: 'The database is unreachable.' }), { status: 503 }));
    render(DefinitionPerformancePanelHarness, { eventRecording: 'audit' });

    expect(await screen.findByText('Deployments could not be read: The database is unreachable.')).toBeTruthy();
    expect(screen.getByRole('img', { name: /^Histogram of succeeded/ })).toBeTruthy();
    expect(screen.getByRole('img', { name: /^Trend of succeeded/ })).toBeTruthy();
    expect(screen.queryByText('Deployments in this window')).toBeNull();
  });

  it('keeps the window in the URL and reads the range it names', async () => {
    location.hash = '#/definitions/billing/send-invoice?ns=billing&window=week';
    const calls = serve(typical);
    render(DefinitionPerformancePanelHarness, { eventRecording: 'audit' });

    await screen.findByText('Failure rate');
    expect(calls[0].searchParams.get('intervalSeconds')).toBe('10800');
    expect(intervals(calls[0])).toBe(56);
    expect((screen.getByRole('radio', { name: 'Week' }) as HTMLInputElement).checked).toBe(true);

    await fireEvent.click(screen.getByRole('radio', { name: 'Hour' }));
    await vi.waitFor(() => expect(calls.at(-1)?.searchParams.get('intervalSeconds')).toBe('60'));
    expect(location.hash).toBe('#/definitions/billing/send-invoice?ns=billing&window=hour');
    expect(intervals(calls.at(-1)!)).toBe(60);

    // The default window leaves the URL as it was.
    await fireEvent.click(screen.getByRole('radio', { name: 'Day' }));
    await vi.waitFor(() => expect(location.hash).toBe('#/definitions/billing/send-invoice?ns=billing'));
  });
});

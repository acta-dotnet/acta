import { render } from '@testing-library/svelte';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import OverviewHarness from '../test/OverviewHarness.svelte';
import { setScope } from '../scope.ts';

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });
const paged = (items: unknown[]) => ({ items, nextCursor: null, hasMore: false, pageSize: 10, totalCount: items.length });

const overview = (readyCount: number, oldestReadyAgeSeconds: number) => ({
  readyCount,
  oldestReadyAgeSeconds,
  executingCount: 16,
  executorCapacity: 16,
  failedCount: 0,
  unresolvedAlertCount: 0,
  unresolvedCriticalAlertCount: 0,
  deadWorkerCount: 0,
  staleWorkerCount: 0,
  dueSoonScheduleCount: 0,
  scheduleLagSeconds: 0
});

function stubOverview(answer: () => unknown) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(input as string | URL);
      if (url.pathname.endsWith('/overview')) return json(answer());
      if (url.pathname.endsWith('/outbox/sources')) return json({ items: [] });
      return json(paged([]));
    })
  );
}

const card = (container: HTMLElement, label: string) =>
  [...container.querySelectorAll<HTMLElement>('.metric')].find((metric) => metric.querySelector('.metric-label')?.textContent?.startsWith(label))!;
const drawn = (metric: HTMLElement) => metric.querySelector('.metric-icon path')?.getAttribute('d');

describe('Overview', () => {
  afterEach(() => vi.useRealTimers());

  // Only the clock is faked: the direction is read off the instants two polls answered at, and nothing here waits.
  it('turns a card to an arrow once its value has moved between two polls a minute apart', async () => {
    setScope('');
    location.hash = '#/';
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-06-12T10:00:00Z'));
    let now = overview(6200, 42);
    stubOverview(() => now);

    const { container, getByRole } = render(OverviewHarness);

    await vi.waitFor(() => expect(card(container, 'Ready')?.querySelector('.metric-value')?.textContent).toBe('6200'));
    expect(card(container, 'Ready').querySelector('.metric-icon.direction')).toBeNull();
    expect(card(container, 'Ready').title).toBe('');
    const clock = drawn(card(container, 'Ready'));

    vi.setSystemTime(new Date('2026-06-12T10:02:00Z'));
    now = overview(5000, 142);
    await userEvent.click(getByRole('button', { name: 'Refresh now' }));

    await vi.waitFor(() => expect(card(container, 'Ready').querySelector('.metric-value')?.textContent).toBe('5000'));
    // A link's hint is its description, so the words stay out of its name.
    const ready = card(container, 'Ready');
    expect(ready.querySelector('.metric-icon.direction')).not.toBeNull();
    expect(drawn(ready)).not.toBe(clock);
    expect(ready.title).toBe('Down 1200 in 2 minutes');
    expect(ready.querySelector('.metric-label')?.textContent).toBe('Ready');
    // The oldest wait is no link: its hint gains the words, and its label carries them for a screen reader.
    const oldest = card(container, 'Oldest ready');
    expect(oldest.querySelector('.metric-icon.direction')).not.toBeNull();
    expect(drawn(oldest)).not.toBe(drawn(ready));
    expect(oldest.title).toBe('How long the oldest due job has been waiting for a worker. Up 1m 40s in 2 minutes');
    expect(oldest.querySelector('.metric-label')?.textContent).toBe('Oldest ready, Up 1m 40s in 2 minutes');
  });
});

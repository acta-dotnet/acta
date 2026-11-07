import { fireEvent, render, screen } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import CalendarsListHarness from '../test/CalendarsListHarness.svelte';

const at = '2026-07-14T08:00:00Z';

const family = (calendarName: string, currentVersion: number | null, latestVersion: number, latestStatus: string, dependentCount: number) => ({
  jobNamespace: 'billing',
  calendarName,
  role: 'availability',
  currentVersion,
  latestVersion,
  latestStatus,
  publishedAtUtc: currentVersion === null ? null : at,
  modifiedAtUtc: at,
  dependentCount
});

const families = [
  family('office-hours', 2, 3, 'draft', 4),
  family('payout-cutoff', null, 1, 'draft', 2),
  family('support-window', 1, 1, 'published', 1)
];

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });

// Serves the list as the server would filter it, and answers the list requests it served.
function stubApi(): URL[] {
  const listReads: URL[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(input as string | URL);
      if (url.pathname.endsWith('/capabilities')) {
        return json({ controlsEnabled: true, engineVersion: 'v', provider: 'sqlite', confirmationHeader: 'X-Acta-Control', permissions: ['acta.read'] });
      }
      listReads.push(url);
      const status = url.searchParams.get('latestStatus');
      const items = families.filter((f) => !status || f.latestStatus === status);
      return json({ items, nextCursor: null, hasMore: false, pageSize: 50, totalCount: items.length });
    })
  );
  return listReads;
}

const row = (name: string) => screen.getByRole('link', { name }).closest('tr') as HTMLTableRowElement;

describe('CalendarsList', () => {
  it('asks the server for the families whose latest version has the chosen status', async () => {
    location.hash = '#/calendars';
    const listReads = stubApi();

    render(CalendarsListHarness);
    await screen.findByRole('link', { name: 'support-window' });
    // A latest version is superseded when an older draft is published after it.
    const status = screen.getByLabelText('Status') as HTMLSelectElement;
    expect([...status.options].map((option) => option.value)).toEqual(['', 'draft', 'published', 'superseded', 'archived']);
    await fireEvent.change(status, { target: { value: 'draft' } });

    await vi.waitFor(() => expect(listReads.some((url) => url.searchParams.get('latestStatus') === 'draft')).toBe(true));
    await vi.waitFor(() => expect(screen.queryByRole('link', { name: 'support-window' })).toBeNull());
    expect(screen.getByRole('button', { name: /Status: draft/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Copy link' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Copy SQL' })).toBeTruthy();
  });

  it('says what each row rail means, and marks a bound family with no current version as an error', async () => {
    location.hash = '#/calendars';
    stubApi();

    render(CalendarsListHarness);
    await screen.findByRole('link', { name: 'office-hours' });

    const draft = row('office-hours');
    expect(draft.classList.contains('due')).toBe(true);
    expect(draft.cells[0].title).toBe('Draft v3 is newer than current v2 and waits to be published.');
    expect(draft.cells[0].textContent).toContain('Draft v3 is newer than current v2');

    const broken = row('payout-cutoff');
    expect(broken.classList.contains('trouble')).toBe(true);
    expect(broken.cells[0].title).toMatch(/^No current version: 2 bound schedules report evaluation errors/);
    const current = broken.querySelector('td[data-label="Current"] .badge.bad');
    expect(current?.textContent?.trim()).toBe('none');

    const settled = row('support-window');
    expect([settled.classList.contains('due'), settled.classList.contains('trouble'), settled.cells[0].title]).toEqual([false, false, '']);
  });
});

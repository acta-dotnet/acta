import { fireEvent, render, screen } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import CalendarDetailHarness from '../test/CalendarDetailHarness.svelte';

const jobNamespace = 'sys';
const calendarName = 'office-hours';
const at = '2026-07-14T08:00:00Z';

function page(items: unknown[]) {
  return { items, nextCursor: null, hasMore: false, pageSize: 20, totalCount: items.length };
}

// The family with version 2 shown: a draft beside current version 1, or current itself once published.
function detail(current: number) {
  const published = current === 2;
  return {
    calendar: {
      jobNamespace,
      calendarName,
      role: 'availability',
      currentVersion: current,
      latestVersion: 2,
      shownVersion: 2,
      shownStatus: published ? 'published' : 'draft',
      definition: { kind: 'weekly-availability', timeZone: 'UTC', weekly: [{ days: ['Monday'], windows: [['09:00', '17:00']] }] }
    },
    versions: page([
      { version: 2, status: published ? 'published' : 'draft', contentHash: 'h2', createdAtUtc: at, publishedAtUtc: published ? at : null, modifiedAtUtc: at },
      { version: 1, status: published ? 'superseded' : 'published', contentHash: 'h1', createdAtUtc: at, publishedAtUtc: at, modifiedAtUtc: at }
    ]),
    dependents: page([])
  };
}

const preview = {
  role: 'availability',
  version: 2,
  currentVersion: 1,
  occurrencesUtc: [],
  currentOccurrencesUtc: [],
  windows: [],
  currentWindows: [],
  schedules: [],
  schedulesTotal: 0,
  overdueTotal: 0
};

const adminCapabilities = {
  controlsEnabled: true,
  engineVersion: 'v',
  provider: 'sqlite',
  confirmationHeader: 'X-Acta-Control',
  permissions: ['acta.read', 'acta.admin']
};

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });

describe('CalendarDetail publish', () => {
  it('treats a publication of the version already current as a success and shows it as current', async () => {
    // Another operator published version 2 after this page loaded it as a draft.
    let current = 1;
    const detailReads: URL[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = new URL(input as string | URL);
        if (url.pathname.endsWith('/capabilities')) return json(adminCapabilities);
        if (url.pathname.endsWith('/publish')) {
          current = 2;
          return json({ action: 'already-in-state', version: 2, retimedSchedules: [], overdueSchedules: [], reason: null });
        }
        if (url.pathname.endsWith('/preview')) return json(preview);
        if (url.pathname.endsWith('/events')) return json(page([]));
        detailReads.push(url);
        return json(detail(current));
      })
    );

    render(CalendarDetailHarness, { jobNamespace, calendarName });
    await fireEvent.click(await screen.findByRole('button', { name: /Publish version 2/ }));
    await fireEvent.input(screen.getByLabelText(/Reason \(required/), { target: { value: 'Summer hours.' } });
    await fireEvent.click(screen.getByRole('button', { name: 'Publish' }));

    const message = await screen.findByText('Version 2 is already current; nothing was retimed.');
    expect(message.classList.contains('ok')).toBe(true);
    expect(await screen.findByText(/^Version 2 is current/)).toBeTruthy();
    expect(detailReads.length).toBeGreaterThan(1);
    expect(screen.queryByRole('button', { name: /Publish version 2/ })).toBeNull();
  });
});

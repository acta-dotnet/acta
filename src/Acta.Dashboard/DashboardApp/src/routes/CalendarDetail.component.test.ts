import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import CalendarDetailHarness from '../test/CalendarDetailHarness.svelte';

const jobNamespace = 'sys';
const calendarName = 'office-hours';
const at = '2026-07-14T08:00:00Z';

function page(items: unknown[]) {
  return { items, nextCursor: null, hasMore: false, pageSize: 20, totalCount: items.length };
}

// The family with version 2 shown: a draft beside current version 1, current itself once published, or a draft beside
// an archived version 1 when nothing is current.
function detail(current: number | null) {
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
      { version: 1, status: published ? 'superseded' : current === 1 ? 'published' : 'archived', contentHash: 'h1', createdAtUtc: at, publishedAtUtc: at, modifiedAtUtc: at }
    ]),
    dependents: page([])
  };
}

const window = (day: string) => ({ startUtc: `2026-07-${day}T09:00:00Z`, endUtc: `2026-07-${day}T17:00:00Z` });
const bound = (...statuses: string[]) =>
  statuses.map((status, i) => ({ jobNamespace: 'billing', jobName: `job-${i}`, scheduleName: 'nightly', binding: 'availability', status, overdue: false }));

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

// Serves the page's reads, and a publication when given one; answers the detail reads it served.
function stubApi(routes: { detail: () => unknown; preview?: unknown; publish?: () => unknown }): URL[] {
  const detailReads: URL[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(input as string | URL);
      if (url.pathname.endsWith('/capabilities')) return json(adminCapabilities);
      if (url.pathname.endsWith('/publish') && routes.publish) return json(routes.publish());
      if (url.pathname.endsWith('/preview')) return json(routes.preview ?? preview);
      if (url.pathname.endsWith('/events')) return json(page([]));
      detailReads.push(url);
      return json(routes.detail());
    })
  );
  return detailReads;
}

describe('CalendarDetail saved version', () => {
  it('reads as bars and text, and only a new draft opens form fields', async () => {
    stubApi({ detail: () => detail(1) });

    render(CalendarDetailHarness, { jobNamespace, calendarName });
    await screen.findByRole('heading', { name: 'Version 2 definition' });
    const definition = screen.getByRole('region', { name: 'Definition' });

    expect(definition.querySelectorAll('input, select, textarea')).toHaveLength(0);
    expect(definition.textContent).toContain('09:00 to 17:00');
    expect(within(definition).getByText('UTC')).toBeTruthy();

    await fireEvent.click(within(definition).getByRole('button', { name: 'Edit as a new draft' }));
    expect(within(definition).getByLabelText('Monday window 1 from')).toBeTruthy();
    expect(within(definition).getByRole('button', { name: 'Save as version 3' })).toBeTruthy();
  });
});

describe('CalendarDetail publish', () => {
  it('names at most the schedules it retimes and what the forecast changes beside the publish button', async () => {
    stubApi({
      detail: () => detail(1),
      preview: { ...preview, windows: [window('14'), window('15'), window('16')], currentWindows: [window('14'), window('15')], schedules: bound('active', 'orphaned', 'paused'), schedulesTotal: 3 }
    });

    render(CalendarDetailHarness, { jobNamespace, calendarName });
    const publish = await screen.findByRole('button', { name: 'Publish version 2' });
    const impact = screen.getByRole('list', { name: 'What publishing changes' });

    // A publication leaves an orphaned schedule as it is.
    expect(await within(impact).findByText('Retimes up to 2 bound schedules to this version.')).toBeTruthy();
    expect(within(impact).getByText('1 added, 0 removed over the next 3 windows.')).toBeTruthy();
    expect(publish.closest('section')).toBe(impact.closest('section'));

    await fireEvent.click(publish);
    expect(screen.getByRole('dialog').textContent).toContain('up to 2 bound schedules are retimed to it in one transaction');
  });

  it('treats a publication of the version already current as a success and shows it as current', async () => {
    // Another operator published version 2 after this page loaded it as a draft.
    let current = 1;
    const detailReads = stubApi({
      detail: () => detail(current),
      publish: () => {
        current = 2;
        return { action: 'already-in-state', version: 2, retimedSchedules: [], overdueSchedules: [], reason: null };
      }
    });

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

describe('CalendarDetail archive', () => {
  it('is disabled with the reason while a live schedule binds the calendar', async () => {
    stubApi({ detail: () => detail(2), preview: { ...preview, currentVersion: 2, schedules: bound('active', 'orphaned'), schedulesTotal: 2 } });

    render(CalendarDetailHarness, { jobNamespace, calendarName });
    const archive = (await screen.findByRole('button', { name: 'Archive calendar' })) as HTMLButtonElement;

    await vi.waitFor(() =>
      expect(document.getElementById(archive.getAttribute('aria-describedby') ?? '')?.textContent).toContain('1 schedule binds this calendar.')
    );
    expect(archive.disabled).toBe(true);
  });

  it('is held while the bound schedules are unknown, says why, and the page refresh checks again', async () => {
    let previewReads = 0;
    let failFirstRead!: () => void;
    const firstRead = new Promise<Response>((resolve) => {
      failFirstRead = () => resolve(new Response(JSON.stringify({ title: 'Forecast failed.' }), { status: 500 }));
    });
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) => {
        const url = new URL(input as string | URL);
        if (url.pathname.endsWith('/capabilities')) return Promise.resolve(json(adminCapabilities));
        if (url.pathname.endsWith('/events')) return Promise.resolve(json(page([])));
        if (url.pathname.endsWith('/preview')) {
          previewReads++;
          return previewReads === 1 ? firstRead : Promise.resolve(json({ ...preview, currentVersion: 2, schedules: bound('orphaned'), schedulesTotal: 1 }));
        }
        return Promise.resolve(json(detail(2)));
      })
    );

    render(CalendarDetailHarness, { jobNamespace, calendarName });
    const archive = (await screen.findByRole('button', { name: 'Archive calendar' })) as HTMLButtonElement;
    const reason = () => document.getElementById(archive.getAttribute('aria-describedby') ?? '')?.textContent;

    expect(archive.disabled).toBe(true);
    expect(reason()).toBe('Checking the schedules bound to this calendar.');

    failFirstRead();
    await vi.waitFor(() => expect(reason()).toContain('could not be checked because the forecast did not load'));
    expect(archive.disabled).toBe(true);

    await fireEvent.click(screen.getByRole('button', { name: 'Refresh now' }));
    await vi.waitFor(() => expect(archive.disabled).toBe(false));
    expect(previewReads).toBe(2);
  });

  it('is offered when only orphaned schedules name the calendar, and names what it retires', async () => {
    stubApi({ detail: () => detail(2), preview: { ...preview, currentVersion: 2, schedules: bound('orphaned'), schedulesTotal: 1 } });

    render(CalendarDetailHarness, { jobNamespace, calendarName });
    // The forecast's title shows the preview has loaded, so the gate has read its schedules.
    await screen.findByText('Version 2 is current: the next windows');
    const archive = screen.getByRole('button', { name: 'Archive calendar' }) as HTMLButtonElement;
    expect(archive.disabled).toBe(false);

    await fireEvent.click(archive);
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText('Archive office-hours?')).toBeTruthy();
    expect(within(dialog).getByRole('button', { name: 'Archive calendar' })).toBeTruthy();
  });

  it('is absent without a current version, which the header says in sentence case', async () => {
    stubApi({ detail: () => detail(null), preview: { ...preview, currentVersion: null } });

    render(CalendarDetailHarness, { jobNamespace, calendarName });
    await screen.findByRole('button', { name: 'Publish version 2' });

    expect(screen.queryByRole('button', { name: 'Archive calendar' })).toBeNull();
    expect(screen.getByText('No current version')).toBeTruthy();
  });
});

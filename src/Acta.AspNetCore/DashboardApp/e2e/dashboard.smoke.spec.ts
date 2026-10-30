import { expect, test, type Page, type Route } from '@playwright/test';

const jobRef = 'job_019f9b3509487d1b911f2020158384a3';
const timestamp = '2026-07-14T08:00:00Z';

const paged = <T>(items: T[]) => ({ items, nextCursor: null, hasMore: false, pageSize: 50, totalCount: items.length });

const job = {
  jobRef,
  lineageRootJobRef: null,
  parentJobRef: null,
  deduplicationKey: 'invoice-42',
  correlationKey: 'order-42',
  jobNamespace: 'billing',
  jobName: 'send-invoice',
  tenantName: null,
  status: 'failed',
  priority: 'normal',
  executionNumber: 1,
  failureCount: 1,
  inputFormatId: 1,
  nextRunAtUtc: null,
  leasedByWorkerRef: null,
  leaseExpiresAtUtc: null,
  concurrencyKey: null,
  retentionUntilUtc: '2026-08-14T08:00:00Z',
  createdAtUtc: '2026-07-14T07:55:00Z',
  modifiedAtUtc: timestamp
};

// The failed attempt's one event: the job's final failure, which closes attempt 1.
const event = {
  eventId: 90,
  eventCode: 'job.failed',
  createdAtUtc: timestamp,
  jobNamespace: 'billing',
  entityKind: 'job',
  entityId: 4242,
  jobName: 'send-invoice',
  jobRef,
  lineageRootJobRef: null,
  tenantName: null,
  workerRef: 'wrk_019f9b3508007e4dbfd13d9d20bc81f4',
  executionNumber: 1,
  durationMs: 125,
  message: 'Invoice provider timed out.',
  detailsText: '{"reason":"unhandled-exception"}'
};

// A shared availability calendar: version 2 is current, version 3 a draft that opens Saturday mornings.
const calendarVersions = [
  { version: 3, status: 'draft', contentHash: 'c3', createdAtUtc: timestamp, publishedAtUtc: null, modifiedAtUtc: timestamp },
  { version: 2, status: 'published', contentHash: 'c2', createdAtUtc: '2026-07-01T08:00:00Z', publishedAtUtc: '2026-07-02T08:00:00Z', modifiedAtUtc: '2026-07-02T08:00:00Z' },
  { version: 1, status: 'superseded', contentHash: 'c1', createdAtUtc: '2026-06-01T08:00:00Z', publishedAtUtc: '2026-06-02T08:00:00Z', modifiedAtUtc: '2026-07-02T08:00:00Z' }
];
const weekdays = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'];
const officeHours = (shown: number) => ({
  jobNamespace: 'sys',
  calendarName: 'office-hours',
  role: 'availability',
  currentVersion: 2,
  versions: calendarVersions,
  shownVersion: shown,
  definition: JSON.stringify({
    kind: 'weekly-availability',
    timeZone: 'schedule',
    weekly: shown === 3
      ? [{ days: weekdays, windows: [['09:00', '17:00']] }, { days: ['Saturday'], windows: [['09:00', '12:00']] }]
      : [{ days: weekdays, windows: [['09:00', '17:00']] }]
  }),
  dependents: [
    { jobNamespace: 'billing', jobName: 'send-invoice', scheduleName: 'nightly', binding: 'availability', status: 'active', pinnedVersion: 2, scheduledDelivery: 'permanent' }
  ]
});
const officeHoursPreview = (version: number) => ({
  role: 'availability',
  version,
  currentVersion: 2,
  occurrencesUtc: [],
  currentOccurrencesUtc: [],
  windows: [
    { startUtc: '2026-07-17T07:00:00Z', endUtc: '2026-07-17T15:00:00Z' },
    ...(version === 3 ? [{ startUtc: '2026-07-18T07:00:00Z', endUtc: '2026-07-18T10:00:00Z' }] : []),
    { startUtc: '2026-07-20T07:00:00Z', endUtc: '2026-07-20T15:00:00Z' }
  ],
  currentWindows: [
    { startUtc: '2026-07-17T07:00:00Z', endUtc: '2026-07-17T15:00:00Z' },
    { startUtc: '2026-07-20T07:00:00Z', endUtc: '2026-07-20T15:00:00Z' }
  ],
  schedules: [{ jobNamespace: 'billing', jobName: 'send-invoice', scheduleName: 'nightly', binding: 'availability', status: 'active', pinnedVersion: 2, overdue: false }]
});

async function mockDashboard(
  page: Page,
  options: { controls: boolean; catalogPages?: boolean; onRestart?: () => void; onPublish?: (body: unknown) => void }
): Promise<void> {
  await page.route('**/api/v1/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname.split('/api/v1/')[1] ?? '';
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

    if (path === 'capabilities') {
      return json({ controlsEnabled: options.controls, version: 'test', provider: 'mock', confirmationHeader: 'X-Acta-Control' });
    }
    if (path === 'namespaces') {
      return json(paged([{ jobNamespace: 'billing', status: 'active', ownerTeam: null, description: null, version: 1 }]));
    }
    if (path === 'overview') {
      return json({
        readyCount: 0,
        executingCount: 0,
        failedCount: 1,
        oldestReadyAgeSeconds: 0,
        unresolvedAlertCount: 0,
        unresolvedCriticalAlertCount: 0,
        deadWorkerCount: 0,
        staleWorkerCount: 0,
        dueSoonScheduleCount: 0,
        executorCapacity: 8,
        scheduleLagSeconds: null
      });
    }
    if (path === 'jobs' && request.method() === 'GET') return json(paged([job]));
    // The Overview's outbox sources read had no fixture, so every test that lands on the Overview
    // failed at its first assertion rather than on anything it was written to check.
    if (path === 'outbox/sources') return json(paged([]));
    if (path === 'alerts') return json(paged([]));
    if (path === 'workers') return json(paged([]));
    if (path === 'schedules') return json(paged([]));
    if (path === 'definitions') {
      const definition = (jobName: string) => ({
        jobNamespace: 'billing',
        jobName,
        status: 'active',
        inputTypeName: 'SendInvoice',
        outputTypeName: null,
        priorityEffective: 'normal',
        priorityOverride: null,
        maxAttemptsEffective: 3,
        maxAttemptsOverride: null,
        modifiedAtUtc: timestamp
      });
      // The enqueue picker walks the catalog in pages of 100; the job on page three proves the walk.
      if (options.catalogPages) {
        const all = ['send-invoice', ...Array.from({ length: 204 }, (_, i) => `report-${String(i + 1).padStart(3, '0')}`)];
        const cursor = url.searchParams.get('cursor');
        const start = cursor === 'page-2' ? 100 : cursor === 'page-3' ? 200 : 0;
        const items = all.slice(start, start + 100).map(definition);
        const next = start + 100 < all.length ? (start === 0 ? 'page-2' : 'page-3') : null;
        return json({ items, nextCursor: next, hasMore: next !== null, pageSize: 100, totalCount: all.length });
      }
      return json(paged([definition('send-invoice')]));
    }
    if (path === 'tenants') return json(paged([]));
    if (path === 'calendars') {
      return json(paged([{
        jobNamespace: 'sys',
        calendarName: 'office-hours',
        role: 'availability',
        currentVersion: 2,
        latestVersion: 3,
        latestStatus: 'draft',
        publishedAtUtc: '2026-07-02T08:00:00Z',
        modifiedAtUtc: timestamp,
        dependentCount: 1
      }]));
    }
    if (path === 'calendars/sys/office-hours') return json(officeHours(Number(url.searchParams.get('version') ?? '2')));
    if (path === 'calendars/sys/office-hours/preview') return json(officeHoursPreview(Number(url.searchParams.get('version') ?? '2')));
    if (path === 'calendars/sys/office-hours/publish' && request.method() === 'POST') {
      if (!options.controls) return json({ title: 'Controls disabled.' }, 404);
      options.onPublish?.(request.postDataJSON());
      return json({ action: 'applied', version: 3, retimedSchedules: ['billing/send-invoice/nightly'], overdueSchedules: [], reason: null });
    }
    if (path === 'jobs/by-key') return json(job);
    if (path === `jobs/${jobRef}/events` || path === 'events') return json(paged([event]));
    // The aggregate the detail page actually reads (JobDetailView in api.ts). Without it the
    // page rendered its title and nothing else, so three tests failed at their first assertion
    // rather than on what they were written to check.
    if (path === `jobs/${jobRef}/detail`) {
      return json({
        job,
        input: { formatName: 'json', formatId: 1, json: '{"invoiceId":42}', byteLength: 16, truncated: false },
        result: null,
        checkpoints: [],
        explain: {
          headline: 'The latest attempt failed in the invoice provider.',
          activeWait: null,
          lease: null,
          lastExecutedBy: 'worker-42',
          steps: [],
          reason: 'Invoice provider timed out.',
          nextActions: [{ kind: 'restart', description: 'Run the job again after checking the provider.' }]
        },
        lineage: { ancestors: [], job, steps: [], activeWait: null, children: [], childrenHasMore: false },
        schedules: [],
        schedulesTotal: 0,
        workers: [],
        workersTotal: 0
      });
    }
    if (path === `jobs/${jobRef}/restart` && request.method() === 'POST') {
      if (!options.controls) return json({ title: 'Controls disabled.' }, 404);
      options.onRestart?.();
      return json({ action: 'applied', status: 'ready', version: 4 });
    }

    return json({ title: `No mock for ${request.method()} ${path}` }, 404);
  });
}

test('job investigation: overview to failed job explanation and events', async ({ page }) => {
  await mockDashboard(page, { controls: true });
  await page.goto('/');

  await expect(page.getByRole('heading', { name: 'Recent failed jobs' })).toBeVisible();
  await page.getByRole('link', { name: jobRef }).click();
  await expect(page.getByRole('heading', { name: 'Evidence' })).toBeVisible();
  await expect(page.getByText('The latest attempt failed in the invoice provider.').first()).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Timeline' })).toBeVisible();
  await expect(page.getByText('Invoice provider timed out.').first()).toBeVisible();
});

test('details shows the request and the result under the status, and an old result link lands there', async ({ page }) => {
  await mockDashboard(page, { controls: false });
  await page.goto(`/#/jobs/${jobRef}?ns=billing&tab=result`);

  await expect(page.getByRole('region', { name: 'Job input' })).toContainText('invoiceId');
  await expect(page.getByRole('region', { name: 'Job result' })).toContainText('No result yet');
  await expect(page.getByRole('heading', { name: 'Timeline' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Result', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Input', exact: true })).toHaveCount(0);
});

test('executions tab summarizes the attempt and deep-links expanded', async ({ page }) => {
  await mockDashboard(page, { controls: false });
  await page.goto(`/#/jobs/${jobRef}?ns=billing`);

  await page.getByRole('button', { name: 'Executions' }).click();
  await expect(page.getByText('Showing 1 of 1 executions')).toBeVisible();
  await expect(page.getByText('125 ms')).toBeVisible();
  await expect(page.getByRole('link', { name: 'wrk_019f9b3508007e4dbfd13d9d20bc81f4' })).toBeVisible();

  await page.goto(`/#/jobs/${jobRef}?ns=billing&tab=executions&execution=1`);
  const walk = page.getByRole('button', { name: 'Walk execution 1 in the timeline' });
  await expect(walk).toBeVisible();
  await walk.click();
  await expect(page.getByRole('heading', { name: 'Timeline' })).toBeVisible();
  await expect(page.getByLabel('Attempt')).toHaveValue('1');
});

test('events feed reads a linked job\'s events and shows their subject, message, and details', async ({ page }) => {
  await mockDashboard(page, { controls: false });
  const feedRequest = page.waitForRequest((request) => new URL(request.url()).pathname.endsWith('/api/v1/events'));
  await page.goto(`/#/events?ns=billing&jobRef=${jobRef}`);

  expect(new URL((await feedRequest).url()).searchParams.get('jobRef')).toBe(jobRef);
  await expect(page.getByText('Invoice provider timed out.')).toBeVisible();
  await expect(page.getByText('{"reason":"unhandled-exception"}')).toBeVisible();
  await expect(page.getByRole('link', { name: jobRef })).toBeVisible();
});

test('quick search finds definitions by fragment and jumps to a pasted job ref', async ({ page }) => {
  await mockDashboard(page, { controls: true });
  await page.goto('/');

  await page.keyboard.press('Control+k');
  const input = page.getByRole('combobox', { name: 'Quick search' });
  await expect(input).toBeFocused();

  await input.fill('send');
  await expect(page.getByRole('option', { name: /send-invoice/ })).toBeVisible();

  await input.fill(jobRef);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Evidence' })).toBeVisible();
});

test('safe single-job control sends exactly one restart request', async ({ page }) => {
  let restartRequests = 0;
  await mockDashboard(page, { controls: true, onRestart: () => restartRequests++ });
  await page.goto(`/#/jobs/${jobRef}?ns=billing`);

  await page.getByRole('button', { name: 'Run again' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText(/External side effects.*may be repeated/)).toBeVisible();
  await dialog.getByLabel(/Reason \(required/).fill('Provider recovered; operator retry.');
  await dialog.getByRole('button', { name: 'Run again' }).click();

  await expect(page.getByText('Restart applied.')).toBeVisible();
  expect(restartRequests).toBe(1);
});

test('calendar catalog: list to a draft version, the week bars and the forecast diff', async ({ page }) => {
  await mockDashboard(page, { controls: false });
  await page.goto('/#/calendars?ns=billing');

  await expect(page.getByRole('link', { name: 'office-hours', exact: true })).toBeVisible();
  await expect(page.getByText('shared', { exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'office-hours', exact: true }).click();

  await expect(page.getByRole('heading', { name: 'Version 2 definition' })).toBeVisible();
  await expect(page.getByRole('group', { name: 'Weekly hours' })).toBeVisible();
  await expect(page.getByLabel('Monday: 8h')).toBeVisible();
  await expect(page.getByLabel('Saturday: closed')).toBeVisible();
  await expect(page.getByRole('button', { name: /Publish version/ })).toHaveCount(0);

  await page.getByRole('link', { name: 'Version 3, draft' }).click();
  await expect(page.getByRole('heading', { name: 'Version 3 definition' })).toBeVisible();
  await expect(page.getByLabel('Saturday: 3h')).toBeVisible();
  await expect(page.getByText('Version 3 beside current version 2')).toBeVisible();
  await expect(page.getByText('1 added')).toBeVisible();
  await expect(page.getByRole('link', { name: 'billing/send-invoice/nightly' })).toBeVisible();
});

test('publishing a draft calendar sends one publish request carrying the candidate and the reason', async ({ page }) => {
  const publishes: unknown[] = [];
  await mockDashboard(page, { controls: true, onPublish: (body) => publishes.push(body) });
  await page.goto('/#/calendars/sys/office-hours?ns=billing&v=3');

  await page.getByRole('button', { name: 'Publish version 3' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText(/the 1 bound schedule is retimed/)).toBeVisible();
  await dialog.getByLabel(/Reason \(required/).fill('Saturday mornings open from July.');
  await dialog.getByRole('button', { name: 'Publish', exact: true }).click();

  await expect(page.getByText('Version 3 published; 1 schedule retimed.')).toBeVisible();
  expect(publishes).toEqual([
    { expectedCurrentVersion: 2, candidateVersion: 3, overdueDecision: 'refuse', reasonMessage: 'Saturday mornings open from July.' }
  ]);
});

test('the enqueue picker walks every catalog page and offers a job from page three', async ({ page }) => {
  await mockDashboard(page, { controls: true, catalogPages: true });
  await page.goto('/#/enqueue?ns=billing');

  await page.getByRole('button', { name: 'Job name' }).click();
  await page.getByRole('textbox', { name: 'Job name filter' }).fill('report-204');
  const option = page.getByRole('option', { name: 'report-204' });
  await expect(option).toBeVisible();
  await option.click();
  await expect(page.getByRole('button', { name: 'Job name' })).toContainText('report-204');
});

test('read-only deployment hides controls and rejects a direct request', async ({ page }) => {
  await mockDashboard(page, { controls: false });
  await page.goto(`/#/jobs/${jobRef}`);

  await expect(page.locator('.mode-chip')).toBeVisible();
  await expect(page.locator('.mode-chip')).toContainText('read-only');
  await expect(page.getByRole('button', { name: 'Run again' })).toHaveCount(0);
  const status = await page.evaluate(async (ref) => {
    const response = await fetch(`api/v1/jobs/${ref}/restart`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Acta-Control': 'true' },
      body: JSON.stringify({ reasonMessage: 'bypass attempt' })
    });
    return response.status;
  }, jobRef);
  expect(status).toBeGreaterThanOrEqual(400);
});

test('Jobs and Job Detail fit a mobile viewport without page overflow', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await mockDashboard(page, { controls: false });
  await page.goto('/#/jobs?ns=billing');

  await expect(page.getByRole('button', { name: 'Open navigation' })).toBeVisible();
  await expect(page.getByRole('link', { name: /send-invoice/ })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);

  await page.getByRole('link', { name: /send-invoice/ }).click();
  // The detail crumb titles the page with the durable ref; the name lives in the hero meta.
  await expect(page.getByRole('heading', { name: jobRef })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('appearance defaults, themes, accents, text sizing, persistence, and reset stay coherent', async ({ page }) => {
  await mockDashboard(page, { controls: true });
  await page.addInitScript(() => {
    localStorage.removeItem('acta-appearance-v1');
  });
  // A fresh install follows the OS preference; pin it dark so the golden tokens are deterministic.
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.goto('/');

  const golden = await page.evaluate(() => {
    const root = document.documentElement;
    const styles = getComputedStyle(root);
    const before = getComputedStyle(document.body, '::before');
    const tokens = [
      '--bg',
      '--panel',
      '--ink',
      '--muted',
      '--line',
      '--accent',
      '--accent-solid',
      '--nav-active-bg',
      '--grid',
      '--glow',
    ];
    return {
      theme: root.dataset.theme,
      accent: root.dataset.accent,
      textSize: root.dataset.textSize,
      colorScheme: root.style.colorScheme,
      tokens: Object.fromEntries(tokens.map((token) => [token, styles.getPropertyValue(token).trim()])),
      gridSize: before.backgroundSize,
      bodyBackground: getComputedStyle(document.body).backgroundImage,
    };
  });

  expect(golden).toEqual({
    theme: 'acta',
    accent: 'teal',
    textSize: 'default',
    colorScheme: 'dark',
    tokens: {
      '--bg': '#0b0f17',
      '--panel': '#11161f',
      '--ink': '#eef2f7',
      '--muted': '#9aa6b6',
      '--line': '#232c40',
      '--accent': '#64d8c7',
      '--accent-solid': '#077a70',
      '--nav-active-bg': '#16302b',
      '--grid': 'rgba(100, 216, 199, 0.05)',
      '--glow': 'rgba(100, 216, 199, 0.1)',
    },
    gridSize: '46px 46px, 46px 46px',
    bodyBackground: expect.stringContaining('radial-gradient'),
  });

  const trigger = page.getByRole('button', { name: /^Appearance$/ });
  await trigger.click();
  const systemRadio = page.getByRole('radio', { name: /^System/ });
  await expect(systemRadio).toBeFocused();

  const popoverGeometry = await page.evaluate(() => {
    const nav = document.querySelector<HTMLElement>('nav.side');
    const sideScroll = document.querySelector<HTMLElement>('.side-scroll');
    const popover = document.querySelector<HTMLElement>('.appearance-menu .popover');
    if (!nav || !sideScroll || !popover) return null;

    const navRect = nav.getBoundingClientRect();
    const popoverRect = popover.getBoundingClientRect();
    const visiblePoint = document.elementFromPoint(popoverRect.right - 2, popoverRect.top + 20);
    return {
      extendsPastSidebar: popoverRect.right > navRect.right,
      rightEdgeVisible: visiblePoint !== null && popover.contains(visiblePoint),
      navigationHasHorizontalOverflow: sideScroll.scrollWidth > sideScroll.clientWidth,
    };
  });
  expect(popoverGeometry).toEqual({
    extendsPastSidebar: true,
    rightEdgeVisible: true,
    navigationHasHorizontalOverflow: false,
  });

  await page.getByText('Light', { exact: true }).click();
  await page.getByText('Violet', { exact: true }).click();
  await page.getByText('Large', { exact: true }).click();

  const lightViolet = await page.evaluate(() => {
    const root = document.documentElement;
    const styles = getComputedStyle(root);
    return {
      theme: root.dataset.theme,
      accent: root.dataset.accent,
      textSize: root.dataset.textSize,
      colorScheme: root.style.colorScheme,
      background: styles.getPropertyValue('--bg').trim(),
      panel: styles.getPropertyValue('--panel').trim(),
      accentToken: styles.getPropertyValue('--accent').trim(),
      rowHeight: styles.getPropertyValue('--grid-row-height').trim(),
      stored: JSON.parse(localStorage.getItem('acta-appearance-v1') ?? 'null'),
    };
  });
  expect(lightViolet).toEqual({
    theme: 'light',
    accent: 'violet',
    textSize: 'large',
    colorScheme: 'light',
    background: '#f3f6fa',
    panel: '#ffffff',
    accentToken: '#6550b9',
    rowHeight: '48px',
    stored: { version: 1, theme: 'light', accent: 'violet', textSize: 'large' },
  });

  await page.getByText('Amber', { exact: true }).click();
  const neutralsAfterAccentSwitch = await page.evaluate(() => {
    const styles = getComputedStyle(document.documentElement);
    return [styles.getPropertyValue('--bg').trim(), styles.getPropertyValue('--panel').trim()];
  });
  expect(neutralsAfterAccentSwitch).toEqual(['#f3f6fa', '#ffffff']);

  await page.getByText('Paper', { exact: true }).click();
  await page.getByText('Crimson', { exact: true }).click();
  await page.getByText('Small', { exact: true }).click();
  const paper = await page.evaluate(() => {
    const rootStyles = getComputedStyle(document.documentElement);
    const heading = document.querySelector<HTMLElement>('.page-head h1');
    return {
      colorScheme: document.documentElement.style.colorScheme,
      background: rootStyles.getPropertyValue('--bg').trim(),
      panel: rootStyles.getPropertyValue('--panel').trim(),
      radius: rootStyles.getPropertyValue('--radius-panel').trim(),
      rowHeight: rootStyles.getPropertyValue('--grid-row-height').trim(),
      headingFont: heading ? getComputedStyle(heading).fontFamily : '',
      ruledBackground: getComputedStyle(document.body, '::before').backgroundImage,
    };
  });
  expect(paper).toEqual({
    colorScheme: 'light',
    // Chrome is the deeper tone and the sheet is the lighter one, the inverse of the other
    // themes; nothing near-white appears anywhere.
    background: '#f4ecd9',
    panel: '#ebe0c8',
    radius: '0px',
    rowHeight: '38px',
    // Paper is the only theme with its own display face (a system serif stack, no bundled font).
    headingFont: expect.stringContaining('serif'),
    // The ruled-paper overlay is gone: it was invisible in practice and it fought the row rules.
    ruledBackground: 'none',
  });

  await page.getByRole('button', { name: 'Restore defaults' }).click();
  await expect(page.getByRole('radio', { name: /^System/ })).toBeChecked();
  await expect(page.getByRole('radio', { name: 'Teal', exact: true })).toBeChecked();
  await expect(page.getByRole('radio', { name: 'Default', exact: true })).toBeChecked();

  await page.keyboard.press('Escape');
  await expect(page.getByRole('button', { name: /^Appearance$/ })).toBeFocused();
});

test('appearance dialog stays usable at 320px by 500px with Large text', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 500 });
  await mockDashboard(page, { controls: true });
  await page.goto('/');

  await page.getByRole('button', { name: 'Open navigation' }).click();
  await page.getByRole('button', { name: /Appearance/ }).click();

  const dialog = page.getByRole('dialog', { name: 'Appearance' });
  await expect(dialog).toBeVisible();
  await dialog.getByText('Large', { exact: true }).click();
  const box = await dialog.boundingBox();
  expect(box).not.toBeNull();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.y).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(320);
  expect(box!.y + box!.height).toBeLessThanOrEqual(500);

  await dialog.getByRole('button', { name: 'Restore defaults' }).scrollIntoViewIfNeeded();
  await dialog.getByRole('button', { name: 'Restore defaults' }).click();
  await expect(dialog.getByRole('radio', { name: 'Default' })).toBeChecked();
});

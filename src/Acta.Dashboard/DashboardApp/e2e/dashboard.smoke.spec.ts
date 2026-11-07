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
  calendar: {
    jobNamespace: 'sys',
    calendarName: 'office-hours',
    role: 'availability',
    currentVersion: 2,
    latestVersion: 3,
    shownVersion: shown,
    shownStatus: calendarVersions.find((v) => v.version === shown)?.status ?? 'draft',
    definition: {
      kind: 'weekly-availability',
      timeZone: 'schedule',
      weekly: shown === 3
        ? [{ days: weekdays, windows: [['09:00', '17:00']] }, { days: ['Saturday'], windows: [['09:00', '12:00']] }]
        : [{ days: weekdays, windows: [['09:00', '17:00']] }]
    }
  },
  versions: { items: calendarVersions, nextCursor: null, hasMore: false, totalCount: calendarVersions.length },
  dependents: {
    items: [
      { jobNamespace: 'billing', jobName: 'send-invoice', scheduleName: 'nightly', binding: 'availability', status: 'active', scheduledDelivery: 'permanent' }
    ],
    nextCursor: null,
    hasMore: false,
    totalCount: 1
  }
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
  schedules: [{ jobNamespace: 'billing', jobName: 'send-invoice', scheduleName: 'nightly', binding: 'availability', status: 'active', overdue: false }],
  schedulesTotal: 1,
  overdueTotal: 0
});

async function mockDashboard(
  page: Page,
  options: {
    controls: boolean;
    catalogPages?: boolean;
    onRestart?: () => void;
    onPublish?: (body: unknown) => void;
    onEnqueue?: (body: unknown) => void;
    // Set, the Overview lists one outbox source with quarantined rows, and its discard reports the body it sent.
    onDiscard?: (body: unknown) => void;
    // The rows the Workers list reads; none unless a test names them.
    workers?: unknown[];
    // While it answers true, every call answers 401 with no body, as an API route does for a lapsed sign-in.
    sessionEnded?: () => boolean;
    // Sees the path of every call, so a test can count what the page sent.
    onRequest?: (path: string) => void;
  }
): Promise<void> {
  await page.route('**/api/v1/**', async (route: Route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname.split('/api/v1/')[1] ?? '';
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    options.onRequest?.(path);
    if (options.sessionEnded?.()) {
      return route.fulfill({ status: 401, body: '' });
    }

    if (path === 'capabilities') {
      return json({ controlsEnabled: options.controls, engineVersion: 'test', provider: 'mock', confirmationHeader: 'X-Acta-Control' });
    }
    if (path === 'namespaces') {
      return json(paged([{ jobNamespace: 'billing', status: 'active', ownerTeam: null, description: null, deploymentVersion: '1.6.2+5c0ffee', version: 1 }]));
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
    if (path === 'jobs' && request.method() === 'POST') {
      options.onEnqueue?.(request.postDataJSON());
      return json({ jobRef, action: 'inserted' }, 201);
    }
    // The Overview's outbox sources read had no fixture, so every test that lands on the Overview
    // failed at its first assertion rather than on anything it was written to check.
    if (path === 'outbox/sources') {
      const source = {
        jobNamespace: 'billing',
        jobRef,
        lastTickSummary: 'claimed=0 relayed=0 dedup=0 quarantined=3 backlog=0 quarantine=3 lag=0s since the last successful relay pass',
        backlog: 0,
        quarantineTotal: 3,
        isLocal: true
      };
      return json(paged(options.onDiscard ? [source] : []));
    }
    if (path === 'outbox/billing/discard' && request.method() === 'POST') {
      options.onDiscard?.(request.postDataJSON());
      return json({ action: 'accepted', pendingSinceUtc: null }, 202);
    }
    if (path === 'alerts') return json(paged([]));
    if (path === 'workers') return json(paged(options.workers ?? []));
    if (path === 'schedules') return json(paged([]));
    if (path === 'definitions') {
      const definition = (jobName: string) => ({
        jobNamespace: 'billing',
        jobName,
        status: 'active',
        inputTypeName: 'SendInvoice',
        resultTypeName: null,
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
    if (path === 'calendars/sys/office-hours/detail') return json(officeHours(Number(url.searchParams.get('version') ?? '2')));
    if (path === 'calendars/sys/office-hours/preview') return json(officeHoursPreview(Number(url.searchParams.get('version') ?? '2')));
    if (path === 'calendars/sys/office-hours/publish' && request.method() === 'POST') {
      if (!options.controls) return json({ title: 'Controls disabled.' }, 404);
      options.onPublish?.(request.postDataJSON());
      return json({ action: 'applied', version: 3, retimedSchedules: ['billing/send-invoice/nightly'], overdueSchedules: [], reason: null });
    }
    if (path === 'jobs/by-deduplication-key') return json(job);
    if (path === `jobs/${jobRef}/events` || path === 'events') return json(paged([event]));
    // The aggregate the detail page actually reads (JobDetailView in api.ts). Without it the
    // page rendered its title and nothing else, so three tests failed at their first assertion
    // rather than on what they were written to check.
    if (path === `jobs/${jobRef}/detail`) {
      return json({
        job,
        input: { json: { invoiceId: 42 }, formatId: 1, formatName: 'json' },
        result: null,
        checkpoints: { items: [], nextCursor: null, hasMore: false, totalCount: 0 },
        explain: {
          headline: 'The latest attempt failed in the invoice provider.',
          activeWait: null,
          lease: null,
          lastExecutedBy: 'worker-42',
          steps: [],
          stepsHasMore: false,
          reason: 'Invoice provider timed out.',
          nextActions: [{ kind: 'restart', description: 'Run the job again after checking the provider.' }]
        },
        lineage: { ancestors: [], ancestorsHasMore: false, job, steps: [], stepsHasMore: false, activeWait: null, children: [], childrenHasMore: false },
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

test('the Permanent jobs view lists jobs by the permanent kind code', async ({ page }) => {
  await mockDashboard(page, { controls: false });
  await page.goto('/#/jobs?ns=billing');
  const listRequest = page.waitForRequest((request) => {
    const url = new URL(request.url());
    return url.pathname.endsWith('/api/v1/jobs') && url.searchParams.has('kind');
  });

  const nav = page.getByRole('link', { name: 'Permanent jobs' });
  await nav.click();

  expect(new URL((await listRequest).url()).searchParams.get('kind')).toBe('permanent');
  await expect(page.getByRole('heading', { name: 'Permanent jobs' })).toBeVisible();
  await expect(nav).toHaveClass(/active/);
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

test('the workers list says which live worker is not on the namespace\'s deployed version', async ({ page }) => {
  const seen = new Date().toISOString();
  const worker = (workerRef: string, deploymentVersion: string, status: string) => ({
    workerRef,
    status,
    jobNamespace: 'billing',
    hostName: 'host-1',
    deploymentVersion,
    engineVersion: '1.0.0',
    dotnetVersion: '.NET 10.0',
    processId: 4242,
    maxConcurrentExecutors: 8,
    lastSeenAtUtc: seen,
    startedAtUtc: seen
  });
  // One worker on the deployed version, one live on an earlier build, and one of that build already stopped.
  await mockDashboard(page, {
    controls: false,
    workers: [
      worker('wrk_01a1b27e82e87edfb219e2785be47301', '1.6.2+5c0ffee', 'active'),
      worker('wrk_01a1b27e82e87edfb219e2785be47302', '1.6.1+b81d04e', 'active'),
      worker('wrk_01a1b27e82e87edfb219e2785be47303', '1.6.1+b81d04e', 'stopped')
    ]
  });
  await page.goto('/#/workers?ns=billing');

  const note = page.getByText('not current');
  await expect(note).toHaveCount(1);
  await expect(note).toHaveAttribute('title', 'This namespace is deployed at 1.6.2+5c0ffee');
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

test('the outbox panel keeps its actions inside the panel, and a discard says all', async ({ page }) => {
  const discards: unknown[] = [];
  await mockDashboard(page, { controls: true, onDiscard: (body) => discards.push(body) });
  await page.goto('/');

  const panel = page.locator('.panel').filter({ has: page.getByRole('heading', { name: 'Outbox relays' }) });
  const discard = panel.getByRole('button', { name: 'Discard', exact: true });
  // Narrow, the table sizes itself; wide, the Overview's shared column grid applies to the other panels.
  for (const width of [1280, 1800]) {
    await page.setViewportSize({ width, height: 720 });
    const panelBox = (await panel.boundingBox())!;
    const buttonBox = (await discard.boundingBox())!;
    expect(buttonBox.x + buttonBox.width).toBeLessThanOrEqual(panelBox.x + panelBox.width);
  }

  await discard.click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel(/Reason \(optional/).fill('The producer is retired.');
  await dialog.getByLabel(/to confirm/).fill('billing');
  await dialog.getByRole('button', { name: 'Discard all' }).click();

  await expect(page.getByText('Outbox discard accepted; the next relay pass applies it.')).toBeVisible();
  expect(discards).toEqual([{ all: true, reasonMessage: 'The producer is retired.' }]);
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

test('the enqueue form sends lane, concurrency key, and tags as the server would store them', async ({ page }) => {
  let sent: unknown = null;
  await mockDashboard(page, { controls: true, onEnqueue: (body) => { sent = body; } });
  await page.goto('/#/enqueue?ns=billing&jobName=send-invoice');

  await page.getByLabel('Lane').fill(' Reports ');
  await page.getByLabel('Concurrency key').fill('customer:42');
  await page.getByRole('button', { name: 'Add tag' }).click();
  await page.getByLabel('Tag 1 name').fill('Source');
  await page.getByLabel('Tag 1 value').fill('dashboard');
  await page.getByRole('button', { name: 'Add tag' }).click();
  await page.getByLabel('Tag 2 name').fill('review-needed');
  await page.getByRole('button', { name: 'Enqueue job' }).click();

  await expect(page.locator('.control-message')).toContainText('Job enqueued.');
  expect(sent).toMatchObject({
    lane: 'reports',
    concurrencyKey: 'customer:42',
    tags: [
      { name: 'source', value: 'dashboard' },
      { name: 'review-needed', value: null }
    ]
  });
});

test('an ended session shows one banner and sends nothing more until reload', async ({ page }) => {
  let ended = false;
  let sentAfter = 0;
  await mockDashboard(page, {
    controls: true,
    sessionEnded: () => ended,
    onRequest: () => {
      if (ended) sentAfter++;
    }
  });
  await page.goto('/#/jobs');
  await expect(page.getByRole('link', { name: jobRef }).first()).toBeVisible();

  ended = true;
  await page.getByRole('button', { name: 'Refresh now' }).first().click();
  const banner = page.locator('.session-banner');
  await expect(banner).toContainText('Your session has ended. Reload to sign in again.');
  await expect(banner.getByRole('button', { name: 'Reload' })).toBeVisible();
  await expect(page.getByText('Session ended').first()).toBeVisible();
  // Calls already in flight when the first 401 landed may still arrive; none starts after it.
  await page.waitForTimeout(300);
  const first = sentAfter;
  expect(first).toBeGreaterThan(0);

  // Past the query retry delay and after a focus nothing is sent, and the freshness buttons are disabled.
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await page.waitForTimeout(2_500);
  await expect(page.getByRole('button', { name: 'Refresh now' }).first()).toBeDisabled();
  expect(sentAfter).toBe(first);
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
